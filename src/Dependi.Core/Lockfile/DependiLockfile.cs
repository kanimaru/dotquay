namespace Dependi.Core.Lockfile;

/// <summary>The parsed contents of <c>dependi.lock</c> (SPEC.md §7.3) — "what you actually got",
/// pinned to commit SHAs. Machine-written, committed to the repo, and the only thing
/// <c>dependi install</c> reads.</summary>
public sealed record DependiLockfile
{
    public IReadOnlyList<LockedAddon> Addons { get; init; } = new List<LockedAddon>();

    public IReadOnlyList<LockedDotnet> Dotnet { get; init; } = new List<LockedDotnet>();
}
