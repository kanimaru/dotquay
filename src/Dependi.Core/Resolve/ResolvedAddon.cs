namespace Dependi.Core.Resolve;

/// <summary>An <see cref="Manifest.AddonSpec"/> pinned to a real commit — the resolver stage's
/// output (SPEC.md §5). Not yet a <see cref="Lockfile.LockedAddon"/>: <c>content_hash</c> is only
/// known after the sync engine actually installs the tree.</summary>
public sealed record ResolvedAddon(string Name, string Git, string ResolvedSha, string? DisplayVersion, string Path);
