using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Dotquay.Core.Auth;
using Dotquay.Core.Workflows;

namespace Dotquay.Core.SelfUpdate;

/// <summary>
/// The shipping <see cref="IReleaseChannel"/>: a GitLab project's Releases API plus the per-version
/// manifest CI uploads to its generic package registry.
/// </summary>
/// <remarks>
/// <para>Two requests per answer, by design. The releases endpoint says what the newest tag is and
/// carries the asset links; the manifest linked from it says which file is for which RID and what it
/// should hash to. Reading checksums from separate <c>.sha256</c> links would be a third and fourth
/// request and would make "the binary and its checksum disagree about which release they belong to"
/// possible; one signed-off document per version cannot disagree with itself.</para>
/// <para>Speaks HTTP directly with source-generated JSON for the same reason
/// <see cref="Dotnet.NuGetV3Client"/> does (SPEC.md §12): a reflection-based client library is a
/// NativeAOT failure waiting for its first published build.</para>
/// </remarks>
public sealed class GitLabReleaseChannel : IReleaseChannel {
    /// <summary>The asset link CI attaches to every release. Named, not positional — a release's
    /// links are ordered by whoever wrote the pipeline.</summary>
    public const string ManifestLinkName = "manifest.json";

    private readonly HttpClient _httpClient;
    private readonly string _projectApiUrl;
    private readonly AuthenticationHeaderValue? _authHeader;
    private readonly string? _privateToken;
    private readonly string _describe;

    /// <summary>Whether requests still carry the stored credential. Cleared for the rest of this
    /// instance's life the first time GitLab refuses it on a read anonymous access can answer.</summary>
    private bool _credentialActive;

    /// <param name="projectApiUrl">The project's API root, e.g.
    /// <c>https://gitlab.example.com/api/v4/projects/128</c>. The numeric ID, like every other
    /// GitLab endpoint dotquay talks to (see reference_gitlab_api_status_codes).</param>
    /// <param name="credentials">Resolved through the same stack as every other host dotquay
    /// authenticates to, so someone already logged in for the private NuGet feed is already logged
    /// in for updates. Null resolves to anonymous, which is correct for a public channel.</param>
    public GitLabReleaseChannel(string projectApiUrl, CredentialResolver? credentials = null, HttpClient? httpClient = null) {
        _projectApiUrl = projectApiUrl.TrimEnd('/');
        _httpClient = httpClient ?? Diagnostics.DiagnosticHttp.CreateClient();

        var credential = credentials?.ForGitRepository(_projectApiUrl).Credential;
        if (credential is not null) {
            // Both headers, on purpose. GitLab's two halves want different things: the v4 REST API
            // (where /releases lives) authenticates a personal access token through PRIVATE-TOKEN
            // and does not accept HTTP Basic, while the generic package registry the assets are
            // served from does accept Basic - which is the same credential dotquay already stores
            // for that host's NuGet feed. Sending one would work for exactly one of the two requests
            // every update makes, and which one would depend on where GitLab chose to host the file.
            var raw = Encoding.UTF8.GetBytes($"{credential.Username}:{credential.Token}");
            _authHeader = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(raw));
            _privateToken = credential.Token;
            _credentialActive = true;
        }

