namespace Dotquay.Core.Resolve;

/// <summary>An <see cref="Manifest.AddonSpec"/> pinned to a real commit — the resolver stage's
/// output (SPEC.md §5). Not yet a <see cref="Lockfile.LockedAddon"/>: <c>content_hash</c> is only
/// known after the sync engine actually installs the tree.</summary>
/// <param name="Path">The subtree to extract from the addon's own repo.</param>
/// <param name="InstallAs">Folder to install into under <c>addons/</c>, or null for the addon's
/// name (SPEC.md §7.1). Carried through resolution because the installer needs it and the lockfile
/// records it — recomputing it from the manifest later would fail for a transitive addon, which
/// appears in no manifest the project owns.</param>
public sealed record ResolvedAddon(
    string Name, string Git, string ResolvedSha, string? DisplayVersion, string Path,
    string? InstallAs = null) {
    /// <summary>Where this addon installs to under <c>addons/</c>.</summary>
    public string EffectiveFolder => InstallAs ?? Name;
}
