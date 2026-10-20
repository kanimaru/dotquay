using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml.Linq;
using Dotquay.Core.Abstractions;

namespace Dotquay.Core.Dotnet;

/// <summary>
/// Real <see cref="INuGetClient"/> that speaks the standard NuGet V3 protocol directly via
/// <see cref="HttpClient"/> and source-generated <c>System.Text.Json</c> — deliberately NOT the
/// official <c>NuGet.Protocol</c> library SPEC.md §11 originally called for.
/// </summary>
/// <remarks>
/// <para>
/// Confirmed via a real NativeAOT publish (docs/SPRINT-01.md, 2026-08-21): even a simple read-only
/// "list versions" query crashes under <c>NuGet.Protocol</c>, because it always tries to fetch
/// repository signature metadata first, and that path deserializes
/// <c>NuGet.Protocol.RepositoryCertificateInfo</c> through Newtonsoft.Json's reflection-based
/// object binder — which the trimmer strips. Same shape of failure, same fix, as the
/// Spectre.Console.Cli swap earlier this project.
/// </para>
/// <para>
/// Every V3-compliant source — nuget.org included — publishes a service index (this class's
/// <paramref name="serviceIndexUrl"/>, e.g. <c>https://api.nuget.org/v3/index.json</c> or a private
/// feed's equivalent, such as GitLab's
/// <c>https://gitlab.example.com/api/v4/projects/&lt;id&gt;/packages/nuget/index.json</c>) listing
/// its actual resource URLs by <c>@type</c>. This client discovers the <c>PackageBaseAddress/3.0.0</c>
/// resource from that index rather than hardcoding nuget.org's own fixed flat-container URL, so the
/// exact same code path works for private feeds too (confirmed against a real GitLab package
/// registry, 2026-08-21) — no special-casing per provider.
/// </para>
/// </remarks>
public sealed class NuGetV3Client : INuGetClient
{
    public const string DefaultServiceIndexUrl = "https://api.nuget.org/v3/index.json";

    private const string PackageBaseAddressResourceType = "PackageBaseAddress/3.0.0";

    private readonly HttpClient _httpClient;
    private readonly string _serviceIndexUrl;
    private readonly string _sourceName;
    private readonly bool _isPublicDefault;
    private readonly AuthenticationHeaderValue? _authHeader;
    private string? _packageBaseAddress;

