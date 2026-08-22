using System.Net.Http;
using Dependi.Core.Abstractions;

namespace Dependi.Core.Dotnet;

/// <summary>
/// Real <see cref="INuGetClientFactory"/>: builds a <see cref="NuGetV3Client"/> per distinct source
/// URL (caching so a project with several <c>[dotnet]</c> deps doesn't re-discover the same source's
/// service index for every package), sharing one underlying <see cref="HttpClient"/> for connection
/// pooling across all of them.
/// </summary>
/// <remarks>
/// Credentials are read via <see cref="NuGetCredentials"/> (<c>DEPENDI_NUGET_USERNAME</c>/
/// <c>DEPENDI_NUGET_TOKEN</c>), and only applied for a non-default source — nuget.org itself is
/// always anonymous. This is deliberately not read from <c>dependi.toml</c>: that file is meant to be
/// committed, and a private feed's token has no business living in git history (SPEC.md §11).
/// <see cref="DotnetInstaller"/> reads the same two variables (via the same helper) to authenticate
/// the separate <c>dotnet restore</c> process against the identical source.
/// </remarks>
public sealed class NuGetV3ClientFactory : INuGetClientFactory
{
    private readonly Dictionary<string, INuGetClient> _clientsBySource = new(StringComparer.OrdinalIgnoreCase);
    private readonly HttpClient _httpClient;

    public NuGetV3ClientFactory(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient();
    }

    public INuGetClient Create(string? sourceUrl)
    {
        var effectiveUrl = string.IsNullOrWhiteSpace(sourceUrl) ? NuGetV3Client.DefaultServiceIndexUrl : sourceUrl;

        if (_clientsBySource.TryGetValue(effectiveUrl, out var cached))
        {
            return cached;
        }

        var isDefaultSource = string.Equals(effectiveUrl, NuGetV3Client.DefaultServiceIndexUrl, StringComparison.OrdinalIgnoreCase);
        var (username, token) = isDefaultSource ? (null, null) : NuGetCredentials.FromEnvironment();

        var client = new NuGetV3Client(effectiveUrl, username, token, _httpClient);
        _clientsBySource[effectiveUrl] = client;
        return client;
    }
}
