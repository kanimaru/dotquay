using Dotquay.Core.Abstractions;
using Dotquay.Core.Local;
using Dotquay.Core.Lockfile;
using Dotquay.Core.Patch;
using Dotquay.Core.Resolve;
using Dotquay.Core.Workflows;

namespace Dotquay.Core.Sync;

/// <summary>
/// Installs one already-resolved addon: fetch its commit into the shared cache, extract its
/// subtree into staging, re-apply any captured patch (SPEC.md §10 step 3), swap it into
/// <c>addons/&lt;name&gt;</c> (SPEC.md §9 steps 1-5). This is the one piece <c>add</c> and
/// <c>install</c> share — both ultimately do the same thing to a resolved commit, <c>add</c> just
/// runs the resolver first and <c>install</c> replays a commit the lockfile already pinned.
/// </summary>
public sealed class AddonInstaller
{
    private readonly IFileSystem _fileSystem;
    private readonly RepoCache _cache;
    private readonly SyncEngine _syncEngine;
    private readonly PatchReapplier _patchReapplier;

    public AddonInstaller(IFileSystem fileSystem, RepoCache cache, SyncEngine syncEngine, IPatchTool patchTool)
    {
        _fileSystem = fileSystem;
        _cache = cache;
        _syncEngine = syncEngine;
        _patchReapplier = new PatchReapplier(fileSystem, cache, patchTool);
    }