        _describe = Uri.TryCreate(_projectApiUrl, UriKind.Absolute, out var uri) ? uri.Host : _projectApiUrl;
    }

    public string Describe => _describe;

    /// <summary>
    /// True once GitLab refused the stored credential and the same request then succeeded without
    /// it.
    /// </summary>
    /// <remarks>
    /// Exposed so a caller can say so: the update itself went through, but the credential that
    /// was refused is the same one git and NuGet will offer this host, where there may be no
    /// anonymous fallback to hide behind.
    /// </remarks>
    public bool StoredCredentialRejected { get; private set; }

    public async Task<ReleaseManifest?> GetLatestAsync(CancellationToken cancellationToken = default) {
        // per_page=1 rather than /releases/permalink/latest: the permalink endpoint resolves by
        // semantic version and 404s on a project whose newest release is a prerelease, which is a
        // state this project's auto-tagging can produce.
        var releases = await ReadAsync(
            $"{_projectApiUrl}/releases?per_page=1",
            SelfUpdateJsonContext.Default.ListGitLabRelease,
            "looking up the newest release",
            cancellationToken);

        var release = releases?.FirstOrDefault();
        return release is null ? null : await ResolveAsync(release, cancellationToken);
    }

    public async Task<ReleaseManifest?> GetVersionAsync(string version, CancellationToken cancellationToken = default) {
        var tag = version.StartsWith('v') ? version : $"v{version}";

        var release = await ReadAsync(
            $"{_projectApiUrl}/releases/{Uri.EscapeDataString(tag)}",
            SelfUpdateJsonContext.Default.GitLabRelease,
            $"looking up release {tag}",
            cancellationToken,
            nullOnNotFound: true);

        return release is null ? null : await ResolveAsync(release, cancellationToken);
    }

    public async Task<byte[]> DownloadAsync(ReleaseAsset asset, CancellationToken cancellationToken = default) {
        var (response, authenticated) = await SendAsync(asset.Url, $"downloading {asset.FileName}", cancellationToken);
        using var _ = response;
        await ThrowIfUnsuccessfulAsync(response, authenticated, $"downloading {asset.FileName}", cancellationToken);

        return await response.Content.ReadAsByteArrayAsync(cancellationToken);
    }

    /// <summary>Turns a release plus its manifest link into the channel-neutral shape. A release
    /// with no manifest is reported as having no assets, which the workflow refuses by name — that
    /// is a release published by a pipeline older than v0.17, and saying so beats "no build for your
    /// platform".</summary>
    private async Task<ReleaseManifest> ResolveAsync(GitLabRelease release, CancellationToken cancellationToken) {
        var version = release.TagName.TrimStart('v', 'V');
        var manifestUrl = release.Assets?.Links
            .FirstOrDefault(link => string.Equals(link.Name, ManifestLinkName, StringComparison.OrdinalIgnoreCase))
            ?.Url;

        if (manifestUrl is null) {
            return new ReleaseManifest(version, release.ReleasedAt, []);
        }

        var document = await ReadAsync(
            manifestUrl,
            SelfUpdateJsonContext.Default.ReleaseManifestDocument,
            $"reading the manifest for {release.TagName}",
            cancellationToken);

        if (document is null) {
            return new ReleaseManifest(version, release.ReleasedAt, []);
        }

        if (document.Schema > 1) {
            throw new WorkflowException(
                $"Release {release.TagName} publishes a manifest with schema {document.Schema}, and this dotquay "
                + "understands 1. Update by hand this once; a newer dotquay will understand it.");
        }

        var assets = document.Assets
            .Select(asset => new ReleaseAsset(asset.Rid, asset.FileName, asset.Url, Normalize(asset.Sha256), asset.Size))
            .ToList();

        // The manifest's own version wins over the tag: the tag is how the release was named, the
        // manifest is what the binary inside it reports, and a mismatch between the two is exactly
        // what the post-download --version check exists to catch.
        return new ReleaseManifest(
            document.Version.Length > 0 ? document.Version : version,
            document.ReleasedAt ?? release.ReleasedAt,
            assets);
    }

    private static string? Normalize(string? sha256) =>
        string.IsNullOrWhiteSpace(sha256) ? null : sha256.Trim().ToLowerInvariant();

    private async Task<T?> ReadAsync<T>(
        string url,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo,
        string what,
        CancellationToken cancellationToken,
        bool nullOnNotFound = false) {
        var (response, authenticated) = await SendAsync(url, what, cancellationToken);
        using var _ = response;

        if (nullOnNotFound && response.StatusCode == HttpStatusCode.NotFound && authenticated) {
            // Only with a credential. Without one, GitLab answers 404 for "no such release" and
            // "you cannot see this project" identically, and silently reporting the second as the
            // first is how someone spends an afternoon on a missing token.
            return default;
        }

        await ThrowIfUnsuccessfulAsync(response, authenticated, what, cancellationToken);

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonSerializer.DeserializeAsync(stream, typeInfo, cancellationToken);
    }

    /// <summary>
    /// Sends a GET, falling back to anonymous once when GitLab refuses the stored credential.
    /// </summary>
    /// <remarks>
    /// <para>Found on a second developer's PC: <c>dotquay login gitlab.kemomi.de</c> stores a token
    /// for a bare host without verifying it (there is no URL to check), so a mistyped or expired one
    /// is stored silently. GitLab answers a request carrying an unrecognised token with 401 even on a
    /// project anyone may read anonymously, so that stale token turned a public channel into a
    /// failure. Measured 2026-10-20 against project 128: anonymous 200, bogus PRIVATE-TOKEN 401.</para>
    /// <para>401 and 403 both retry: 401 is a token GitLab does not recognise, 403 a real one lacking a
    /// permission (a fine-grained token without <c>Release: Read</c>) - and on a public project
    /// anonymous access needs neither. Anything else is returned as is: a 404 or a 500 is not about
    /// the credential, and retrying it anonymously would only turn one answer into two.</para>
    /// <para>When the anonymous attempt fails too, the <em>original</em> response is what gets
    /// reported. The channel genuinely wants a login, and the refused credential is the thing to fix -
    /// an anonymous 404 would point at the URL instead.</para>
    /// </remarks>
    private async Task<(HttpResponseMessage Response, bool Authenticated)> SendAsync(
        string url, string what, CancellationToken cancellationToken) {
        if (!_credentialActive) {
            return (await SendOnceAsync(url, what, withCredential: false, cancellationToken), false);
        }

        var response = await SendOnceAsync(url, what, withCredential: true, cancellationToken);
        if (response.StatusCode is not (HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)) {
            return (response, true);
        }

        var anonymous = await SendOnceAsync(url, what, withCredential: false, cancellationToken);
        if (!anonymous.IsSuccessStatusCode) {
            anonymous.Dispose();
            return (response, true);
        }

        response.Dispose();

        // For the rest of this instance: an update is three requests (release, manifest, binary), and
        // offering a credential already refused would cost a doubled round trip on each.
        _credentialActive = false;
        StoredCredentialRejected = true;
        return (anonymous, false);
    }

    private async Task<HttpResponseMessage> SendOnceAsync(
        string url, string what, bool withCredential, CancellationToken cancellationToken) {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (withCredential && _authHeader is not null) {
            request.Headers.Authorization = _authHeader;
        }

        if (withCredential && _privateToken is not null) {
            request.Headers.Add("PRIVATE-TOKEN", _privateToken);
        }

        try {
            return await _httpClient.SendAsync(request, cancellationToken);
        } catch (HttpRequestException ex) {
            throw new WorkflowException(
                $"The update channel ({_describe}) could not be reached while {what}: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Reports a refusal in GitLab's own words when it gave any.
    /// </summary>
    /// <remarks>
    /// Reading the body is the whole point. A fine-grained personal access token names the exact
    /// permission it is missing, and a client that throws that away can only offer a guess about
    /// scopes - which is worse than saying nothing, because the guess sounds authoritative.
    /// See <see cref="GitLabErrorExplanation"/> for the rest of the reasoning.
    /// </remarks>

    /// <param name="authenticated">Whether the request that produced <paramref name="response"/>
    /// carried the credential - not whether one is configured. After a fallback they differ, and the
    /// advice for each is different.</param>
    private async Task ThrowIfUnsuccessfulAsync(
        HttpResponseMessage response, bool authenticated, string what, CancellationToken cancellationToken) {
        if (response.IsSuccessStatusCode) {
            return;
        }

        var message = GitLabErrorExplanation.Describe(
            response.StatusCode,
            await ReadErrorDetailAsync(response, cancellationToken),
            authenticated,
            _describe,
            what);

        throw GitLabErrorExplanation.IsAuthFailure(response.StatusCode, authenticated)
            ? new WorkflowException(message, new CredentialException(message))
            : new WorkflowException(message);
    }

    /// <summary>GitLab's <c>error_description</c> or <c>message</c>, or null when the body is not one
    /// of those shapes. Every failure here is null: an error body that cannot be parsed must not
    /// replace the error it was describing.</summary>
    private static async Task<string?> ReadErrorDetailAsync(HttpResponseMessage response, CancellationToken cancellationToken) {
        try {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (body.Length is 0 or > 8192) {
                return null;
            }

            var error = JsonSerializer.Deserialize(body, SelfUpdateJsonContext.Default.GitLabApiError);
            return error?.ErrorDescription ?? error?.Message;
        } catch (Exception ex) when (ex is JsonException or HttpRequestException or IOException or NotSupportedException) {
            return null;
        }
    }
}
