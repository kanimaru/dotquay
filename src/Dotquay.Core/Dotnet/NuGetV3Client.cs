using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
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
    public NuGetV3Client(string serviceIndexUrl = DefaultServiceIndexUrl, string? username = null, string? password = null, HttpClient? httpClient = null)
    {
        _serviceIndexUrl = serviceIndexUrl;
        _httpClient = httpClient ?? new HttpClient();

        if (username is not null && password is not null)
        {
            var raw = Encoding.UTF8.GetBytes($"{username}:{password}");
            _authHeader = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(raw));
        }
    }

    /// <summary>Whatever order the feed returns — callers that care about ordering, like
    /// <see cref="NuGetVersionSelector"/>, sort explicitly rather than trusting the feed's order.</summary>
    public async Task<IReadOnlyList<string>> GetVersionsAsync(string packageId, CancellationToken cancellationToken = default)
    {
        var baseAddress = await GetPackageBaseAddressAsync(cancellationToken);

        // The flat-container-style resource requires the package ID lowercased in the URL
        // (case-insensitive package IDs, but the storage path itself is not).
        using var response = await SendAsync($"{baseAddress}{packageId.ToLowerInvariant()}/index.json", cancellationToken);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return [];
        }

        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var payload = await JsonSerializer.DeserializeAsync(stream, NuGetV3JsonContext.Default.FlatContainerIndex, cancellationToken);
        return payload?.Versions ?? [];
    }

    public async Task<bool> PackageExistsAsync(string packageId, CancellationToken cancellationToken = default) =>
        (await GetVersionsAsync(packageId, cancellationToken)).Count > 0;

    private async Task<string> GetPackageBaseAddressAsync(CancellationToken cancellationToken)
    {
        if (_packageBaseAddress is not null)
        {
            return _packageBaseAddress;
        }

        using var response = await SendAsync(_serviceIndexUrl, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var index = await JsonSerializer.DeserializeAsync(stream, NuGetV3JsonContext.Default.ServiceIndex, cancellationToken);

        var resource = index?.Resources.FirstOrDefault(r => r.Type == PackageBaseAddressResourceType)
            ?? throw new InvalidOperationException(
                $"NuGet source '{_serviceIndexUrl}' does not advertise a {PackageBaseAddressResourceType} resource "
                + "(not a V3-compliant feed, or the service index is unreachable/misconfigured).");

        _packageBaseAddress = resource.Id.EndsWith('/') ? resource.Id : resource.Id + "/";
        return _packageBaseAddress;
    }

    private async Task<HttpResponseMessage> SendAsync(string url, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (_authHeader is not null)
        {
            request.Headers.Authorization = _authHeader;
        }

        return await _httpClient.SendAsync(request, cancellationToken);
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
