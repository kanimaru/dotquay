using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Dependi.Core.Abstractions;
using Dependi.Core.Doctor;
using Dependi.Core.Dotnet;
using Dependi.Core.Git;
using Dependi.Core.Lockfile;
using Dependi.Core.Manifest;
using Dependi.Core.Patch;
using Dependi.Core.Resolve;
using Dependi.Core.Sync;
using Dependi.Core.Versioning;

namespace Dependi.Core.Workflows;

/// <summary>
/// Orchestrates <c>dependi add</c>/<c>install</c>/<c>remove</c>/<c>update</c>/<c>list</c>: the
/// CLI-agnostic composition of manifest/lockfile I/O with <see cref="AddonResolver"/>/
/// <see cref="AddonInstaller"/> for git addons (SPEC.md §5) and <see cref="NuGetVersionSelector"/>/
/// <see cref="DotnetInstaller"/> for NuGet packages (SPEC.md §11). CLI commands should be a thin
/// translation of parsed args into calls here plus console output — no orchestration logic in
/// Dependi.Cli itself (SPEC.md §12).
/// </summary>
public sealed class DependencyWorkflow
{
    private const string ManifestFileName = "dependi.toml";
    private const string LockfileFileName = "dependi.lock";

    private readonly IFileSystem _fileSystem;
    private readonly IGitClient _gitClient;
    private readonly INuGetClientFactory _nuGetClientFactory;
    private readonly AddonResolver _resolver;
    private readonly AddonInstaller _addonInstaller;
    private readonly RepoCache _repoCache;
    private readonly IPatchTool _patchTool;
    private readonly DotnetInstaller _dotnetInstaller;

    public DependencyWorkflow(IFileSystem fileSystem, IGitClient gitClient, string cacheRoot)
        : this(fileSystem, gitClient, new NuGetV3ClientFactory(), cacheRoot)
    {
    }

    public DependencyWorkflow(IFileSystem fileSystem, IGitClient gitClient, INuGetClientFactory nuGetClientFactory, string cacheRoot)
        : this(fileSystem, gitClient, nuGetClientFactory, new RepoCache(fileSystem, gitClient, cacheRoot))
    {
    }

    /// <summary>Shares one <see cref="RepoCache"/> between <see cref="AddonInstaller"/> (fetch for
    /// install) and <see cref="PatchAsync"/> (fetch the pristine base to diff against) — both need
    /// the exact same content-addressed cache, not two independently-behaving instances pointed at
    /// the same directory.</summary>
    private DependencyWorkflow(IFileSystem fileSystem, IGitClient gitClient, INuGetClientFactory nuGetClientFactory, RepoCache repoCache)
        : this(fileSystem, gitClient, nuGetClientFactory, repoCache, new ProcessPatchTool())
    {
    }

    /// <summary>Shares one <see cref="IPatchTool"/> between <see cref="AddonInstaller"/> (patch
    /// re-apply on install/update, SPEC.md §10 step 3) and <see cref="PatchAsync"/> (patch capture) —
    /// same "one real collaborator per concern" reasoning as the <see cref="RepoCache"/> overload
    /// above.</summary>
    private DependencyWorkflow(
        IFileSystem fileSystem, IGitClient gitClient, INuGetClientFactory nuGetClientFactory, RepoCache repoCache, IPatchTool patchTool)
        : this(
            fileSystem,
            gitClient,
            nuGetClientFactory,
            new AddonInstaller(fileSystem, repoCache, new SyncEngine(fileSystem), patchTool),
            repoCache,
            patchTool,
            new DotnetInstaller(fileSystem, new ProcessDotnetCliRunner()))
    {
    }

    /// <summary>Test seam — lets tests substitute the addon install pipeline with a controlled
    /// <see cref="AddonInstaller"/> while still getting working (if network/process-touching)
    /// collaborators for everything else, including patch capture. The <see cref="RepoCache"/> built
    /// here is a fresh one (<see cref="AddonInstaller"/> doesn't expose whichever one it was given),
    /// which is only a problem for a test that both substitutes a custom <see cref="AddonInstaller"/>
    /// <em>and</em> exercises <see cref="PatchAsync"/> against the same fetch — use the full seam
    /// below for that case instead.</summary>
    internal DependencyWorkflow(IFileSystem fileSystem, IGitClient gitClient, AddonInstaller addonInstaller)
        : this(
            fileSystem,
            gitClient,
            new NuGetV3ClientFactory(),
            addonInstaller,
            new RepoCache(fileSystem, gitClient, cacheRoot: "unused-by-this-seam"),
            new ProcessPatchTool(),
            new DotnetInstaller(fileSystem, new ProcessDotnetCliRunner()))
    {
    }

    /// <summary>Same test seam as below, minus <see cref="RepoCache"/>/<see cref="IPatchTool"/> — for
    /// existing tests that supply their own <see cref="AddonInstaller"/>/<see cref="DotnetInstaller"/>
    /// pair but don't exercise <see cref="PatchAsync"/>. Builds the same throwaway
    /// <see cref="RepoCache"/>/real <see cref="ProcessPatchTool"/> fallback as the
    /// <see cref="AddonInstaller"/>-only seam above, for the same reason.</summary>
    internal DependencyWorkflow(
        IFileSystem fileSystem,
        IGitClient gitClient,
        INuGetClientFactory nuGetClientFactory,
        AddonInstaller addonInstaller,
        DotnetInstaller dotnetInstaller)
        : this(
            fileSystem,
            gitClient,
            nuGetClientFactory,
            addonInstaller,
            new RepoCache(fileSystem, gitClient, cacheRoot: "unused-by-this-seam"),
            new ProcessPatchTool(),
            dotnetInstaller)
    {
    }

    /// <summary>Full test seam — every network/process-touching dependency is a fake.</summary>
    internal DependencyWorkflow(
        IFileSystem fileSystem,
        IGitClient gitClient,
        INuGetClientFactory nuGetClientFactory,
        AddonInstaller addonInstaller,
        RepoCache repoCache,
        IPatchTool patchTool,
        DotnetInstaller dotnetInstaller)
    {
        _fileSystem = fileSystem;
        _gitClient = gitClient;
        _nuGetClientFactory = nuGetClientFactory;
        _resolver = new AddonResolver(gitClient);
        _addonInstaller = addonInstaller;
        _repoCache = repoCache;
        _patchTool = patchTool;
        _dotnetInstaller = dotnetInstaller;
    }

