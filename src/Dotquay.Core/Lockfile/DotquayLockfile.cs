namespace Dotquay.Core.Lockfile;

/// <summary>The parsed contents of <c>dotquay.lock</c> (SPEC.md §7.3) — "what you actually got",
/// pinned to commit SHAs. Machine-written, committed to the repo, and the only thing
/// <c>dotquay install</c> reads.</summary>
public sealed record DotquayLockfile
{
    public IReadOnlyList<LockedAddon> Addons { get; init; } = new List<LockedAddon>();

    public IReadOnlyList<LockedDotnet> Dotnet { get; init; } = new List<LockedDotnet>();
}
