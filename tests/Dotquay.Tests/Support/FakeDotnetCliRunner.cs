using Dotquay.Core.Abstractions;
using Dotquay.Core.Dotnet;

namespace Dotquay.Tests.Support;

/// <summary>Configurable <see cref="IDotnetCliRunner"/> test double — records restore calls (path
/// plus whatever environment variables were passed for private-feed credentials) instead of
/// actually invoking the .NET SDK.</summary>
public sealed class FakeDotnetCliRunner : IDotnetCliRunner {
    public List<RestoreCall> RestoreCalls { get; } = [];

    /// <summary>Convenience view for the common assertion of "which csproj paths were restored" —
    /// most tests don't care about the credential environment.</summary>
    public IReadOnlyList<string> RestoredCsprojPaths => RestoreCalls.Select(c => c.CsprojPath)
        .ToList();

    public bool ThrowOnRestore { get; set; }

    public Task RestoreAsync(
        string csprojPath, IReadOnlyDictionary<string, string>? environmentVariables = null,
        CancellationToken cancellationToken = default) {
        if (ThrowOnRestore) {
            throw new DotnetSyncException($"simulated restore failure for {csprojPath}");
        }

        RestoreCalls.Add(new RestoreCall(csprojPath, environmentVariables));
        return Task.CompletedTask;
    }

    public sealed record RestoreCall(string CsprojPath,
        IReadOnlyDictionary<string, string>? EnvironmentVariables);
}