    /// <summary>
    /// Adds a new git-sourced addon: derives a name from the URL, picks a sensible default source
    /// (latest tag's minor range if the repo has releases, otherwise a hard pin to the default
    /// branch's tip — SPEC.md §6), resolves, installs, and updates both the manifest and lockfile.
    /// </summary>
    /// <param name="pathOverride">
    /// Subtree to extract, overriding the <c>addons/&lt;name&gt;</c> default (SPEC.md §7.1, OQ-2).
    /// Real repos routinely name their addon folder differently from the repo itself — e.g.
    /// <c>github.com/nathanhoad/godot_dialogue_manager</c>'s addon lives at
    /// <c>addons/dialogue_manager</c>, not <c>addons/godot_dialogue_manager</c> — so the default
    /// guess is a starting point, not a guarantee, and callers need a way to correct it up front
    /// instead of hand-editing the manifest after a failed add.
    /// </param>
    public async Task<LockedAddon> AddAsync(
        string projectDirectory, string gitUrl, string? pathOverride = null, CancellationToken cancellationToken = default)
    {
        var (manifest, manifestPath) = ReadManifestOrThrow(projectDirectory);

        var name = DeriveAddonName(gitUrl);
        if (manifest.Addons.ContainsKey(name))
        {
            throw new WorkflowException(
                $"'{name}' is already declared in {manifestPath}. Use 'dependi update {name}' instead.");
        }

        var spec = await BuildDefaultSpecAsync(name, gitUrl, pathOverride, cancellationToken);
        var updatedManifest = manifest with
        {
            Addons = new Dictionary<string, AddonSpec>(manifest.Addons) { [name] = spec },
        };

        var (lockfile, lockfilePath) = ReadOrCreateLockfile(projectDirectory);

        // Only the new addon (and whatever it transitively pulls in) is resolved fresh -- every
        // other already-locked addon is frozen to its current commit, so adding one addon can never
        // silently move an unrelated one forward (SPEC.md §8 unification still runs across the whole
        // graph, it just treats untouched addons as exact pins for that purpose; see
        // SyncAddonGraphAsync's remarks).
        var (syncedAddons, addonDeclaredDotnet) = await SyncAddonGraphAsync(
            projectDirectory, updatedManifest, lockfile, freshNames: new HashSet<string>(StringComparer.Ordinal) { name },
            force: false, cancellationToken);

        // Same "don't move what wasn't asked to move" treatment for dotnet: only genuinely new
        // addon-declared packages get resolved; anything already known (project-declared or already
        // locked from an earlier add) is left exactly as it was (requirement P1-5).
        var newDotnetNames = addonDeclaredDotnet
            .Select(s => s.Name)
            .Where(n => !updatedManifest.Dotnet.ContainsKey(n) && lockfile.Dotnet.All(d => d.Name != n))
            .ToHashSet(StringComparer.Ordinal);
        var syncedDotnet = await SyncDotnetAsync(
            projectDirectory, updatedManifest, lockfile.Dotnet, newDotnetNames, pruneUnreachable: false, cancellationToken);

        WriteManifest(manifestPath, updatedManifest);
        WriteLockfile(lockfilePath, lockfile with { Addons = syncedAddons, Dotnet = syncedDotnet });

        return syncedAddons.First(a => a.Name == name);
    }

    /// <summary>
    /// Adds a new NuGet dependency: queries the latest stable version (or verifies an explicit
    /// <paramref name="versionOverride"/> actually exists), writes a <c>PackageReference</c> into
    /// the project's <c>.csproj</c>, runs <c>dotnet restore</c>, and updates the manifest and
    /// lockfile (SPEC.md §11, requirement P0-7).
    /// </summary>
    public async Task<LockedDotnet> AddDotnetAsync(
        string projectDirectory, string packageId, string? versionOverride = null, CancellationToken cancellationToken = default)
    {
        var (manifest, manifestPath) = ReadManifestOrThrow(projectDirectory);

        if (manifest.Dotnet.ContainsKey(packageId))
        {
            throw new WorkflowException(
                $"'{packageId}' is already declared in {manifestPath}. Use 'dependi update {packageId}' instead.");
        }

        var nuGetClient = _nuGetClientFactory.Create(manifest.NuGetSource);
        var version = await ResolveDotnetVersionOrThrowAsync(nuGetClient, packageId, versionOverride, cancellationToken);
        var locked = await InstallDotnetOrThrowAsync(
            projectDirectory, packageId, version, manifest.NuGetSource, restore: true, cancellationToken);

        WriteManifest(manifestPath, manifest with
        {
            Dotnet = new Dictionary<string, DotnetSpec>(manifest.Dotnet) { [packageId] = new DotnetSpec { Name = packageId, Version = version } },
        });

        var (lockfile, lockfilePath) = ReadOrCreateLockfile(projectDirectory);
        WriteLockfile(lockfilePath, lockfile with
        {
            Dotnet = lockfile.Dotnet.Where(d => d.Name != packageId).Append(locked).ToList(),
        });

        return locked;
    }

    /// <summary>
    /// Replays every locked addon and dotnet entry exactly as the lockfile pinned them — never
    /// re-resolves (that's what makes <c>install</c> reproducible, SPEC.md §5). Refuses if the
    /// manifest declares something the lockfile doesn't know about yet (requirement P0-3).
    /// </summary>
    public async Task<InstallResult> InstallAsync(string projectDirectory, bool force, CancellationToken cancellationToken = default)
    {
        var (manifest, _) = ReadManifestOrThrow(projectDirectory);
        var (lockfile, lockfilePath) = ReadLockfileOrThrow(projectDirectory);

        var missingAddons = manifest.Addons.Keys.Where(name => lockfile.Addons.All(a => a.Name != name)).ToList();
        var missingDotnet = manifest.Dotnet.Keys.Where(name => lockfile.Dotnet.All(d => d.Name != name)).ToList();
        var missing = missingAddons.Concat(missingDotnet).ToList();
        if (missing.Count > 0)
        {
            throw new WorkflowException(
                $"{lockfilePath} is missing {string.Join(", ", missing)} (declared in the manifest but not "
                + $"locked) — run 'dependi add' or 'dependi update' first (requirement P0-3).");
        }

        var installedAddons = new List<LockedAddon>();
        foreach (var addon in lockfile.Addons)
        {
            var resolved = new ResolvedAddon(addon.Name, addon.Git, addon.Resolved, addon.Version, addon.Path);
            installedAddons.Add(await InstallAddonOrThrowAsync(resolved, projectDirectory, addon, force, cancellationToken));
        }

        var installedDotnet = new List<LockedDotnet>();
        foreach (var dependency in lockfile.Dotnet)
        {
            installedDotnet.Add(await InstallDotnetOrThrowAsync(
                projectDirectory, dependency.Name, dependency.Resolved, manifest.NuGetSource, restore: false, cancellationToken));
        }

        if (installedDotnet.Count > 0)
        {
            await RestoreDotnetOrThrowAsync(projectDirectory, manifest.NuGetSource, cancellationToken);
        }

        WriteLockfile(lockfilePath, lockfile with { Addons = installedAddons, Dotnet = installedDotnet });
        return new InstallResult(installedAddons, installedDotnet);
    }

