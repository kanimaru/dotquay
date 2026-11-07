using System.Net.Http;
using Dotquay.Core.Abstractions;
using Dotquay.Core.Auth;
using Dotquay.Core.Manifest;

namespace Dotquay.Core.Dotnet;

/// <summary>
/// Real <see cref="INuGetClientFactory"/>: one <see cref="NuGetV3Client"/> per distinct source URL
/// (cached, so a project with several <c>[dotnet]</c> deps doesn't re-discover the same feed's
/// service index for every package), composed into a <see cref="MultiSourceNuGetClient"/> and
/// sharing one <see cref="HttpClient"/> for connection pooling across all of them.
/// </summary>
/// <remarks>
/// Credentials come from <see cref="CredentialResolver"/> — <c>dotquay login</c> or the
/// <c>DOTQUAY_NUGET_*</c> variables, never <c>dotquay.toml</c>, which is meant to be committed and
/// has no business carrying a token (SPEC.md §11.2). nuget.org is always anonymous: there is nothing
/// to authenticate, and sending a private feed's token to a public one would leak it to a party that
/// never needed it.
/// </remarks>
public sealed class NuGetV3ClientFactory : INuGetClientFactory {
    private readonly Dictionary<string, INuGetClient> _clientsBySource = new(StringComparer.OrdinalIgnoreCase);
    private readonly HttpClient _httpClient;
    private readonly CredentialResolver _credentials;

    public NuGetV3ClientFactory(CredentialResolver credentials, HttpClient? httpClient = null) {
        _credentials = credentials;
        _httpClient = httpClient ?? Diagnostics.DiagnosticHttp.CreateClient();
    }

    public INuGetClient Create(NuGetSourceSet sources, string? pinnedSourceName = null) {
        var selected = sources.Resolve(pinnedSourceName);

        return new MultiSourceNuGetClient(
            selected.All.Select(source => (source, ClientFor(source))).ToList());
    }

    private INuGetClient ClientFor(NuGetSourceSpec source) {
        if (_clientsBySource.TryGetValue(source.Url, out var cached)) {
            return cached;
        }

        var lookup = source.IsPublicDefault
            ? CredentialLookup.Anonymous
            : _credentials.ForNuGetSource(source.Name, source.Url);

        var client = new NuGetV3Client(
            source.Url, lookup.Credential?.Username, lookup.Credential?.Token, _httpClient, source.Name);

        _clientsBySource[source.Url] = client;
        return client;
    }
}