    /// <summary>
    /// Points the addon at the subtree's real casing, and — when nothing said otherwise — installs it
    /// under that real name rather than the one derived from the repository's URL.
    /// </summary>
    /// <remarks>
    /// A Godot addon refers to its own files by absolute <c>res://addons/&lt;folder&gt;/</c> paths, and
    /// those are case-sensitive strings even where the filesystem is not. So the folder an addon must
    /// live in is a fact its own repository states, and the URL is only a guess at it. Gut states
    /// <c>addons/gut</c>; the repository is named <c>Gut</c>; installing to <c>addons/Gut</c> left 88
    /// files pointing at a path that, to Godot, did not exist.
    /// <para>
    /// Deliberately narrow. This only ever fires when the guess and the repository agree
    /// case-insensitively and disagree otherwise — a correction of capitalisation, never of identity.
    /// A genuinely different folder name still has to be given as <c>--path</c>/<c>--install-as</c>,
    /// so dotquay continues not to infer layout (SPEC.md §7.1, OQ-2). An explicit
    /// <see cref="ResolvedAddon.InstallAs"/> always wins; nobody's stated choice is second-guessed.
    /// </para>
    /// <para>
    /// For a project already installed at the wrong case this reads as a rename, which
    /// <see cref="GuardOldFolderBeforeRename"/> already handles — so an existing project repairs
    /// itself on its next update instead of needing the fix by hand.
    /// </para>
    /// </remarks>
    private ResolvedAddon CorrectFolderCase(ResolvedAddon resolved, string cachedRepoPath)
    {
        var actualPath = SubtreeExtractor.ResolveSubtreePath(_fileSystem, cachedRepoPath, resolved.Path);

        if (string.Equals(actualPath, resolved.Path, StringComparison.Ordinal))
        {
            return resolved;
        }

        var corrected = resolved with { Path = actualPath };

        if (resolved.InstallAs is not null)
        {
            return corrected;
        }

        var actualFolder = actualPath[(actualPath.LastIndexOf('/') + 1)..];

        // Only a capitalisation fix. If the subtree's real name is a different name, the folder stays
        // whatever it already was — that is a layout decision, and dotquay does not make those.
        return string.Equals(actualFolder, resolved.EffectiveFolder, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(actualFolder, resolved.EffectiveFolder, StringComparison.Ordinal)
                ? corrected with { InstallAs = actualFolder }
                : corrected;
    }

    /// <summary>
    /// Fetches, extracts, (re-)patches, and swaps <paramref name="resolved"/> into
    /// <c>&lt;projectDirectory&gt;/addons/&lt;name&gt;</c>, returning the <see cref="LockedAddon"/>
    /// to write back to the lockfile. <paramref name="previous"/> is the lockfile's prior record for
    /// this addon (null for a brand-new install) — its <see cref="LockedAddon.ContentHash"/> feeds
    /// the dirty check (see <see cref="DirtyChecker"/>) and, when present, its
    /// <see cref="LockedAddon.PatchHash"/>/<see cref="LockedAddon.PatchBaseSha"/> trigger a patch
    /// re-apply before the swap (SPEC.md §10 step 3) — a captured patch survives an
    /// <c>install</c>/<c>update</c> instead of silently vanishing.
    /// </summary>
    public async Task<LockedAddon> InstallAsync(
        ResolvedAddon resolved,
        ProjectLayout layout,
        LockedAddon? previous,
        bool force,
        LocalLink? link = null,
        CancellationToken cancellationToken = default)
    {
        var projectDirectory = layout.ProjectDirectory;

        if (link is not null)
        {
            return InstallAsLink(resolved, projectDirectory, previous, force, link);
        }

        var cachedRepoPath = await _cache.GetOrFetchAsync(resolved.Git, resolved.ResolvedSha, cancellationToken);

        resolved = CorrectFolderCase(resolved, cachedRepoPath);

        var stagingPath = _fileSystem.CombinePath(projectDirectory, ".dotquay", "staging", resolved.Name);
        SubtreeExtractor.Extract(_fileSystem, cachedRepoPath, resolved.Path, stagingPath);

        if (previous is { PatchHash: not null, PatchBaseSha: not null })
        {
            await ReapplyPatchOrThrowAsync(previous, resolved, layout, stagingPath, cancellationToken);
        }

        var isRename = IsRename(previous, resolved);

        // Checked before anything is written, so a refusal leaves the project exactly as it was.
        if (isRename)
        {
            GuardOldFolderBeforeRename(previous!, projectDirectory, force);
        }

        var destinationPath = InstallPaths.For(_fileSystem, projectDirectory, resolved);

        // Either this folder is one dotquay owns, or it is not safe to write at all (v0.16). Reaching
        // here means no link was declared for this addon, so a link on disk is somebody else's: it
        // points into a checkout, and installing through it would drop a published tree inside their
        // working repository. Quietly deleting the link instead — which is what this used to do —
        // freezes their live link into a copy without a word, which is the same surprise one degree
        // quieter. Both are the caller's call to make, through `dotquay link` or `link --remove`.
        var verdict = AddonLinkGuard.Inspect(_fileSystem, destinationPath, declared: null);
        if (verdict.ForbidsWriting)
        {
            throw new SyncException(AddonLinkGuard.Explain(
                resolved.Name,
                resolved.EffectiveFolder,
                resolved.DisplayVersion ?? resolved.ResolvedSha,
                verdict));
        }

        var expectedContentHash = isRename ? null : previous?.ContentHash;
        var contentHash = _syncEngine.SwapAndHash(stagingPath, destinationPath, expectedContentHash, force);

        if (isRename)
        {
            RetirePreviousFolder(previous!, projectDirectory);
        }

        return new LockedAddon
        {
            Name = resolved.Name,
            Git = resolved.Git,
            Resolved = resolved.ResolvedSha,
            Version = resolved.DisplayVersion ?? BuildPseudoVersion(resolved.ResolvedSha),
            Path = resolved.Path,
            Folder = resolved.InstallAs,
            ContentHash = contentHash,
            PatchHash = previous?.PatchHash,
            PatchBaseSha = previous?.PatchBaseSha,

            // Carried over, never reset. The installer resolves and swaps one addon; it never walks
            // the graph, so it has no basis for claiming this addon requires nothing. `update` does
            // walk, and overwrites this with what the walk found (DependencyWorkflow's
            // SyncAddonGraphAsync) — but `install` deliberately never re-resolves, so whatever it
            // gets back from here is what lands in the lockfile. Emitting an empty list there erased
            // every graph edge on every install, which is how healthy transitive addons ended up
            // unreachable from any root: reported as orphans by `doctor` and by the editor plugin's
            // graph, and pruned for real by the next `remove`.
            Dependencies = previous?.Dependencies ?? [],
        };
    }

    /// <summary>
    /// Points <c>addons/&lt;folder&gt;</c> at a folder on this machine instead of installing the
    /// resolved commit (SPEC.md §7.4).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The lockfile entry still records the upstream commit, not the local path. The lockfile is
    /// committed and must stay reproducible for everyone else (goal #1); a machine-specific path in it
    /// would break every teammate. A link is a local detour, and unlinking puts the project straight
    /// back on the commit the lockfile always claimed.
    /// </para>
    /// <para>
    /// <see cref="LockedAddon.ContentHash"/> is deliberately carried over rather than recomputed:
    /// hashing a link means hashing the developer's in-progress edits, which would then be recorded as
    /// the expected content of an upstream release.
    /// </para>
    /// <para>
    /// A link is renameable for exactly the same reasons an installed copy is, and gets the same
    /// treatment: guard the old folder, then retire it. Skipping that left the previous
    /// <c>addons/&lt;folder&gt;</c> in place as a second live link into the same checkout — so the
    /// project loaded the addon twice, under two names, and the folder the rename was supposed to
    /// vacate kept satisfying every path it was meant to redirect.
    /// </para>
    /// </remarks>
    private LockedAddon InstallAsLink(
        ResolvedAddon resolved, string projectDirectory, LockedAddon? previous, bool force, LocalLink link)
    {
        var destinationPath = InstallPaths.For(_fileSystem, projectDirectory, resolved);

        if (!_fileSystem.DirectoryExists(link.Path))
        {
            throw new SyncException(
                $"'{resolved.Name}' is linked to '{link.Path}', which does not exist. Fix the path in "
                + $"{Local.LocalLinks.FileName} or remove the link.");
        }

        var isRename = IsRename(previous, resolved);

        // Before anything is written, as on the installed-copy path: a refusal here has to leave the
        // project exactly as it was.
        if (isRename)
        {
            GuardOldFolderBeforeRename(previous!, projectDirectory, force);
        }

        // A real installed folder is about to be replaced by a link, so the usual protection applies:
        // whatever is in there is content dotquay would otherwise refuse to overwrite (requirement
        // P0-6). An existing *link* needs no such check — it holds nothing of its own.
        if (!force && !_fileSystem.IsDirectoryLink(destinationPath)
            && DirtyChecker.Check(_fileSystem, destinationPath, previous?.ContentHash).IsDirty)
        {
            throw new SyncException(
                $"'{resolved.Name}' is being replaced by a link to '{link.Path}', but addons/"
                + $"{resolved.EffectiveFolder} has local modifications that don't match the lockfile. Linking "
                + $"would discard them. Run 'dotquay patch {resolved.Name}' to capture them first, or pass "
                + "--force to discard them (SPEC.md requirement P0-6).");
        }

        // Re-pointing an existing link is deliberately free: a link holds no content of its own, so
        // nothing can be lost, and this is the act the v0.16 refusal tells people to perform when a
        // junction and the declaration have drifted apart. The declaration wins here because the
        // caller just stated it.
        _fileSystem.CreateDirectoryLink(destinationPath, link.Path);

        if (isRename)
        {
            RetirePreviousFolder(previous!, projectDirectory);
        }

        return new LockedAddon
        {
            Name = resolved.Name,
            Git = resolved.Git,
            Resolved = resolved.ResolvedSha,
            Version = resolved.DisplayVersion ?? previous?.Version ?? BuildPseudoVersion(resolved.ResolvedSha),
            Path = resolved.Path,
            Folder = resolved.InstallAs,
            ContentHash = previous?.ContentHash,
            PatchHash = previous?.PatchHash,
            PatchBaseSha = previous?.PatchBaseSha,
            Dependencies = previous?.Dependencies ?? [],
        };
    }

    /// <summary>Whether this install lands the addon somewhere other than where the lockfile records
    /// it — a changed <c>install_as</c>, or the casing correction above. Ordinal on purpose: to Godot,
    /// <c>addons/Gut</c> and <c>addons/gut</c> are two different <c>res://</c> paths, so a case-only
    /// change is a real move and has to be handled as one.</summary>
    private static bool IsRename(LockedAddon? previous, ResolvedAddon resolved) =>
        previous is not null
        && !string.Equals(previous.EffectiveFolder, resolved.EffectiveFolder, StringComparison.Ordinal);

    /// <summary>Removes the folder a rename has just moved the addon out of.</summary>
    /// <remarks>
    /// Leaving it is worse than untidy: Godot loads whatever is under <c>addons/</c>, and a stale copy
    /// keeps satisfying the <c>preload</c> paths this feature exists to redirect — so the rename looks
    /// like it worked while the old code is what actually runs. When the old folder is a link, this
    /// removes the link and nothing else; <see cref="IFileSystem.DeleteDirectory"/> never follows one
    /// (SPEC.md §7.4).
    /// </remarks>
    private void RetirePreviousFolder(LockedAddon previous, string projectDirectory) =>
        _fileSystem.DeleteDirectory(InstallPaths.For(_fileSystem, projectDirectory, previous));

    /// <summary>
    /// Applies the dirty check (requirement P0-6) to the folder a rename is about to delete.
    /// </summary>
    /// <remarks>
    /// Without this, changing <c>install_as</c> would be the one way to get dotquay to destroy
    /// hand-edits without asking: the swap's own check looks at the <em>new</em> destination, which
    /// doesn't exist yet and is therefore trivially clean, while the old folder — the one holding the
    /// user's actual files — gets deleted unexamined. The protection has to follow the content, not
    /// the path.
    /// </remarks>
    private void GuardOldFolderBeforeRename(LockedAddon previous, string projectDirectory, bool force)
    {
        if (force)
        {
            return;
        }

        var oldPath = InstallPaths.For(_fileSystem, projectDirectory, previous);

        // A link has no content of its own to protect: what it points at is the developer's own
        // checkout, and retiring it only removes the link. Hashing it would hash their working copy —
        // in-progress edits and all — and then refuse the rename over "hand-edits" to files dotquay
        // never installed, telling them to capture their own source as a patch.
        if (_fileSystem.IsDirectoryLink(oldPath))
        {
            return;
        }

        // A patched addon legitimately differs from its recorded pristine hash; PatchAsync records
        // the patched tree as the new baseline, so ContentHash is still the right comparison.
        if (DirtyChecker.Check(_fileSystem, oldPath, previous.ContentHash).IsDirty)
        {
            throw new SyncException(
                $"'{previous.Name}' is being moved from addons/{previous.EffectiveFolder} to a different folder, "
                + $"but addons/{previous.EffectiveFolder} has local modifications that don't match the lockfile. "
                + $"Moving it would delete them. Run 'dotquay patch {previous.Name}' to capture them first, or "
                + "pass --force to discard them (SPEC.md requirement P0-6).");
        }
    }

    private async Task ReapplyPatchOrThrowAsync(
        LockedAddon previous,
        ResolvedAddon resolved,
        ProjectLayout layout,
        string stagingPath,
        CancellationToken cancellationToken)
    {
        var patchFilePath = layout.PatchFilePath(_fileSystem, previous.Name);
        if (!_fileSystem.FileExists(patchFilePath))
        {
            // The lockfile says there's a patch but the file itself is gone (deleted by hand,
            // outside dotquay) — nothing to reapply. Not this method's job to flag that
            // inconsistency (that's dotquay doctor's territory); just proceed as if unpatched.
            return;
        }

        var patchText = _fileSystem.ReadAllText(patchFilePath);

        IReadOnlyList<string> conflicts;
        try
        {
            conflicts = await _patchReapplier.ReapplyAsync(
                previous.Git, previous.PatchBaseSha!, previous.Path, patchText, stagingPath, cancellationToken);
        }
        catch (PatchException ex)
        {
            throw new SyncException(
                $"'{previous.Name}': could not reconstruct its patch against its recorded base ({ex.Message}). "
                + $"Run 'dotquay patch {previous.Name}' again once resolved.", ex);
        }

        if (conflicts.Count > 0)
        {
            // Record which commit the staged tree came from. The update is about to abort, so the
            // lockfile is never written and still names the *old* commit — without this note,
            // `patch --resolve` would recapture the resolved tree against the old base, producing a
            // patch that conflicts against the new upstream on every future update, forever. Found
            // exactly that way: resolve worked once and then the addon could never update again.
            _fileSystem.WriteAllText(
                layout.ConflictMarkerPath(_fileSystem, previous.Name),
                $"{resolved.ResolvedSha}{Environment.NewLine}{resolved.DisplayVersion}{Environment.NewLine}");

            // The patch was re-applied automatically (that part always happens, SPEC.md §10 step 3);
            // upstream simply changed the same lines this patch touches. Leading with what dotquay
            // did, and what is safe, matters — the old wording read like the update had failed
            // outright and left the project in an unknown state.
            throw new SyncException(
                $"'{previous.Name}': your patch still applies, but upstream changed the same lines in "
                + $"{conflicts.Count} file(s): {string.Join(", ", conflicts)}.{Environment.NewLine}"
                + $"  addons/{previous.EffectiveFolder} is untouched and still working (requirement P1-1)."
                + $"{Environment.NewLine}"
                + $"  1. Edit the conflict markers in {stagingPath}{Environment.NewLine}"
                + $"  2. Run 'dotquay patch {previous.Name} --resolve' to apply and recapture."
                + $"{Environment.NewLine}"
                + $"  Or 'dotquay patch {previous.Name} --abandon' to drop the patch and take upstream as-is.");
        }
    }

    /// <summary>Pseudo-version for untagged repos, e.g. <c>"0.0.0-20260821-9f2c1ab"</c> (SPEC.md
    /// §6) — display-only, never parsed back.</summary>
    private static string BuildPseudoVersion(string commitSha)
    {
        var shortSha = commitSha.Length > 7 ? commitSha[..7] : commitSha;
        return $"0.0.0-{DateTime.UtcNow:yyyyMMdd}-{shortSha}";
    }
}