    /// <summary>
    /// Re-resolves one dependency (or every addon and dotnet package declared in the manifest, if
    /// <paramref name="name"/> is null) fresh and reinstalls it — unlike <see cref="InstallAsync"/>,
    /// which only ever replays what the lockfile already pinned. Untargeted entries already in the
    /// lockfile are left exactly as they are. Also doubles as the recovery path for something a
    /// teammate hand-added to the manifest without running <c>dependi add</c>: with no prior lock
    /// entry, this locks and installs it for the first time (SPEC.md §6).
    /// </summary>
    public async Task<UpdateResult> UpdateAsync(
        string projectDirectory, string? name, bool force, CancellationToken cancellationToken = default)
    {
        var (manifest, manifestPath) = ReadManifestOrThrow(projectDirectory);
        var (lockfile, lockfilePath) = ReadOrCreateLockfile(projectDirectory);

        if (name is not null && !manifest.Addons.ContainsKey(name) && !manifest.Dotnet.ContainsKey(name))
        {
            throw new WorkflowException($"'{name}' is not declared in {manifestPath}.");
        }

        var previousAddonsByName = lockfile.Addons.ToDictionary(a => a.Name, StringComparer.Ordinal);
        var walksAddonGraph = name is null || manifest.Addons.ContainsKey(name);

        IReadOnlyList<LockedAddon> addons = lockfile.Addons;
        IReadOnlyList<DotnetSpec> addonDeclaredDotnet = [];
        var addonResults = new List<AddonUpdateResult>();

        if (walksAddonGraph)
        {
            // A full update (name is null) re-resolves every top-level addon fresh -- SyncAddonGraphAsync
            // then treats the walk as exhaustive and prunes anything no longer reachable. A single
            // named addon update only re-resolves that one addon; every other top-level addon (and,
            // transitively, anything only it needs) is frozen exactly as it's currently locked.
            var freshNames = name is null
                ? manifest.Addons.Keys.ToHashSet(StringComparer.Ordinal)
                : new HashSet<string>(StringComparer.Ordinal) { name };

            (addons, addonDeclaredDotnet) = await SyncAddonGraphAsync(projectDirectory, manifest, lockfile, freshNames, force, cancellationToken);

            var currentByName = addons.ToDictionary(a => a.Name, StringComparer.Ordinal);
            foreach (var addonName in freshNames.Union(previousAddonsByName.Keys).Union(currentByName.Keys))
            {
                previousAddonsByName.TryGetValue(addonName, out var previous);
                if (!currentByName.TryGetValue(addonName, out var current))
                {
                    continue; // pruned as an orphan -- nothing to report as an "update" result for it
                }

                // Report anything explicitly targeted (even if unchanged, so the CLI can say so) plus
                // anything that moved for any other reason (e.g. a transitive dependency newly
                // discovered, or advanced because its own package manifest changed).
                if (freshNames.Contains(addonName) || previous is null || previous.Resolved != current.Resolved)
                {
                    addonResults.Add(new AddonUpdateResult(addonName, previous?.Resolved, current));
                }
            }
        }

        var dotnetTargetNames = new HashSet<string>(StringComparer.Ordinal);
        if (name is null)
        {
            dotnetTargetNames.UnionWith(manifest.Dotnet.Keys);
        }
        else if (manifest.Dotnet.ContainsKey(name))
        {
            dotnetTargetNames.Add(name);
        }

        foreach (var spec in addonDeclaredDotnet)
        {
            if (!manifest.Dotnet.ContainsKey(spec.Name))
            {
                dotnetTargetNames.Add(spec.Name);
            }
        }

        var previousDotnetByName = lockfile.Dotnet.ToDictionary(d => d.Name, StringComparer.Ordinal);
        var syncedDotnet = await SyncDotnetAsync(
            projectDirectory, manifest, lockfile.Dotnet, dotnetTargetNames, pruneUnreachable: name is null, cancellationToken);

        var syncedDotnetByName = syncedDotnet.ToDictionary(d => d.Name, StringComparer.Ordinal);
        var dotnetResults = new List<DotnetUpdateResult>();
        foreach (var targetName in dotnetTargetNames)
        {
            previousDotnetByName.TryGetValue(targetName, out var previous);
            if (syncedDotnetByName.TryGetValue(targetName, out var current))
            {
                dotnetResults.Add(new DotnetUpdateResult(targetName, previous?.Resolved, current));
            }
        }

        WriteLockfile(lockfilePath, lockfile with { Addons = addons, Dotnet = syncedDotnet });
        return new UpdateResult(addonResults, dotnetResults);
    }

    /// <summary>Removes an addon or a dotnet dependency (whichever <paramref name="name"/> matches)
    /// from the manifest, lockfile, and disk/.csproj. Unlike <c>install</c>, this does not run the
    /// dirty check for addons — removing is an explicit, unambiguous request, not an overwrite
    /// dependi needs to protect the user from (SPEC.md §6).</summary>
    /// <remarks>
    /// The addon branch never touches the network: reachability after removing <paramref name="name"/>
    /// from the manifest is computed purely from the lockfile's own recorded
    /// <see cref="LockedAddon.Dependencies"/> graph edges (SPEC.md §8) — exactly what that field
    /// exists for. If <paramref name="name"/> is still reachable transitively through some other
    /// addon's dependencies, its lockfile entry and installed folder are left alone (something else
    /// still needs it); only entries no longer reachable from any remaining top-level addon are
    /// pruned from disk and the lockfile. This deliberately does not re-walk or re-resolve anything
    /// that stays reachable — a plain <c>remove</c> never moves another addon's pinned commit.
    /// </remarks>
    public Task RemoveAsync(string projectDirectory, string name, CancellationToken cancellationToken = default)
    {
        var (manifest, manifestPath) = ReadManifestOrThrow(projectDirectory);

        if (manifest.Addons.ContainsKey(name))
        {
            var updatedManifest = manifest with
            {
                Addons = manifest.Addons.Where(kv => kv.Key != name).ToDictionary(kv => kv.Key, kv => kv.Value),
            };
            WriteManifest(manifestPath, updatedManifest);

            var (lockfile, lockfilePath) = ReadOrCreateLockfile(projectDirectory);
            var lockedByName = lockfile.Addons.ToDictionary(a => a.Name, StringComparer.Ordinal);
            var reachable = ComputeReachableAddonNames(lockedByName, updatedManifest.Addons.Keys);

            foreach (var orphan in lockfile.Addons.Where(a => !reachable.Contains(a.Name)))
            {
                _fileSystem.DeleteDirectory(_fileSystem.CombinePath(projectDirectory, "addons", orphan.Name));
            }

            WriteLockfile(lockfilePath, lockfile with { Addons = lockfile.Addons.Where(a => reachable.Contains(a.Name)).ToList() });
            return Task.CompletedTask;
        }

        if (manifest.Dotnet.ContainsKey(name))
        {
            WriteManifest(manifestPath, manifest with
            {
                Dotnet = manifest.Dotnet.Where(kv => kv.Key != name).ToDictionary(kv => kv.Key, kv => kv.Value),
            });

            var (lockfile, lockfilePath) = ReadOrCreateLockfile(projectDirectory);
            WriteLockfile(lockfilePath, lockfile with { Dotnet = lockfile.Dotnet.Where(d => d.Name != name).ToList() });

            try
            {
                _dotnetInstaller.Remove(projectDirectory, name);
            }
            catch (DotnetSyncException ex)
            {
                throw new WorkflowException(ex.Message, ex);
            }

            return Task.CompletedTask;
        }

        throw new WorkflowException($"'{name}' is not declared in {manifestPath}.");
    }

