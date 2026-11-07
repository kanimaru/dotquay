using Dotquay.Core.Abstractions;
using Dotquay.Core.Lockfile;
using Dotquay.Core.Sync;

namespace Dotquay.Core.Workflows;

/// <summary>
/// Classifies one installed addon as clean / patched-as-expected / modified / missing (SPEC.md §6,
/// requirement P1-4) by comparing disk against the lockfile's recorded content hash. Extracted from
/// <see cref="DependencyWorkflow.Status"/> in v0.5 so <c>dotquay graph</c> can label graph nodes with
/// the same verdict <c>dotquay status</c> prints, rather than a second, subtly-different check.
/// </summary>
public static class AddonStatusEvaluator {
    public static AddonStatusKind Evaluate(IFileSystem fileSystem, string projectDirectory, LockedAddon locked) {
        // Deliberately not locked.Path: that is the SOURCE subtree extracted from the upstream repo,
        // which is a different thing from where the addon lands here. InstallPaths is the single
        // authority on the destination (it also honours install_as, SPEC.md §7.1) — computing it by
        // hand is how the installer and the dirty check end up disagreeing about which folder they
        // mean.
        var installedPath = InstallPaths.For(fileSystem, projectDirectory, locked);

        if (!fileSystem.DirectoryExists(installedPath)) {
            return AddonStatusKind.Missing;
        }

        // Checked before the content hash, because hashing a link means hashing the developer's live
        // working copy — which will essentially never match the lockfile, so every linked addon would
        // report as modified and the dirty check would refuse to touch it.
        if (fileSystem.IsDirectoryLink(installedPath)) {
            return AddonStatusKind.Linked;
        }

        if (DirtyChecker.Check(fileSystem, installedPath, locked.ContentHash).IsDirty) {
            return AddonStatusKind.Modified;
        }

        return locked.PatchHash is not null ? AddonStatusKind.PatchedAsExpected : AddonStatusKind.Clean;
    }
}
