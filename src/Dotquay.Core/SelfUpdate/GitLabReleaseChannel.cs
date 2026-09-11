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
public sealed class GitLabReleaseChannel : IReleaseChannel
{
    /// <summary>The asset link CI attaches to every release. Named, not positional — a release's
    /// links are ordered by whoever wrote the pipeline.</summary>
    public const string ManifestLinkName = "manifest.json";

    private readonly HttpClient _httpClient;
    private readonly string _projectApiUrl;
    private readonly AuthenticationHeaderValue? _authHeader;
    private readonly string? _privateToken;
    private readonly string _describe;

    /// <param name="projectApiUrl">The project's API root, e.g.
    /// <c>https://gitlab.example.com/api/v4/projects/128</c>. The numeric ID, like every other
    /// GitLab endpoint dotquay talks to (see reference_gitlab_api_status_codes).</param>
    /// <param name="credentials">Resolved through the same stack as every other host dotquay
    /// authenticates to, so someone already logged in for the private NuGet feed is already logged
    /// in for updates. Null resolves to anonymous, which is correct for a public channel.</param>
    public GitLabReleaseChannel(string projectApiUrl, CredentialResolver? credentials = null, HttpClient? httpClient = null)
    {
        _projectApiUrl = projectApiUrl.TrimEnd('/');
        _httpClient = httpClient ?? new HttpClient();

        var credential = credentials?.ForGitRepository(_projectApiUrl).Credential;
        if (credential is not null)
        {
            // Both headers, on purpose. GitLab's two halves want different things: the v4 REST API
            // (where /releases lives) authenticates a personal access token through PRIVATE-TOKEN
            // and does not accept HTTP Basic, while the generic package registry the assets are
            // served from does accept Basic - which is the same credential dotquay already stores
            // for that host's NuGet feed. Sending one would work for exactly one of the two requests
            // every update makes, and which one would depend on where GitLab chose to host the file.
            var raw = Encoding.UTF8.GetBytes($"{credential.Username}:{credential.Token}");
            _authHeader = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(raw));
            _privateToken = credential.Token;
        }

        _describe = Uri.TryCreate(_projectApiUrl, UriKind.Absolute, out var uri) ? uri.Host : _projectApiUrl;
    }

    public string Describe => _describe;

    public async Task<ReleaseManifest?> GetLatestAsync(CancellationToken cancellationToken = default)
    {
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

    public async Task<ReleaseManifest?> GetVersionAsync(string version, CancellationToken cancellationToken = default)
    {
        var tag = version.StartsWith('v') ? version : $"v{version}";

        var release = await ReadAsync(
            $"{_projectApiUrl}/releases/{Uri.EscapeDataString(tag)}",
            SelfUpdateJsonContext.Default.GitLabRelease,
            $"looking up release {tag}",
            cancellationToken,
            nullOnNotFound: true);

        return release is null ? null : await ResolveAsync(release, cancellationToken);
    }

    public async Task<byte[]> DownloadAsync(ReleaseAsset asset, CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(asset.Url, $"downloading {asset.FileName}", cancellationToken);
        ThrowIfUnsuccessful(response, $"downloading {asset.FileName}");

        return await response.Content.ReadAsByteArrayAsync(cancellationToken);
    }

    /// <summary>Turns a release plus its manifest link into the channel-neutral shape. A release
    /// with no manifest is reported as having no assets, which the workflow refuses by name — that
    /// is a release published by a pipeline older than v0.17, and saying so beats "no build for your
    /// platform".</summary>
    private async Task<ReleaseManifest> ResolveAsync(GitLabRelease release, CancellationToken cancellationToken)
    {
        var version = release.TagName.TrimStart('v', 'V');
        var manifestUrl = release.Assets?.Links
            .FirstOrDefault(link => string.Equals(link.Name, ManifestLinkName, StringComparison.OrdinalIgnoreCase))
            ?.Url;

        if (manifestUrl is null)
        {
            return new ReleaseManifest(version, release.ReleasedAt, []);
        }

        var document = await ReadAsync(
            manifestUrl,
            SelfUpdateJsonContext.Default.ReleaseManifestDocument,
            $"reading the manifest for {release.TagName}",
            cancellationToken);

        if (document is null)
        {
            return new ReleaseManifest(version, release.ReleasedAt, []);
        }

        if (document.Schema > 1)
        {
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
        bool nullOnNotFound = false)
    {
        using var response = await SendAsync(url, what, cancellationToken);

        if (nullOnNotFound && response.StatusCode == HttpStatusCode.NotFound && _authHeader is not null)
        {
            // Only with a credential. Without one, GitLab answers 404 for "no such release" and
            // "you cannot see this project" identically, and silently reporting the second as the
            // first is how someone spends an afternoon on a missing token.
            return default;
        }

        ThrowIfUnsuccessful(response, what);

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonSerializer.DeserializeAsync(stream, typeInfo, cancellationToken);
    }

    private async Task<HttpResponseMessage> SendAsync(string url, string what, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (_authHeader is not null)
        {
            request.Headers.Authorization = _authHeader;
        }

        if (_privateToken is not null)
        {
            request.Headers.Add("PRIVATE-TOKEN", _privateToken);
        }

        try
        {
            return await _httpClient.SendAsync(request, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            throw new WorkflowException(
                $"The update channel ({_describe}) could not be reached while {what}: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Branches on whether a credential was sent, not on the status code alone.
    /// </summary>
    /// <remarks>
    /// Measured against gitlab.kemomi.de (reference_gitlab_api_status_codes): an unauthenticated 404
    /// on a project endpoint is ambiguous between "does not exist" and "not allowed to see", because
    /// GitLab denies both identically. With a valid credential the ambiguity is gone and 404 means
    /// the URL is wrong — telling that user to log in would be advice that cannot work.
    /// </remarks>
    private void ThrowIfUnsuccessful(HttpResponseMessage response, string what)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var status = $"{(int)response.StatusCode} {response.StatusCode}";

        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
            || (response.StatusCode == HttpStatusCode.NotFound && _authHeader is null))
        {
            var message = _authHeader is null
                ? $"The update channel ({_describe}) answered {status} while {what}. It is a private project, so "
                  + $"this is most likely a missing credential: run 'dotquay login {_describe}'."
                : $"The update channel ({_describe}) answered {status} while {what}. The stored credential was sent "
                  + "and refused - it may have expired, or it may lack read_api scope.";

            throw new WorkflowException(message, new CredentialException(message));
        }

        throw new WorkflowException(
            $"The update channel ({_describe}) answered {status} while {what}."
            + (response.StatusCode == HttpStatusCode.NotFound
                ? " A credential was accepted this far, so the channel URL is the likelier fault: it needs the"
                  + " project's numeric ID, as in /api/v4/projects/<id>."
                : string.Empty));
    }
}