    /// <summary>
    /// Captures hand-edits to an installed addon as a patch file (SPEC.md §10, requirement P1-1):
    /// fetches (or reuses) the pristine cache copy at the addon's locked SHA, extracts the same
    /// subtree <c>Path</c> pointed at during install, diffs it against <c>addons/&lt;name&gt;</c>,
    /// and writes the result to <c>patches/&lt;name&gt;.patch</c> — committed, so teammates get it
    /// (SPEC.md §10 step 2). An empty diff clears any previously captured patch instead of writing
    /// an empty file: nothing to re-apply means nothing should be recorded. Records
    /// <c>patch_hash</c> (a fingerprint of the patch file's own content, so <c>status</c>/<c>doctor</c>
    /// could one day detect a hand-edited patch file too) and <c>patch_base_sha</c> (the exact SHA
    /// the patch applies cleanly against — <see cref="LockedAddon.PatchBaseSha"/>'s doc comment
    /// explains why this is tracked separately from <see cref="LockedAddon.Resolved"/>).
    /// </summary>
    /// <remarks>
    /// Also recomputes <c>content_hash</c> from <c>addons/&lt;name&gt;</c> as it currently stands
    /// (the patched state) — capturing a patch makes that state the new expected baseline, not a
    /// deviation from it, so a later plain <c>install</c>/<c>update</c> (no upstream change) must see
    /// it as clean rather than refusing with "local modifications don't match the lockfile"
    /// (SPEC.md requirement P0-6; confirmed against a real repo during this task — the dirty check
    /// otherwise fires on literally every install/update of a patched addon, since the lockfile's
    /// <c>content_hash</c> would still point at the pre-patch tree forever).
    /// </remarks>
    public async Task<PatchCaptureResult> PatchAsync(string projectDirectory, string name, CancellationToken cancellationToken = default)
    {
        var (lockfile, lockfilePath) = ReadOrCreateLockfile(projectDirectory);
        var locked = lockfile.Addons.FirstOrDefault(a => a.Name == name)
            ?? throw new WorkflowException($"'{name}' is not a locked addon. Run 'dependi add' or 'dependi install' first.");

        var installedPath = _fileSystem.CombinePath(projectDirectory, "addons", name);
        if (!_fileSystem.DirectoryExists(installedPath))
        {
            throw new WorkflowException($"'addons/{name}' does not exist on disk — nothing to diff. Run 'dependi install' first.");
        }

        // Deliberately its own staging subfolder, not the one AddonInstaller uses for the same
        // addon name — this is a read-only scratch copy for diffing, a different concern from the
        // install pipeline's stage-then-swap target.
        var baseStagingPath = _fileSystem.CombinePath(projectDirectory, ".dependi", "staging", $"{name}-patch-base");

        try
        {
            var cachedRepoPath = await _repoCache.GetOrFetchAsync(locked.Git, locked.Resolved, cancellationToken);
            SubtreeExtractor.Extract(_fileSystem, cachedRepoPath, locked.Path, baseStagingPath);

            var patchText = await DiffForPatchOrThrowAsync(baseStagingPath, installedPath, name, cancellationToken);

            var patchesDirectory = _fileSystem.CombinePath(projectDirectory, "patches");
            var patchFilePath = _fileSystem.CombinePath(patchesDirectory, $"{name}.patch");

            // The state on disk right now becomes the new expected baseline either way (see the
            // <remarks> above) — computed once, after the diff, so it reflects addons/<name> exactly
            // as it was compared.
            var contentHash = ContentHasher.HashDirectory(_fileSystem, installedPath);

            LockedAddon updated;
            bool captured;

            if (string.IsNullOrEmpty(patchText))
            {
                if (_fileSystem.FileExists(patchFilePath))
                {
                    _fileSystem.DeleteFile(patchFilePath);
                }

                updated = WithPatch(locked, contentHash, patchHash: null, patchBaseSha: null);
                captured = false;
            }
            else
            {
                _fileSystem.WriteAllText(patchFilePath, patchText);
                updated = WithPatch(locked, contentHash, patchHash: ComputePatchHash(patchText), patchBaseSha: locked.Resolved);
                captured = true;
            }

            WriteLockfile(lockfilePath, lockfile with
            {
                Addons = lockfile.Addons.Select(a => a.Name == name ? updated : a).ToList(),
            });

            return new PatchCaptureResult(name, captured, patchFilePath);
        }
        finally
        {
            _fileSystem.DeleteDirectory(baseStagingPath);
        }
    }

    private async Task<string> DiffForPatchOrThrowAsync(
        string baseDirectory, string oursDirectory, string name, CancellationToken cancellationToken)
    {
        try
        {
            return await _patchTool.DiffDirectoriesAsync(baseDirectory, oursDirectory, cancellationToken);
        }
        catch (PatchException ex)
        {
            throw new WorkflowException($"'{name}': {ex.Message}", ex);
        }
    }

    private static LockedAddon WithPatch(LockedAddon locked, string contentHash, string? patchHash, string? patchBaseSha) => new()
    {
        Name = locked.Name,
        Git = locked.Git,
        Resolved = locked.Resolved,
        Version = locked.Version,
        Path = locked.Path,
        ContentHash = contentHash,
        PatchHash = patchHash,
        PatchBaseSha = patchBaseSha,
        Dependencies = locked.Dependencies,
    };

    private static string ComputePatchHash(string patchText) =>
        "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(patchText))).ToLowerInvariant();

    /// <summary>Reads the lockfile's addons as-is — <c>dependi list</c> shows what's actually
    /// locked, not what's on disk (SPEC.md §6).</summary>
    public IReadOnlyList<LockedAddon> ListInstalled(string projectDirectory) => ReadOrCreateLockfile(projectDirectory).Lockfile.Addons;

