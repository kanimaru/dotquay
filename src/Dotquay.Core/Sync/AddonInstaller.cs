using Dotquay.Core.Abstractions;
using Dotquay.Core.Lockfile;
using Dotquay.Core.Patch;
using Dotquay.Core.Resolve;

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
        string projectDirectory,
        LockedAddon? previous,
        bool force,
        CancellationToken cancellationToken = default)
    {
        var cachedRepoPath = await _cache.GetOrFetchAsync(resolved.Git, resolved.ResolvedSha, cancellationToken);

        var stagingPath = _fileSystem.CombinePath(projectDirectory, ".dotquay", "staging", resolved.Name);
        SubtreeExtractor.Extract(_fileSystem, cachedRepoPath, resolved.Path, stagingPath);

        if (previous is { PatchHash: not null, PatchBaseSha: not null })
        {
            await ReapplyPatchOrThrowAsync(previous, projectDirectory, stagingPath, cancellationToken);
        }

        var destinationPath = _fileSystem.CombinePath(projectDirectory, "addons", resolved.Name);
        var contentHash = _syncEngine.SwapAndHash(stagingPath, destinationPath, previous?.ContentHash, force);

        return new LockedAddon
        {
            Name = resolved.Name,
            Git = resolved.Git,
            Resolved = resolved.ResolvedSha,
            Version = resolved.DisplayVersion ?? BuildPseudoVersion(resolved.ResolvedSha),
            Path = resolved.Path,
            ContentHash = contentHash,
            PatchHash = previous?.PatchHash,
            PatchBaseSha = previous?.PatchBaseSha,
            Dependencies = [],
        };
    }

    private async Task ReapplyPatchOrThrowAsync(
        LockedAddon previous, string projectDirectory, string stagingPath, CancellationToken cancellationToken)
    {
        var patchFilePath = _fileSystem.CombinePath(projectDirectory, "patches", $"{previous.Name}.patch");
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
            throw new SyncException(
                $"'{previous.Name}': updating produced {conflicts.Count} conflicting file(s) while re-applying "
                + $"its patch: {string.Join(", ", conflicts)}. Conflict markers are left in {stagingPath} — "
                + $"resolve them there, copy the resolved files into addons/{previous.Name}, then run "
                + $"'dotquay patch {previous.Name}' to recapture against the new base. addons/{previous.Name} "
                + "was left untouched (SPEC.md requirement P1-1).");
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
