using Dependi.Core.Abstractions;

namespace Dependi.Tests.Support;

/// <summary>Configurable <see cref="INuGetClientFactory"/> test double. By default hands back one
/// shared <see cref="FakeNuGetClient"/> regardless of source (fine for tests that don't care which
/// source was asked for); <see cref="ClientsBySource"/> lets a test configure a distinct fake per
/// source URL to verify <c>[project] nuget_source</c> actually routes to the right one.</summary>
public sealed class FakeNuGetClientFactory : INuGetClientFactory
{
    private readonly FakeNuGetClient _defaultClient;

    public FakeNuGetClientFactory(FakeNuGetClient? defaultClient = null)
    {
        _defaultClient = defaultClient ?? new FakeNuGetClient();
    }

    /// <summary>Keyed by the exact source URL passed to <see cref="Create"/> (nulls normalized to
    /// <c>"(default)"</c>).</summary>
    public Dictionary<string, FakeNuGetClient> ClientsBySource { get; } = new(StringComparer.OrdinalIgnoreCase);

    public List<string?> RequestedSources { get; } = [];

    public INuGetClient Create(string? sourceUrl)
    {
        RequestedSources.Add(sourceUrl);

        if (sourceUrl is not null && ClientsBySource.TryGetValue(sourceUrl, out var configured))
        {
            return configured;
        }

        return _defaultClient;
    }
}