    /// <summary>The dotnet-side twin of <see cref="ListInstalled"/>.</summary>
    public IReadOnlyList<LockedDotnet> ListInstalledDotnet(string projectDirectory) => ReadOrCreateLockfile(projectDirectory).Lockfile.Dotnet;

    /// <summary>
    /// Diffs disk against the lockfile's recorded content hashes, per addon (SPEC.md §6, requirement
    /// P1-4). Purely local — no network, no manifest read, just the lockfile and whatever's actually
    /// on disk right now. Reuses <see cref="Sync.DirtyChecker"/>, the same content-hash comparison
    /// install/update already trust before ever touching an installed addon (requirement P0-6) — this
    /// command exists to surface that same check on demand, not to reimplement it.
    /// </summary>
    public StatusResult Status(string projectDirectory)
    {
        var (lockfile, _) = ReadOrCreateLockfile(projectDirectory);

        var results = new List<AddonStatusInfo>();
        foreach (var locked in lockfile.Addons)
        {
            // Deliberately "addons/<name>", not locked.Path: Path is the SOURCE subtree extracted
            // from the upstream repo (spec.EffectivePath — can differ from the destination when
            // --path overrides it, SPEC.md §7.1 OQ-2), while AddonInstaller always installs to
            // "addons/<name>" regardless (see AddonInstaller.InstallAsync's destinationPath). The two
            // only coincide when no --path override is used, which is why this bug went unnoticed
            // until an addon with an overridden path exercised status/doctor.
            var installedPath = _fileSystem.CombinePath(projectDirectory, "addons", locked.Name);

            if (!_fileSystem.DirectoryExists(installedPath))
            {
                results.Add(new AddonStatusInfo(locked.Name, AddonStatusKind.Missing));
                continue;
            }

            var dirty = DirtyChecker.Check(_fileSystem, installedPath, locked.ContentHash);
            var kind = dirty.IsDirty
                ? AddonStatusKind.Modified
                : locked.PatchHash is not null ? AddonStatusKind.PatchedAsExpected : AddonStatusKind.Clean;

            results.Add(new AddonStatusInfo(locked.Name, kind));
        }

        return new StatusResult(results);
    }

    /// <summary>
    /// Runs the four sanity checks SPEC.md §6 lists for <c>dependi doctor</c> (requirement P1-4): git
    /// on PATH, lockfile entries no longer declared in the manifest, installed addons whose own
    /// <c>plugin.cfg</c> disagrees with what's locked, and folders under <c>addons/</c> no locked
    /// addon accounts for. Unlike most of this class, a missing manifest doesn't abort the whole
    /// run — it just means the dangling-lockfile-entry check (which needs both files to diff) is
    /// skipped; the other three checks only need the lockfile and disk, so "no dependi.toml" still
    /// gets a useful answer instead of an unhelpful hard failure.
    /// </summary>
    public async Task<DoctorResult> DoctorAsync(string projectDirectory, CancellationToken cancellationToken = default)
    {
        var issues = new List<DoctorIssue>();

        if (!await _gitClient.IsAvailableAsync(cancellationToken))
        {
            issues.Add(new DoctorIssue(
                DoctorIssueKind.GitNotOnPath,
                "'git' was not found on PATH. Git-sourced addons cannot be added, updated, or installed."));
        }

        var manifestPath = _fileSystem.CombinePath(projectDirectory, ManifestFileName);
        var manifest = _fileSystem.FileExists(manifestPath)
            ? ManifestReader.ReadProject(_fileSystem.ReadAllText(manifestPath), manifestPath)
            : null;

        var (lockfile, _) = ReadOrCreateLockfile(projectDirectory);

        if (manifest is not null)
        {
            CheckDanglingLockfileEntries(manifest, lockfile, issues);
        }

        foreach (var locked in lockfile.Addons)
        {
            CheckPluginCfgDrift(projectDirectory, locked, issues);
        }

        CheckOrphanedAddonFolders(projectDirectory, lockfile, issues);

        return new DoctorResult(issues);
    }

    private static void CheckDanglingLockfileEntries(ProjectManifest manifest, DependiLockfile lockfile, List<DoctorIssue> issues)
    {
        foreach (var locked in lockfile.Addons)
        {
            if (!manifest.Addons.ContainsKey(locked.Name))
            {
                issues.Add(new DoctorIssue(
                    DoctorIssueKind.DanglingLockfileEntry,
                    $"'{locked.Name}' is locked but no longer declared in {ManifestFileName} — run "
                    + $"'dependi remove {locked.Name}' to clean it up, or re-add the [addons.{locked.Name}] "
                    + "entry if that was unintentional."));
            }
        }

        foreach (var locked in lockfile.Dotnet)
        {
            if (!manifest.Dotnet.ContainsKey(locked.Name))
            {
                issues.Add(new DoctorIssue(
                    DoctorIssueKind.DanglingLockfileEntry,
                    $"'{locked.Name}' (NuGet) is locked but no longer declared in {ManifestFileName} — run "
                    + $"'dependi remove {locked.Name}' to clean it up, or re-add the [dotnet.{locked.Name}] "
                    + "entry if that was unintentional."));
            }
        }
    }

    private void CheckPluginCfgDrift(string projectDirectory, LockedAddon locked, List<DoctorIssue> issues)
    {
        if (locked.Version is null || locked.Version.StartsWith("0.0.0-", StringComparison.Ordinal))
        {
            // A pseudo-version (untagged repo, rev-pinned) has no real upstream release version to
            // compare plugin.cfg against — comparing would just flag every such addon, always.
            return;
        }

        // Same "addons/<name>", not locked.Path, reasoning as Status() above.
        var pluginCfgPath = _fileSystem.CombinePath(projectDirectory, "addons", locked.Name, "plugin.cfg");
        var cfgVersion = PluginCfgReader.TryReadVersion(_fileSystem, pluginCfgPath);
        if (cfgVersion is null)
        {
            return;
        }

        if (!AddonVersion.TryParse(locked.Version, out var lockedVersion) || lockedVersion is null
            || !AddonVersion.TryParse(cfgVersion, out var cfgParsedVersion) || cfgParsedVersion is null)
        {
            return;
        }

        if (lockedVersion.CompareTo(cfgParsedVersion) != 0)
        {
            issues.Add(new DoctorIssue(
                DoctorIssueKind.PluginCfgVersionDrift,
                $"'{locked.Name}': its plugin.cfg reports version '{cfgVersion}', but the lockfile has "
                + $"'{locked.Version}'."));
        }
    }

