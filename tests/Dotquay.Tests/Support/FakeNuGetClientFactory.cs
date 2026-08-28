using Dotquay.Core.Abstractions;
using Dotquay.Core.Dotnet;

namespace Dotquay.Tests.Support;

/// <summary>
/// Configurable <see cref="INuGetClientFactory"/> test double.
/// </summary>
/// <remarks>
/// Keyed on source <em>name</em> since v0.7, not on a single URL. The pre-v0.7 shape — one URL in,
/// one client out — could only express "this project has a feed"; the questions worth asking now are
/// "which feeds were consulted, in what order" and "did the pin narrow the search", and a URL-keyed
/// fake can't answer either. Records both, so a test asserts on routing rather than on a client
/// having been handed back at all.
/// </remarks>
public sealed class FakeNuGetClientFactory : INuGetClientFactory
{
    private readonly FakeNuGetClient _defaultClient;

    public FakeNuGetClientFactory(FakeNuGetClient? defaultClient = null)
    {
        _defaultClient = defaultClient ?? new FakeNuGetClient();
    }

    /// <summary>Per-source fakes, keyed by the declared <c>[nuget.sources.*]</c> name. A source with
    /// no entry here falls back to the shared default client.</summary>
    public Dictionary<string, FakeNuGetClient> ClientsBySource { get; } = new(StringComparer.Ordinal);

    /// <summary>Every source URL consulted, in order, across all <see cref="Create"/> calls — so a
    /// test can assert the manifest's declaration order actually reached the client.</summary>
    public List<string> RequestedSources { get; } = [];

    /// <summary>Every <c>source = "..."</c> pin passed to <see cref="Create"/>, including nulls for
    /// unpinned lookups.</summary>
    public List<string?> RequestedPins { get; } = [];

    public INuGetClient Create(NuGetSourceSet sources, string? pinnedSourceName = null)
    {
        RequestedPins.Add(pinnedSourceName);

        var selected = sources.Resolve(pinnedSourceName);
        RequestedSources.AddRange(selected.All.Select(source => source.Url));

        // Composed the same way the real factory composes it, so ordering and first-match behaviour
        // are exercised by workflow tests instead of only by MultiSourceNuGetClient's own unit tests.
        return new MultiSourceNuGetClient(
            selected.All
                .Select(source => (source, ClientsBySource.TryGetValue(source.Name, out var configured)
                    ? (INuGetClient)configured
                    : _defaultClient))
                .ToList());
    }
}