    /// <param name="serviceIndexUrl">The source's V3 <c>index.json</c>. Defaults to nuget.org.</param>
    /// <param name="username">Basic-auth username for a private feed, if any.</param>
    /// <param name="password">Basic-auth password/token for a private feed, if any. GitLab (and
    /// most private feeds) accept a personal access token here.</param>
    /// <param name="httpClient">Reuse an existing client if the caller has one (recommended .NET
    /// guidance is to not create-and-dispose an <see cref="HttpClient"/> per call — and doing so
    /// here specifically would matter, since a shared client can serve several
    /// <see cref="NuGetV3Client"/> instances with different credentials; the auth header is applied
    /// per-request, never as the shared client's <c>DefaultRequestHeaders</c>, so that's safe).
    /// Otherwise this creates its own and — deliberately, matching the process-lifetime CLI tools
    /// this ships as — never disposes it.</param>
    /// <param name="sourceName">The declared <c>[nuget.sources.*]</c> name, carried so a version
    /// query can say which feed answered (for the lockfile) and an auth failure can name the feed to
    /// log in to (rather than making the user match a URL by eye).</param>
    public NuGetV3Client(
        string serviceIndexUrl = DefaultServiceIndexUrl,
        string? username = null,
        string? password = null,
        HttpClient? httpClient = null,
        string? sourceName = null)
    {
        _serviceIndexUrl = serviceIndexUrl;
        _httpClient = httpClient ?? Diagnostics.DiagnosticHttp.CreateClient();
        _isPublicDefault = string.Equals(serviceIndexUrl, DefaultServiceIndexUrl, StringComparison.OrdinalIgnoreCase);
        _sourceName = sourceName ?? (_isPublicDefault ? Manifest.NuGetSourceSpec.PublicName : serviceIndexUrl);

        if (username is not null && password is not null)
        {
            var raw = Encoding.UTF8.GetBytes($"{username}:{password}");
            _authHeader = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(raw));
        }
    }

    /// <summary>Whatever order the feed returns — callers that care about ordering, like
    /// <see cref="NuGetVersionSelector"/>, sort explicitly rather than trusting the feed's order.</summary>
    public async Task<NuGetVersionQuery> GetVersionsAsync(string packageId, CancellationToken cancellationToken = default)
    {
        var baseAddress = await GetPackageBaseAddressAsync(cancellationToken);

        // The flat-container-style resource requires the package ID lowercased in the URL
        // (case-insensitive package IDs, but the storage path itself is not).
        using var response = await SendAsync($"{baseAddress}{packageId.ToLowerInvariant()}/index.json", cancellationToken);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return new NuGetVersionQuery([], ReportedSourceName);
        }

        ThrowIfUnauthorized(response, $"looking up '{packageId}'");
        ThrowIfUnsuccessful(response, $"looking up '{packageId}'");

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var payload = await JsonSerializer.DeserializeAsync(stream, NuGetV3JsonContext.Default.FlatContainerIndex, cancellationToken);
        return new NuGetVersionQuery(payload?.Versions ?? [], ReportedSourceName);
    }

    public async Task<bool> PackageExistsAsync(string packageId, CancellationToken cancellationToken = default) =>
        (await GetVersionsAsync(packageId, cancellationToken)).Found;

    /// <summary>Discovering the base address is exactly the check: it fetches the service index and
    /// throws unless the response is a V3 feed advertising a package resource. The discovered address
    /// is cached, so a later query on the same client costs nothing extra.</summary>
    public async Task VerifySourceAsync(CancellationToken cancellationToken = default) =>
        await GetPackageBaseAddressAsync(cancellationToken);

    /// <summary>
    /// Fetches <c>{id}/{version}/{id}.nuspec</c> from the package resource and reads
    /// <c>developmentDependency</c> out of it.
    /// </summary>
    /// <remarks>
    /// <para>Every failure short of a broken feed answers <see cref="PackageShape.Unknown"/>. A missing
    /// or malformed nuspec means dotquay has no signal, and the only thing it drives is whether an
    /// optional question gets asked — so it must never be the reason an otherwise-fine <c>add</c>
    /// fails. An unauthorized response is the exception, and is left to throw: a feed that has stopped
    /// accepting the credential is a real problem the caller is already handling for every other query.
    /// </para>
    /// <para>Parsed with LINQ-to-XML on local names only. A nuspec carries a default xmlns whose
    /// version has changed several times (2010/07, 2011/10, 2013/05 all appear in the wild, and the same
    /// package repacked on a newer SDK changes it), so matching qualified names would read a perfectly
    /// good document as empty. No reflection either way, which keeps this NativeAOT-safe.</para>
    /// </remarks>
    public async Task<PackageShape> GetPackageShapeAsync(
        string packageId, string version, CancellationToken cancellationToken = default)
    {
        var baseAddress = await GetPackageBaseAddressAsync(cancellationToken);
        var id = packageId.ToLowerInvariant();

        using var response = await SendAsync(
            $"{baseAddress}{id}/{version.ToLowerInvariant()}/{id}.nuspec", cancellationToken);

        ThrowIfUnauthorized(response, $"reading the nuspec for '{packageId}'");

        if (!response.IsSuccessStatusCode)
        {
            return PackageShape.Unknown;
        }

        try
        {
            var document = XDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));

            var declared = document.Descendants()
                .FirstOrDefault(element => element.Name.LocalName == "developmentDependency")?.Value.Trim();

            return declared is null
                ? new PackageShape(IsDevelopmentDependency: false)
                : new PackageShape(string.Equals(declared, "true", StringComparison.OrdinalIgnoreCase));
        }
        catch (System.Xml.XmlException)
        {
            return PackageShape.Unknown;
        }
    }

    /// <summary>Null for nuget.org, matching the lockfile's convention of omitting the key for the
    /// public feed — so a public-only project's lockfile stays byte-identical to pre-v0.7.</summary>
    private string? ReportedSourceName => _isPublicDefault ? null : _sourceName;

    private async Task<string> GetPackageBaseAddressAsync(CancellationToken cancellationToken)
    {
        if (_packageBaseAddress is not null)
        {
            return _packageBaseAddress;
        }

        using var response = await SendAsync(_serviceIndexUrl, cancellationToken);

        // Checked here as well as on the package query, and this is the one that usually fires: on a
        // private feed a bad or missing credential is rejected at the service index, before any
        // package lookup happens at all.
        ThrowIfUnauthorized(response, "fetching the service index");
        ThrowIfUnsuccessful(response, "fetching the service index");

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);

        ServiceIndex? index;
        try
        {
            index = await JsonSerializer.DeserializeAsync(stream, NuGetV3JsonContext.Default.ServiceIndex, cancellationToken);
        }
        catch (JsonException ex)
        {
            // A sign-in page, an HTML error, a proxy's interstitial — anything that answered 200 with
            // something that isn't a service index. Typed, because an unhandled JsonException would
            // reach the user as a stack trace and, under --json, as no envelope at all.
            throw new NuGetSourceException(
                _sourceName, _serviceIndexUrl,
                $"NuGet source '{_sourceName}' ({_serviceIndexUrl}) did not return a valid V3 service index. "
                + "Check the URL — a page that requires sign-in will often answer 200 with HTML.", ex);
        }

        var resource = index?.Resources.FirstOrDefault(r => r.Type == PackageBaseAddressResourceType)
            ?? throw new NuGetSourceException(
                _sourceName,
                _serviceIndexUrl,
                $"NuGet source '{_sourceName}' ({_serviceIndexUrl}) does not advertise a "
                + $"{PackageBaseAddressResourceType} resource, so it isn't a usable V3 feed. Check the URL: a GitLab "
                + "NuGet feed is /api/v4/projects/<id>/packages/nuget/index.json, or "
                + "/api/v4/groups/<id>/-/packages/nuget/index.json for a group (GitLab 17.4+).");

        _packageBaseAddress = resource.Id.EndsWith('/') ? resource.Id : resource.Id + "/";
        return _packageBaseAddress;
    }

    /// <summary>
    /// Turns a 401/403 into a typed <see cref="NuGetAuthenticationException"/>.
    /// </summary>
    /// <remarks>
    /// Not merely a nicer message. <see cref="MultiSourceNuGetClient"/> has to tell "not on this
    /// feed, try the next" (404) from "this feed won't say" (401), because falling through on a 401
    /// to a public feed is how a private package gets substituted by a same-named public one
    /// (SPEC.md §11.1). <see cref="HttpRequestException"/> carries the status code, but branching on
    /// it from the aggregator would mean reaching into this class's HTTP details from outside.
    /// </remarks>
    private void ThrowIfUnauthorized(HttpResponseMessage response, string what)
    {
        if (response.StatusCode is not (HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden))
        {
            return;
        }

        var hint = _authHeader is null
            ? $"No credentials were sent. Run 'dotquay login {_serviceIndexUrl}'."
            : "The credentials sent were rejected. Check them with 'dotquay login --list', or log in again.";

        throw new NuGetAuthenticationException(
            _sourceName,
            _serviceIndexUrl,
            $"NuGet source '{_sourceName}' answered {(int)response.StatusCode} {response.StatusCode} while {what}. {hint}");
    }

    /// <summary>
    /// Turns any other unsuccessful status into a typed <see cref="NuGetSourceException"/>.
    /// </summary>
    /// <remarks>
    /// <para>Replaces <c>EnsureSuccessStatusCode()</c>, whose <see cref="HttpRequestException"/>
    /// nothing caught — it escaped as an unhandled exception, printing a stack trace and, under
    /// <c>--json</c>, producing no envelope at all.</para>
    /// <para>A 404 is genuinely ambiguous on GitLab, which returns it both for a project that does
    /// not exist and for one the caller may not see — confirmed empirically against a self-hosted
    /// instance, where a project known to exist and a name known not to exist were byte-identical
    /// 404s when unauthenticated, and matching GitLab's documented practice of denying with a
    /// generic 404 rather than disclosing existence. The hint therefore branches on whether a
    /// credential was actually sent, because that is what decides which reading is plausible:
    /// unauthenticated, "log in" is worth trying; authenticated, the credential was accepted this
    /// far and the URL is the likelier fault.</para>
    /// </remarks>
    private void ThrowIfUnsuccessful(HttpResponseMessage response, string what)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var hint = response.StatusCode switch
        {
            HttpStatusCode.NotFound when _authHeader is null =>
                "GitLab returns 404 both for a project that doesn't exist and for one you aren't allowed to see, "
                + $"so this could be either. Check the URL, then try 'dotquay login {_serviceIndexUrl}'.",

            HttpStatusCode.NotFound =>
                "A credential was sent and accepted this far, so the URL is the likelier fault: a GitLab NuGet feed "
                + "needs the project's numeric ID (/api/v4/projects/<id>/packages/nuget/index.json), not its path. "
                + "Failing that, the token may lack the read_package_registry scope.",

            HttpStatusCode.BadRequest =>
                "Check the URL shape: a GitLab NuGet feed needs the project's numeric ID "
                + "(/api/v4/projects/<id>/packages/nuget/index.json), and rejects a URL-encoded project path.",

            _ => "The feed may be down or misconfigured.",
        };

        throw new NuGetSourceException(
            _sourceName,
            _serviceIndexUrl,
            $"NuGet source '{_sourceName}' answered {(int)response.StatusCode} {response.StatusCode} while {what}. {hint}");
    }

    private async Task<HttpResponseMessage> SendAsync(string url, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (_authHeader is not null)
        {
            request.Headers.Authorization = _authHeader;
        }

        try
        {
            return await _httpClient.SendAsync(request, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            // DNS failure, refused connection, TLS problem. Same reasoning as the status check: a
            // transport error must not reach the user as an unhandled exception.
            throw new NuGetSourceException(
                _sourceName, _serviceIndexUrl,
                $"NuGet source '{_sourceName}' ({_serviceIndexUrl}) could not be reached: {ex.Message}", ex);
        }
    }
}

/// <summary>The subset of a V3 service index (<c>index.json</c>) this client needs: just enough of
/// the <c>resources</c> array to find the <c>PackageBaseAddress/3.0.0</c> entry.</summary>
internal sealed class ServiceIndex
{
    [JsonPropertyName("resources")]
    public List<ServiceIndexResource> Resources { get; set; } = [];
}

internal sealed class ServiceIndexResource
{
    [JsonPropertyName("@id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("@type")]
    public string Type { get; set; } = "";
}

/// <summary>The flat-container-style version index's only shape: <c>{"versions": ["1.0.0", ...]}</c>.</summary>
internal sealed class FlatContainerIndex
{
    [JsonPropertyName("versions")]
    public List<string> Versions { get; set; } = [];
}

/// <summary>Source-generated <c>System.Text.Json</c> context — no reflection-based serialization
/// anywhere in this client, which is the entire point of not using <c>NuGet.Protocol</c> here.</summary>
[JsonSerializable(typeof(ServiceIndex))]
[JsonSerializable(typeof(FlatContainerIndex))]
internal sealed partial class NuGetV3JsonContext : JsonSerializerContext
{
}