    private void CheckOrphanedAddonFolders(string projectDirectory, DependiLockfile lockfile, List<DoctorIssue> issues)
    {
        var addonsPath = _fileSystem.CombinePath(projectDirectory, "addons");
        if (!_fileSystem.DirectoryExists(addonsPath))
        {
            return;
        }

        var lockedNames = lockfile.Addons.Select(a => a.Name).ToHashSet(StringComparer.Ordinal);

        foreach (var dir in _fileSystem.EnumerateDirectories(addonsPath))
        {
            var name = dir.Replace('\\', '/').TrimEnd('/').Split('/').Last();
            if (!lockedNames.Contains(name))
            {
                issues.Add(new DoctorIssue(
                    DoctorIssueKind.OrphanedAddonFolder,
                    $"'addons/{name}' is on disk but isn't tracked by any locked addon — a leftover from a "
                    + "manual copy, or a rename that didn't clean up the old folder?"));
            }
        }
    }

    /// <summary>
    /// Checks every locked dependency against its upstream source without cloning or restoring
    /// anything (SPEC.md §6, requirement P1-3) — <c>version</c>/<c>branch</c>-sourced addons via
    /// <c>git ls-remote</c>, dotnet packages via a NuGet metadata query. <c>rev</c>-sourced addons are
    /// skipped entirely: a hard pin has nothing to be "outdated" against. An entry locked but no
    /// longer declared in the manifest is also skipped here — that drift is <c>doctor</c>'s job to
    /// flag (<see cref="DoctorAsync"/>), not <c>outdated</c>'s.
    /// </summary>
    public async Task<OutdatedResult> OutdatedAsync(string projectDirectory, CancellationToken cancellationToken = default)
    {
        var (manifest, _) = ReadManifestOrThrow(projectDirectory);
        var (lockfile, _) = ReadOrCreateLockfile(projectDirectory);

        var addonResults = new List<AddonOutdatedInfo>();
        foreach (var locked in lockfile.Addons)
        {
            if (!manifest.Addons.TryGetValue(locked.Name, out var spec))
            {
                continue;
            }

            var info = await CheckAddonOutdatedOrThrowAsync(spec, locked, cancellationToken);
            if (info is not null)
            {
                addonResults.Add(info);
            }
        }

        var nuGetClient = _nuGetClientFactory.Create(manifest.NuGetSource);
        var dotnetResults = new List<DotnetOutdatedInfo>();
        foreach (var locked in lockfile.Dotnet)
        {
            if (!manifest.Dotnet.ContainsKey(locked.Name))
            {
                continue;
            }

            var latest = await NuGetVersionSelector.CheckForNewerStableAsync(nuGetClient, locked.Name, locked.Resolved, cancellationToken);
            dotnetResults.Add(new DotnetOutdatedInfo(locked.Name, locked.Resolved, latest, IsOutdated: latest is not null));
        }

        return new OutdatedResult(addonResults, dotnetResults);
    }

    private async Task<AddonOutdatedInfo?> CheckAddonOutdatedOrThrowAsync(AddonSpec spec, LockedAddon locked, CancellationToken cancellationToken)
    {
        try
        {
            return await CheckAddonOutdatedAsync(spec, locked, cancellationToken);
        }
        catch (GitCommandException ex)
        {
            throw new WorkflowException($"'{spec.Name}': {ex.Message}", ex);
        }
    }

    private async Task<AddonOutdatedInfo?> CheckAddonOutdatedAsync(AddonSpec spec, LockedAddon locked, CancellationToken cancellationToken)
    {
        switch (spec.SourceKind)
        {
            case AddonSourceKind.Rev:
                // A hard pin names the exact commit already — nothing upstream to compare it to.
                return null;

            case AddonSourceKind.Branch:
            {
                var tip = await _gitClient.ResolveBranchTipAsync(spec.Git, spec.Branch!, cancellationToken);
                var isOutdated = !string.Equals(tip, locked.Resolved, StringComparison.OrdinalIgnoreCase);
                return new AddonOutdatedInfo(
                    locked.Name, ShortSha(locked.Resolved), isOutdated ? ShortSha(tip) : null, isOutdated);
            }

            case AddonSourceKind.Version:
            {
                if (!VersionConstraint.TryParse(spec.Version!, out var constraint) || constraint is null)
                {
                    // The manifest wouldn't have resolved successfully in the first place with an
                    // invalid constraint — nothing meaningful to report if this somehow happens.
                    return null;
                }

                var refs = await _gitClient.ListRemoteRefsAsync(spec.Git, cancellationToken);
                var match = TagSelector.SelectHighestMatching(refs, constraint);
                if (match is null)
                {
                    // Nothing currently satisfies the constraint (e.g. the matching tag was
                    // deleted upstream since locking) — outdated has nothing newer to offer.
                    return null;
                }

                var isOutdated = !string.Equals(match.Ref.CommitSha, locked.Resolved, StringComparison.OrdinalIgnoreCase);
                var current = locked.Version ?? ShortSha(locked.Resolved);
                return new AddonOutdatedInfo(locked.Name, current, isOutdated ? match.Version.OriginalTag : null, isOutdated);
            }

            default:
                throw new UnreachableException($"Unknown {nameof(AddonSourceKind)}: {spec.SourceKind}");
        }
    }

    private static string ShortSha(string sha) => sha.Length > 7 ? sha[..7] : sha;

    private async Task<string> ResolveDotnetVersionOrThrowAsync(
        INuGetClient nuGetClient, string packageId, string? versionOverride, CancellationToken cancellationToken)
    {
        if (versionOverride is not null)
        {
            if (!await NuGetVersionSelector.ExistsAsync(nuGetClient, packageId, versionOverride, cancellationToken))
            {
                throw new WorkflowException($"'{packageId}' version '{versionOverride}' was not found on NuGet.");
            }

            return versionOverride;
        }

        var latest = await NuGetVersionSelector.SelectLatestStableAsync(nuGetClient, packageId, cancellationToken);
        if (latest is null)
        {
            throw new WorkflowException(
                $"'{packageId}' has no stable release on NuGet — pass an explicit version (including "
                + "prerelease) if you want to add it anyway.");
        }

        return latest;
    }

    private async Task<LockedDotnet> InstallDotnetOrThrowAsync(
        string projectDirectory, string packageId, string version, string? nuGetSourceUrl, bool restore, CancellationToken cancellationToken)
    {
        try
        {
            return await _dotnetInstaller.InstallAsync(projectDirectory, packageId, version, nuGetSourceUrl, restore, cancellationToken);
        }
        catch (DotnetSyncException ex)
        {
            throw new WorkflowException($"'{packageId}': {ex.Message}", ex);
        }
    }

    private async Task RestoreDotnetOrThrowAsync(string projectDirectory, string? nuGetSourceUrl, CancellationToken cancellationToken)
    {
        try
        {
            await _dotnetInstaller.RestoreAsync(projectDirectory, nuGetSourceUrl, cancellationToken);
        }
        catch (DotnetSyncException ex)
        {
            throw new WorkflowException(ex.Message, ex);
        }
    }

    private async Task<AddonSpec> BuildDefaultSpecAsync(string name, string gitUrl, string? pathOverride, CancellationToken cancellationToken)
    {
        var refs = await _gitClient.ListRemoteRefsAsync(gitUrl, cancellationToken);

        var highestTag = refs
            .Where(r => r.Kind == GitRefKind.Tag)
            .Select(r => (Ref: r, Version: AddonVersion.TryParse(r.Name, out var v) ? v : null))
            .Where(t => t.Version is not null && !t.Version!.IsPrerelease)
            .OrderByDescending(t => t.Version)
            .FirstOrDefault();

        if (highestTag.Version is not null)
        {
            var minorComponents = highestTag.Version.Components.Take(Math.Min(2, highestTag.Version.Components.Count));
            return new AddonSpec
            {
                Name = name, Git = gitUrl, Version = "~" + string.Join('.', minorComponents), Path = pathOverride,
            };
        }

        // No usable release tag — pin the default branch's current tip as a hard `rev` instead
        // (SPEC.md §6: "No tags found -> pin default-branch tip as rev and print a hint").
        var branch = await _gitClient.ResolveDefaultBranchAsync(gitUrl, cancellationToken);
        var sha = await _gitClient.ResolveBranchTipAsync(gitUrl, branch, cancellationToken);
        return new AddonSpec { Name = name, Git = gitUrl, Rev = sha, Path = pathOverride };
    }

    private async Task<ResolvedAddon> ResolveOrThrowAsync(AddonSpec spec, CancellationToken cancellationToken)
    {
        try
        {
            return await _resolver.ResolveAsync(spec, cancellationToken);
        }
        catch (AddonResolutionException ex)
        {
            throw new WorkflowException(ex.Message, ex);
        }
        catch (GitCommandException ex)
        {
            throw new WorkflowException($"'{spec.Name}': {ex.Message}", ex);
        }
    }

    private async Task<LockedAddon> InstallAddonOrThrowAsync(
        ResolvedAddon resolved, string projectDirectory, LockedAddon? previous, bool force, CancellationToken cancellationToken)
    {
        try
        {
            return await _addonInstaller.InstallAsync(resolved, projectDirectory, previous, force, cancellationToken);
        }
        catch (SyncException ex)
        {
            throw new WorkflowException(ex.Message, ex);
        }
        catch (GitCommandException ex)
        {
            throw new WorkflowException($"'{resolved.Name}': {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Walks and installs the full addon dependency graph (SPEC.md §8, requirement P1-2) reachable
    /// from <paramref name="manifest"/>'s top-level <c>[addons.*]</c> entries, then reconciles it
    /// against <paramref name="lockfile"/>: names in <paramref name="freshNames"/> are resolved live
    /// (their spec is re-evaluated against real git tags/branches); every other already-locked
    /// top-level addon is pinned to exactly its current commit for this walk (via a synthetic
    /// <c>rev</c> spec built from the lockfile), so it still fully participates in discovery and
    /// unification — catching any real conflict a newly-resolved addon's dependencies introduce — but
    /// can never be silently moved forward by an operation that didn't ask to touch it. Because the
    /// walk this produces is always exhaustive (every top-level addon is a root, fresh or pinned),
    /// any previously-locked addon the walk doesn't reach is a genuine orphan and gets deleted from
    /// disk and dropped from the result.
    /// </summary>
    /// <remarks>
    /// One deliberate simplification: "frozen" only applies to a root's own identity. Once a root
    /// (fresh or pinned) is discovered, everything <em>it</em> transitively requires is still
    /// resolved against live specs, not frozen — so a transitive dependency shared with an untouched
    /// addon can still float forward if a newer compatible tag exists upstream, even during an
    /// operation that only asked to touch a different, unrelated addon. This is consistent with
    /// SPEC.md §8's explicit "not a full constraint solver" scope and mirrors how most real package
    /// managers let transitive versions shift slightly on any resolve, while top-level identities
    /// stay put unless asked to move.
    /// </remarks>
    private async Task<(IReadOnlyList<LockedAddon> Addons, IReadOnlyList<DotnetSpec> AddonDeclaredDotnet)> SyncAddonGraphAsync(
        string projectDirectory,
        ProjectManifest manifest,
        DependiLockfile lockfile,
        IReadOnlySet<string> freshNames,
        bool force,
        CancellationToken cancellationToken)
    {
        var previousAddonsByName = lockfile.Addons.ToDictionary(a => a.Name, StringComparer.Ordinal);

        var rootSpecs = new Dictionary<string, AddonSpec>(StringComparer.Ordinal);
        foreach (var (addonName, spec) in manifest.Addons)
        {
            if (freshNames.Contains(addonName) || !previousAddonsByName.TryGetValue(addonName, out var previousLocked))
            {
                rootSpecs[addonName] = spec;
            }
            else
            {
                rootSpecs[addonName] = new AddonSpec
                {
                    Name = addonName, Git = previousLocked.Git, Rev = previousLocked.Resolved, Path = previousLocked.Path,
                };
            }
        }

        var discoveryRoot = _fileSystem.CombinePath(projectDirectory, ".dependi", "staging", "graph-discovery");
        var graphResolver = new DependencyGraphResolver(_fileSystem, _gitClient, _repoCache, discoveryRoot);

        GraphResolutionResult graph;
        try
        {
            graph = await graphResolver.ResolveGraphAsync(rootSpecs, manifest.Overrides, cancellationToken);
        }
        catch (AddonResolutionException ex)
        {
            throw new WorkflowException(ex.Message, ex);
        }
        catch (GitCommandException ex)
        {
            throw new WorkflowException(ex.Message, ex);
        }

        var resultByName = new Dictionary<string, LockedAddon>(StringComparer.Ordinal);
        foreach (var entry in graph.Addons)
        {
            var addonName = entry.Resolved.Name;
            previousAddonsByName.TryGetValue(addonName, out var previous);

            var unchanged = previous is not null
                && !freshNames.Contains(addonName)
                && previous.Git == entry.Resolved.Git
                && previous.Resolved == entry.Resolved.ResolvedSha;

            resultByName[addonName] = unchanged
                ? previous!
                : WithDependencies(
                    await InstallAddonOrThrowAsync(entry.Resolved, projectDirectory, previous, force, cancellationToken),
                    entry.DependencyNames);
        }

        foreach (var orphan in previousAddonsByName.Values.Where(a => !resultByName.ContainsKey(a.Name)))
        {
            _fileSystem.DeleteDirectory(_fileSystem.CombinePath(projectDirectory, "addons", orphan.Name));
        }

        return (resultByName.Values.ToList(), graph.AddonDeclaredDotnet);
    }

    /// <summary>
    /// The dotnet-side twin of <see cref="SyncAddonGraphAsync"/>'s "fresh vs. frozen, prune-or-keep"
    /// pattern: <paramref name="targetNames"/> are (re-)resolved to latest-stable and installed if
    /// they changed; every other previously-locked package not in <paramref name="targetNames"/> is
    /// either dropped (<paramref name="pruneUnreachable"/> — used for a full <c>update</c>, where the
    /// full picture of what's still declared/reachable is known) or carried forward untouched
    /// (used by <c>add</c> and a single-name <c>update</c>, which must never silently drop or move an
    /// unrelated dotnet package).
    /// </summary>
    private async Task<IReadOnlyList<LockedDotnet>> SyncDotnetAsync(
        string projectDirectory,
        ProjectManifest manifest,
        IReadOnlyList<LockedDotnet> previousDotnet,
        IReadOnlySet<string> targetNames,
        bool pruneUnreachable,
        CancellationToken cancellationToken)
    {
        var nuGetClient = _nuGetClientFactory.Create(manifest.NuGetSource);
        var previousByName = previousDotnet.ToDictionary(d => d.Name, StringComparer.Ordinal);
        var resultByName = new Dictionary<string, LockedDotnet>(StringComparer.Ordinal);
        var anyChanged = false;

        foreach (var targetName in targetNames)
        {
            previousByName.TryGetValue(targetName, out var previous);

            var version = await ResolveDotnetVersionOrThrowAsync(nuGetClient, targetName, versionOverride: null, cancellationToken);
            var changed = previous?.Resolved != version;

            resultByName[targetName] = changed
                ? await InstallDotnetOrThrowAsync(projectDirectory, targetName, version, manifest.NuGetSource, restore: false, cancellationToken)
                : previous!;

            anyChanged |= changed;
        }

        if (pruneUnreachable)
        {
            foreach (var orphanName in previousByName.Keys.Where(n => !targetNames.Contains(n)))
            {
                try
                {
                    _dotnetInstaller.Remove(projectDirectory, orphanName);
                }
                catch (DotnetSyncException ex)
                {
                    throw new WorkflowException(ex.Message, ex);
                }

                anyChanged = true;
            }
        }
        else
        {
            foreach (var (packageId, previous) in previousByName)
            {
                if (!targetNames.Contains(packageId))
                {
                    resultByName[packageId] = previous;
                }
            }
        }

        if (anyChanged)
        {
            await RestoreDotnetOrThrowAsync(projectDirectory, manifest.NuGetSource, cancellationToken);
        }

        return resultByName.Values.ToList();
    }

    /// <summary>BFS over the lockfile's own recorded <see cref="LockedAddon.Dependencies"/> edges,
    /// starting from <paramref name="rootNames"/> — lets <see cref="RemoveAsync"/> compute exactly
    /// which locked addons are still needed after a removal without any network access (SPEC.md §8:
    /// "the lockfile stores graph edges so install replays the graph without network access to
    /// manifests" — this is the same property applied to pruning).</summary>
    private static HashSet<string> ComputeReachableAddonNames(
        IReadOnlyDictionary<string, LockedAddon> lockedByName, IEnumerable<string> rootNames)
    {
        var reachable = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>();

        foreach (var rootName in rootNames)
        {
            if (lockedByName.ContainsKey(rootName) && reachable.Add(rootName))
            {
                queue.Enqueue(rootName);
            }
        }

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            foreach (var child in lockedByName[current].Dependencies)
            {
                if (lockedByName.ContainsKey(child) && reachable.Add(child))
                {
                    queue.Enqueue(child);
                }
            }
        }

        return reachable;
    }

    private static LockedAddon WithDependencies(LockedAddon locked, IReadOnlyList<string> dependencyNames) => new()
    {
        Name = locked.Name,
        Git = locked.Git,
        Resolved = locked.Resolved,
        Version = locked.Version,
        Path = locked.Path,
        ContentHash = locked.ContentHash,
        PatchHash = locked.PatchHash,
        PatchBaseSha = locked.PatchBaseSha,
        Dependencies = dependencyNames,
    };

    private (ProjectManifest Manifest, string Path) ReadManifestOrThrow(string projectDirectory)
    {
        var path = _fileSystem.CombinePath(projectDirectory, ManifestFileName);
        if (!_fileSystem.FileExists(path))
        {
            throw new WorkflowException($"No {ManifestFileName} found in {projectDirectory}. Run 'dependi init' first.");
        }

        return (ManifestReader.ReadProject(_fileSystem.ReadAllText(path), path), path);
    }

    private (DependiLockfile Lockfile, string Path) ReadOrCreateLockfile(string projectDirectory)
    {
        var path = _fileSystem.CombinePath(projectDirectory, LockfileFileName);
        if (!_fileSystem.FileExists(path))
        {
            return (new DependiLockfile(), path);
        }

        return (LockfileReader.Read(_fileSystem.ReadAllText(path), path), path);
    }

    private (DependiLockfile Lockfile, string Path) ReadLockfileOrThrow(string projectDirectory)
    {
        var path = _fileSystem.CombinePath(projectDirectory, LockfileFileName);
        if (!_fileSystem.FileExists(path))
        {
            throw new WorkflowException($"No {LockfileFileName} found in {projectDirectory}. Run 'dependi add' first.");
        }

        return (LockfileReader.Read(_fileSystem.ReadAllText(path), path), path);
    }

    private void WriteManifest(string manifestPath, ProjectManifest updated) =>
        _fileSystem.WriteAllText(manifestPath, ManifestWriter.Serialize(updated));

    private void WriteLockfile(string lockfilePath, DependiLockfile updated) =>
        _fileSystem.WriteAllText(lockfilePath, LockfileWriter.Serialize(updated));

    /// <summary>Derives a manifest key from a git URL, e.g.
    /// <c>"https://github.com/nathanhoad/godot_dialogue_manager.git"</c> -> <c>"godot_dialogue_manager"</c>.
    /// <c>add</c> takes no separate name argument (SPEC.md §6 CLI table), so the URL is the only
    /// source of a name.</summary>
    internal static string DeriveAddonName(string gitUrl)
    {
        var trimmed = gitUrl.TrimEnd('/');
        var lastSeparator = trimmed.LastIndexOfAny(['/', ':']);
        var last = lastSeparator < 0 ? trimmed : trimmed[(lastSeparator + 1)..];
        return last.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? last[..^4] : last;
    }
}
