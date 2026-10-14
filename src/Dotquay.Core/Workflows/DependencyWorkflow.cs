using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Dotquay.Core.Abstractions;
using Dotquay.Core.Auth;
using Dotquay.Core.Doctor;
using Dotquay.Core.Dotnet;
using Dotquay.Core.Git;
using Dotquay.Core.IO;
using Dotquay.Core.Local;
using Dotquay.Core.Lockfile;
using Dotquay.Core.Manifest;
using Dotquay.Core.Patch;
using Dotquay.Core.Progress;
using Dotquay.Core.Resolve;
using Dotquay.Core.Sync;
using Dotquay.Core.Versioning;

namespace Dotquay.Core.Workflows;

/// <summary>
/// Orchestrates <c>dotquay add</c>/<c>install</c>/<c>remove</c>/<c>update</c>/<c>list</c>: the
/// CLI-agnostic composition of manifest/lockfile I/O with <see cref="AddonResolver"/>/
/// <see cref="AddonInstaller"/> for git addons (SPEC.md §5) and <see cref="NuGetVersionSelector"/>/
/// <see cref="DotnetInstaller"/> for NuGet packages (SPEC.md §11). CLI commands should be a thin
/// translation of parsed args into calls here plus console output — no orchestration logic in
/// Dotquay.Cli itself (SPEC.md §12).
/// </summary>
public sealed partial class DependencyWorkflow
{
    private const string ManifestFileName = ProjectFileStore.ManifestFileName;
    private const string LockfileFileName = ProjectFileStore.LockfileFileName;

    private readonly IFileSystem _fileSystem;
    private readonly ProjectFileStore _projectFiles;
    private readonly InstalledPackageManifests _installedManifests;
    private readonly LocalLinks _localLinks;

    /// <summary>Where long operations announce themselves. Defaults to discarding, so nothing had to
    /// change at any existing call site to add progress (SPEC.md §12).</summary>
    private IProgressReporter _progress = NullProgressReporter.Instance;
    private readonly IGitClient _gitClient;
    private readonly INuGetClientFactory _nuGetClientFactory;
    private readonly AddonResolver _resolver;
    private readonly AddonInstaller _addonInstaller;
    private readonly RepoCache _repoCache;
    private readonly IPatchTool _patchTool;
    private readonly DotnetInstaller _dotnetInstaller;

    /// <summary>
    /// Consulted when an addon declares a NuGet feed whose host isn't in the project's
    /// <c>[nuget] trusted_hosts</c> (SPEC.md §11.3). Defaults to refusing everything.
    /// </summary>
    /// <remarks>
    /// An init-only property rather than another constructor parameter: this type already has six
    /// constructors forming a test-seam ladder, and threading an optional dependency through all of
    /// them would obscure the ones that matter. Defaulting to
    /// <see cref="DenyAllSourceApproval"/> means every caller that hasn't thought about it — the
    /// editor plugin, CI, anything driving <c>--json</c> — is safe by omission rather than by
    /// remembering.
    /// </remarks>
    public ISourceApproval SourceApproval { get; init; } = DenyAllSourceApproval.Instance;

    /// <summary>
    /// Who decides whether a source generator being added is kept private to this project (v0.14).
    /// </summary>
    /// <remarks>
    /// Same seam-and-safe-default shape as <see cref="SourceApproval"/>, with the safe default in the
    /// other direction: <see cref="ShareGeneratorDecision"/> writes nothing, which is exactly what
    /// every release before v0.14 did. So a caller that never thought about this — the plugin, CI,
    /// <c>--json</c>, <c>install</c> replaying a lockfile — keeps its existing behaviour byte for byte.
    /// </remarks>
    public IGeneratorAssetDecision GeneratorAssetDecision { get; init; } = ShareGeneratorDecision.Instance;

    /// <summary>
    /// Who confirms dotquay's guess at where an addon lives inside its own repository, when the
    /// guessed <c>addons/&lt;name&gt;</c> subtree isn't there (v0.20).
    /// </summary>
    /// <remarks>
    /// Same seam-and-safe-default shape as <see cref="SourceApproval"/>, and the same direction of
    /// safety: <see cref="DeclineSubtreeChoice"/> installs nothing from a folder nobody named, so a
    /// caller that never thought about this keeps failing exactly where it used to — just with a
    /// message that now names the flags that would fix it. Only <c>add</c> ever consults it, and only
    /// when no <c>--path</c> was given.
    /// </remarks>
    public ISubtreeChoice SubtreeChoice { get; init; } = DeclineSubtreeChoice.Instance;

    /// <summary>
    /// How this workflow reads the process environment. Defaults to the real one; overridden in
    /// tests, which must not depend on the machine they run on having (or not having) a known_hosts
    /// file. Named for its type rather than the shorter "Environment", which would shadow
    /// <see cref="System.Environment"/> inside this class and quietly break the next use of it.
    /// </summary>
    /// <remarks>
    /// An init property rather than a constructor parameter, matching <see cref="SourceApproval"/>:
    /// four constructor overloads already chain through each other, and every call site in the CLI
    /// and the test suite passes positionally.
    /// </remarks>
    public IEnvironmentReader EnvironmentReader { get; init; } = new SystemEnvironmentReader();

    public DependencyWorkflow(IFileSystem fileSystem, IGitClient gitClient, string cacheRoot)
        : this(fileSystem, gitClient, new NuGetV3ClientFactory(DefaultCredentialResolver(fileSystem)), cacheRoot)
    {
    }

    /// <summary>The real credential stack: <c>dotquay login</c>'s store plus the environment
    /// (SPEC.md §11.2). Falls back to environment-only when there is no home directory to put a
    /// credential file in — a container with no <c>$HOME</c> is a normal CI shape, and it should use
    /// <c>DOTQUAY_NUGET_*</c> rather than fail at construction.</summary>
    private static CredentialResolver DefaultCredentialResolver(IFileSystem fileSystem)
    {
        var environment = new SystemEnvironmentReader();

        ICredentialStore store = environment.HomeDirectory is null
            ? new EmptyCredentialStore()
            : new TomlCredentialStore(fileSystem, TomlCredentialStore.DefaultPath(environment, fileSystem));

        return new CredentialResolver(environment, store);
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
            new DotnetInstaller(fileSystem, new ProcessDotnetCliRunner(), DefaultCredentialResolver(fileSystem)))
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
            new NuGetV3ClientFactory(DefaultCredentialResolver(fileSystem)),
            addonInstaller,
            new RepoCache(fileSystem, gitClient, cacheRoot: "unused-by-this-seam"),
            new ProcessPatchTool(),
            new DotnetInstaller(fileSystem, new ProcessDotnetCliRunner(), DefaultCredentialResolver(fileSystem)))
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
        _projectFiles = new ProjectFileStore(fileSystem);
        _installedManifests = new InstalledPackageManifests(fileSystem);
        _localLinks = new LocalLinks(fileSystem);
        _gitClient = gitClient;
        _nuGetClientFactory = nuGetClientFactory;
        _resolver = new AddonResolver(gitClient);
        _addonInstaller = addonInstaller;
        _repoCache = repoCache;
        _patchTool = patchTool;
        _dotnetInstaller = dotnetInstaller;
    }

    /// <summary>
    /// Asks the remote what a bare ref means, so <c>dotquay add &lt;url&gt;#main</c> can fill in the
    /// right manifest field (SPEC.md §6).
    /// </summary>
    /// <remarks>
    /// Lives here rather than in the CLI because it needs a git client, and because the Godot plugin
    /// will want the same shorthand eventually — the CLI should not be the only thing that knows how
    /// to read a ref.
    /// </remarks>
    public async Task<AddonAddOptions> ResolveShorthandAsync(
        string gitUrl, string reference, AddonAddOptions options, CancellationToken cancellationToken = default)
    {
        if (options.HasExplicitSource)
        {
            throw new WorkflowException(
                $"'{gitUrl}#{reference}' already names a ref, so --version/--branch/--rev would contradict it. "
                + "Use one or the other.");
        }

        try
        {
            var classified = await new RefClassifier(_gitClient).ClassifyAsync(gitUrl, reference, cancellationToken);
            return options.WithSource(classified.Kind, classified.Value);
        }
        catch (AddonResolutionException ex)
        {
            throw new WorkflowException(ex.Message, ex);
        }
        catch (GitCommandException ex)
        {
            throw new WorkflowException($"{gitUrl}: {ex.Message}", ex);
        }
    }

    /// <summary>Attaches a progress reporter for the lifetime of this workflow instance. Fluent so a
    /// CLI command can write <c>WorkflowFactory.Create().WithProgress(reporter)</c> in one line.</summary>
    public DependencyWorkflow WithProgress(IProgressReporter progress)
    {
        _progress = progress;
        return this;
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
        string projectDirectory,
        string gitUrl,
        AddonAddOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new AddonAddOptions();
        options.Validate();

        var (manifest, manifestPath) = ReadManifestOrThrow(projectDirectory);

        // previewing: false — this one may ask, and refuses rather than installing from a folder
        // nobody named. The suggestion it may have applied is already in `spec`, and is written to
        // the manifest below with everything else.
        var (name, spec, _) = await PrepareAddSpecAsync(
            manifest, manifestPath, gitUrl, options, previewing: false, cancellationToken);

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
        var graphSync = await SyncAddonGraphAsync(
            projectDirectory, updatedManifest, lockfile, freshNames: new HashSet<string>(StringComparer.Ordinal) { name },
            force: options.Force, cancellationToken);

        var syncedAddons = graphSync.Addons;
        var addonDeclaredDotnet = graphSync.AddonDeclaredDotnet;

        // Adopted feeds have to reach the manifest *before* dotnet resolution, or the packages the
        // new addon declares would be looked for on the feeds the project had a moment ago.
        IReadOnlyDictionary<string, string> refusalHints;
        (updatedManifest, refusalHints) = ApplyAdoption(updatedManifest, graphSync.Adoption);

        // Persisted the moment the graph sync finishes, not after dotnet resolution succeeds.
        // SyncAddonGraphAsync has *already* swapped every one of these addons onto disk — so if the
        // dotnet step below throws (a missing stable release, a refused feed, a network error), a
        // manifest/lockfile that still don't know about them would make the very next attempt see
        // real, correctly-installed folders as untracked. DirtyChecker treats "occupied but
        // untracked" as hand-edited content it must not silently overwrite (SPEC.md P0-6), so the
        // retry fails with "local modifications" against content dotquay itself just put there.
        // Same reasoning already applied to the adoption write above; it just stopped one step short.
        WriteManifest(manifestPath, updatedManifest);
        WriteLockfile(lockfilePath, lockfile with { Addons = syncedAddons });

        // Same "don't move what wasn't asked to move" treatment for dotnet: only genuinely new
        // addon-declared packages get resolved; anything already known (project-declared or already
        // locked from an earlier add) is left exactly as it was (requirement P1-5).
        var newDotnetNames = addonDeclaredDotnet
            .Select(s => s.Name)
            .Where(n => !updatedManifest.Dotnet.ContainsKey(n) && lockfile.Dotnet.All(d => d.Name != n))
            .ToHashSet(StringComparer.Ordinal);
        var syncedDotnet = await SyncDotnetAsync(
            projectDirectory, updatedManifest, lockfile.Dotnet, newDotnetNames, pruneUnreachable: false, cancellationToken,
            refusalHints, addonDeclaredDotnet, graphSync.AddonDotnetRequirements);

        WriteLockfile(lockfilePath, lockfile with { Addons = syncedAddons, Dotnet = syncedDotnet });

        return syncedAddons.First(a => a.Name == name);
    }

    /// <summary>
    /// Adds a new NuGet dependency: queries the latest stable version (or the highest inside an
    /// explicit <paramref name="versionOverride"/> range), writes a <c>PackageReference</c> into
    /// the project's <c>.csproj</c>, runs <c>dotnet restore</c>, and updates the manifest and
    /// lockfile (SPEC.md §11, requirement P0-7).
    /// </summary>
    /// <param name="versionOverride">Any range in the constraint language (DESIGN-v0.21), written to
    /// the manifest as typed. Without one the manifest gets <c>^&lt;resolved&gt;</c>, so <c>update</c>
    /// picks up compatible releases (§3.6).</param>
    /// <param name="pinnedSourceName">A declared <c>[nuget.sources.*]</c> name to resolve from, instead
    /// of searching every feed in order (v0.14's <c>--source</c>). Written to the manifest as
    /// <c>source = "..."</c>, so the pin survives into every later resolve.</param>
    public async Task<LockedDotnet> AddDotnetAsync(
        string projectDirectory,
        string packageId,
        string? versionOverride = null,
        string? pinnedSourceName = null,
        CancellationToken cancellationToken = default)
    {
        var (manifest, manifestPath) = ReadManifestOrThrow(projectDirectory);

        if (manifest.Dotnet.ContainsKey(packageId))
        {
            throw new WorkflowException(
                $"'{packageId}' is already declared in {manifestPath}. Use 'dotquay update {packageId}' instead.");
        }

        var sourceSet = manifest.SourceSet();

        // Checked before the first network call, and phrased for the flag the user actually typed:
        // NuGetSourceSet.Resolve would raise this too, but as a message about a `source = "..."` key
        // the user has not written yet.
        ThrowIfUnknownSource(sourceSet, pinnedSourceName);
        var constraint = ParseDotnetConstraintOrThrow(packageId, versionOverride);

        var nuGetClient = _nuGetClientFactory.Create(sourceSet, pinnedSourceName);
        var (version, resolvedSource) = await ResolveDotnetVersionOrThrowAsync(
            nuGetClient, packageId, constraint, cancellationToken);
        var assets = await DecideGeneratorAssetsAsync(
            projectDirectory, nuGetClient, packageId, version, cancellationToken);

        // Read before the install, not after: the install rewrites NuGet.Config's
        // <packageSourceMapping> wholesale, and the lockfile is the only record of which feed every
        // other package resolved from. Without it that rewrite names the package being added and
        // nothing else, silently un-mapping every other private package in the project.
        var (lockfile, lockfilePath) = ReadOrCreateLockfile(projectDirectory);

        var locked = await InstallDotnetRestoringOnFailureAsync(
            projectDirectory,
            () => InstallDotnetOrThrowAsync(
                projectDirectory, packageId, version, manifest, lockfile.Dotnet, restore: true, resolvedSource,
                cancellationToken, assets));

        WriteManifest(manifestPath, manifest with
        {
            Dotnet = new Dictionary<string, DotnetSpec>(manifest.Dotnet)
            {
                // The resolved feed is recorded in the lockfile, not pinned in the manifest. A pin is
                // a statement of intent the user makes; writing one automatically would silently
                // freeze whichever feed happened to answer first today. An explicit --source is
                // exactly that statement, so it is the one thing written here.
                [packageId] = new DotnetSpec
                {
                    Name = packageId,
                    Version = versionOverride ?? CompatibleRange(version),
                    Source = pinnedSourceName,
                    Assets = assets,
                },
            },
        });

        WriteLockfile(lockfilePath, lockfile with
        {
            Dotnet = lockfile.Dotnet.Where(d => d.Name != packageId).Append(locked).ToList(),
        });

        return locked;
    }

    /// <summary>
    /// Asks whether a newly added source generator should be kept private to this project, and turns
    /// the answer into asset metadata.
    /// </summary>
    /// <remarks>
    /// <para>Two gates before anyone is asked anything, and both matter. The package must have declared
    /// itself a development dependency — a missing signal is never read as a generator, or the question
    /// would fire on every ordinary library. And the project must look like something other projects
    /// consume, because a Godot game is the end of the line: nothing depends on it, so whether the
    /// generator flows onward has no consequence and no right answer to get wrong. Both quiet paths
    /// write nothing, which is what NuGet does anyway.</para>
    /// <para>Only <c>private_assets</c> is ever written, and only to keep the generator in. "Share it
    /// onward" needs no declaration at all: measured 2026-09-10 three levels deep with a generated
    /// internal type, a generator behind a plain <c>PackageReference</c> really does run in a downstream
    /// consumer, so the default already shares and only containment is a statement worth recording.
    /// <c>include_assets</c>/<c>exclude_assets</c> stay hand-written — the case that needs them is a
    /// package shipping both <c>lib/</c> and <c>analyzers/</c>, which is exactly what a nuspec-only
    /// signal cannot see.</para>
    /// </remarks>
    private async Task<PackageAssets> DecideGeneratorAssetsAsync(
        string projectDirectory,
        INuGetClient nuGetClient,
        string packageId,
        string version,
        CancellationToken cancellationToken)
    {
        var shape = await nuGetClient.GetPackageShapeAsync(packageId, version, cancellationToken);

        if (!shape.IsGenerator)
        {
            return PackageAssets.None;
        }

        // Probed rather than asked, and a failure to find or parse the .csproj reads as "not
        // publishable" — the quiet answer, which leaves behaviour as it was.
        var evidence = CsprojPackable.Evidence(_fileSystem, CsprojLocator.Locate(_fileSystem, projectDirectory));

        if (evidence is null)
        {
            return PackageAssets.None;
        }

        return GeneratorAssetDecision.KeepPrivate(new GeneratorAssetRequest(packageId, version, evidence))
            ? new PackageAssets { Private = ["all"] }
            : PackageAssets.None;
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
                + $"locked) — run 'dotquay add' or 'dotquay update' first (requirement P0-3).");
        }

        StagingSweeper.Sweep(_fileSystem, ProjectLayout.WithoutPatches(projectDirectory));

        var installedAddons = new List<LockedAddon>();
        var installedCount = 0;
        foreach (var addon in lockfile.Addons)
        {
            _progress.Report($"installing {addon.Name}", ++installedCount, lockfile.Addons.Count);

            // addon.Folder is part of what was locked: install replays the lockfile exactly (SPEC.md
            // §5), and dropping it here would silently relocate the addon back to the default folder
            // on every fresh clone — the precise opposite of the reproducibility guarantee.
            var resolved = new ResolvedAddon(
                addon.Name, addon.Git, addon.Resolved, addon.Version, addon.Path, addon.Folder);
            installedAddons.Add(await InstallAddonOrThrowAsync(resolved, projectDirectory, addon, force, cancellationToken));
        }

        var installedDotnet = new List<LockedDotnet>();
        foreach (var dependency in lockfile.Dotnet)
        {
            // dependency.Source is replayed, not re-resolved: install reproduces the lockfile
            // exactly (SPEC.md §5), and a package that resolved from a private feed must not come
            // from a public one here just because the search order would allow it.
            // Asset metadata is replayed from the lockfile too, not re-read from the manifest: a
            // manifest edit is picked up by 'update', and letting 'install' act on one would break
            // the "install reproduces the lockfile exactly" guarantee for the same reason re-resolving
            // the version would. 'doctor' reports the resulting drift so the edit isn't silent.
            installedDotnet.Add(await InstallDotnetOrThrowAsync(
                projectDirectory, dependency.Name, dependency.Resolved, manifest, lockfile.Dotnet, restore: false,
                dependency.Source, cancellationToken, dependency.Assets, dependency.Assets));
        }

        // Before the restore, not after: restore evaluates the project, and the overlay is what
        // decides whether a linked package resolves from NuGet or from the local project.
        SyncLocalTargets(projectDirectory);

        if (installedDotnet.Count > 0)
        {
            await RestoreDotnetOrThrowAsync(projectDirectory, manifest, cancellationToken, installedDotnet);
        }

        WriteLockfile(lockfilePath, lockfile with { Addons = installedAddons, Dotnet = installedDotnet });
        return new InstallResult(installedAddons, installedDotnet);
    }

    /// <summary>
    /// Re-resolves one dependency (or every addon and dotnet package declared in the manifest, if
    /// <paramref name="name"/> is null) fresh and reinstalls it — unlike <see cref="InstallAsync"/>,
    /// which only ever replays what the lockfile already pinned. Untargeted entries already in the
    /// lockfile are left exactly as they are. Also doubles as the recovery path for something a
    /// teammate hand-added to the manifest without running <c>dotquay add</c>: with no prior lock
    /// entry, this locks and installs it for the first time (SPEC.md §6).
    /// </summary>
    public Task<UpdateResult> UpdateAsync(
        string projectDirectory, string? name, bool force, CancellationToken cancellationToken = default) =>
        UpdateCoreAsync(projectDirectory, name, resync: false, force, cancellationToken);

    /// <summary>
    /// Re-walks and reinstalls the dependency graph without re-resolving anything the user didn't ask
    /// to move: every top-level addon stays pinned to exactly its locked commit, and no NuGet package
    /// is bumped. What can change is what the *graph* now decides — which is precisely what happens
    /// after an <c>[overrides.*]</c> entry is written or cleared (SPEC.md §8). Backs
    /// <c>dotquay override</c> (v0.5) and the editor plugin's conflict picker (SPEC.md §18): applying
    /// a chosen version must not smuggle in unrelated updates, which a plain <c>dotquay update</c>
    /// would.
    /// </summary>
    public Task<UpdateResult> ResyncAsync(string projectDirectory, bool force, CancellationToken cancellationToken = default) =>
        UpdateCoreAsync(projectDirectory, name: null, resync: true, force, cancellationToken);

    private async Task<UpdateResult> UpdateCoreAsync(
        string projectDirectory, string? name, bool resync, bool force, CancellationToken cancellationToken)
    {
        var (manifest, manifestPath) = ReadManifestOrThrow(projectDirectory);
        var (lockfile, lockfilePath) = ReadOrCreateLockfile(projectDirectory);

        if (name is not null && !manifest.Addons.ContainsKey(name) && !manifest.Dotnet.ContainsKey(name))
        {
            throw new WorkflowException($"'{name}' is not declared in {manifestPath}.");
        }

        StagingSweeper.Sweep(_fileSystem, ProjectLayout.WithoutPatches(projectDirectory));

        var previousAddonsByName = lockfile.Addons.ToDictionary(a => a.Name, StringComparer.Ordinal);
        var walksAddonGraph = resync || name is null || manifest.Addons.ContainsKey(name);

        IReadOnlyList<LockedAddon> addons = lockfile.Addons;
        IReadOnlyList<DotnetSpec> addonDeclaredDotnet = [];
        IReadOnlyDictionary<string, IReadOnlyList<AddonDotnetRequirement>>? addonDotnetRequirements = null;
        IReadOnlyList<GraphWarning> graphWarnings = [];
        IReadOnlyDictionary<string, string> refusalHints = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var addonResults = new List<AddonUpdateResult>();

        if (walksAddonGraph)
        {
            // A full update (name is null) re-resolves every top-level addon fresh -- SyncAddonGraphAsync
            // then treats the walk as exhaustive and prunes anything no longer reachable. A single
            // named addon update only re-resolves that one addon; every other top-level addon (and,
            // transitively, anything only it needs) is frozen exactly as it's currently locked.
            // A resync freshens nothing: every root is frozen to its locked commit, so the only thing
            // that can move the graph is a changed [overrides.*] entry.
            var freshNames = resync
                ? new HashSet<string>(StringComparer.Ordinal)
                : name is null
                    ? manifest.Addons.Keys.ToHashSet(StringComparer.Ordinal)
                    : new HashSet<string>(StringComparer.Ordinal) { name };

            var graphSync = await SyncAddonGraphAsync(projectDirectory, manifest, lockfile, freshNames, force, cancellationToken);
            (addons, addonDeclaredDotnet, graphWarnings) = (graphSync.Addons, graphSync.AddonDeclaredDotnet, graphSync.Warnings);
            addonDotnetRequirements = graphSync.AddonDotnetRequirements;

            // Before dotnet resolution, so an adopted feed is available to the very packages that
            // caused it to be adopted.
            (manifest, refusalHints) = ApplyAdoption(manifest, graphSync.Adoption);

            // Persisted immediately, not after resolution succeeds. Adoption is a decision the user
            // has already made — by trusting the host, or by answering a prompt — and losing it
            // because a later step failed would mean re-asking on every retry of a failing update.
            if (graphSync.Adoption.Adopted.Count > 0)
            {
                WriteManifest(manifestPath, manifest);
            }

            // Likewise for the addons themselves: SyncAddonGraphAsync has already swapped every one of
            // them onto disk by this point, so the lockfile has to catch up now, not after dotnet
            // resolution succeeds. Otherwise a failing dotnet step (below) leaves real,
            // freshly-installed folders the lockfile doesn't know about, and the next attempt's
            // DirtyChecker refuses to touch an "untracked" folder even though it's exactly what
            // dotquay itself just installed (SPEC.md P0-6).
            WriteLockfile(lockfilePath, lockfile with { Addons = addons });

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
        if (!resync)
        {
            if (name is null)
            {
                dotnetTargetNames.UnionWith(manifest.Dotnet.Keys);
            }
            else if (manifest.Dotnet.ContainsKey(name))
            {
                dotnetTargetNames.Add(name);
            }
        }

        foreach (var spec in addonDeclaredDotnet)
        {
            // On a resync, only *genuinely new* addon-declared packages are resolved — one already
            // locked stays exactly where it is, the same "don't move what wasn't asked to move" rule
            // AddAsync applies (requirement P1-5).
            if (!manifest.Dotnet.ContainsKey(spec.Name)
                && (!resync || lockfile.Dotnet.All(d => d.Name != spec.Name)))
            {
                dotnetTargetNames.Add(spec.Name);
            }
        }

        var previousDotnetByName = lockfile.Dotnet.ToDictionary(d => d.Name, StringComparer.Ordinal);
        var syncedDotnet = await SyncDotnetAsync(
            projectDirectory, manifest, lockfile.Dotnet, dotnetTargetNames, pruneUnreachable: !resync && name is null, cancellationToken,
            refusalHints, addonDeclaredDotnet, addonDotnetRequirements);


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
        return new UpdateResult(addonResults, dotnetResults, graphWarnings.Select(w => w.Message).ToList());
    }

    /// <summary>Removes an addon or a dotnet dependency (whichever <paramref name="name"/> matches)
    /// from the manifest, lockfile, and disk/.csproj. Unlike <c>install</c>, this does not run the
    /// dirty check for addons — removing is an explicit, unambiguous request, not an overwrite
    /// dotquay needs to protect the user from (SPEC.md §6).</summary>
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
    /// <returns>Which kind of dependency <paramref name="name"/> turned out to be, so a caller can report
    /// the removal as what it was rather than guessing from the name, and which addons still need a
    /// NuGet package whose project declaration was removed.</returns>
    public Task<DependencyRemoval> RemoveAsync(string projectDirectory, string name, CancellationToken cancellationToken = default)
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
                _fileSystem.DeleteDirectory(InstallPaths.For(_fileSystem, projectDirectory, orphan));
            }

            WriteLockfile(lockfilePath, lockfile with { Addons = lockfile.Addons.Where(a => reachable.Contains(a.Name)).ToList() });
            return Task.FromResult(DependencyRemoval.Complete(DependencyKind.Addon));
        }

        if (manifest.Dotnet.ContainsKey(name))
        {
            WriteManifest(manifestPath, manifest with
            {
                Dotnet = manifest.Dotnet.Where(kv => kv.Key != name).ToDictionary(kv => kv.Key, kv => kv.Value),
            });

            var (lockfile, lockfilePath) = ReadOrCreateLockfile(projectDirectory);

            // A reachable addon still declaring it means the project was only overriding the addon's
            // choice (a pin, P1-5). Undoing the pin must not pull the package out from under the addon
            // until the next update puts it back (DESIGN-v0.19 §7a.2), so only the declaration goes.
            var lockedByName = lockfile.Addons.ToDictionary(a => a.Name, StringComparer.Ordinal);
            var reachable = ComputeReachableAddonNames(lockedByName, manifest.Addons.Keys);
            if (AddonDeclaredDotnetNames(projectDirectory, lockfile, reachable).TryGetValue(name, out var declarers))
            {
                return Task.FromResult(new DependencyRemoval(DependencyKind.Dotnet, declarers));
            }

            WriteLockfile(lockfilePath, lockfile with { Dotnet = lockfile.Dotnet.Where(d => d.Name != name).ToList() });

            try
            {
                _dotnetInstaller.Remove(projectDirectory, name);
            }
            catch (DotnetSyncException ex)
            {
                throw new WorkflowException(ex.Message, ex);
            }

            return Task.FromResult(DependencyRemoval.Complete(DependencyKind.Dotnet));
        }

        if (TryRemoveOrphanedDotnet(projectDirectory, name))
        {
            return Task.FromResult(DependencyRemoval.Complete(DependencyKind.Dotnet));
        }

        throw new WorkflowException($"'{name}' is not declared in {manifestPath}.");
    }

    /// <summary>
    /// Removes a NuGet package that only the lockfile still mentions: the orphan doctor reports as a
    /// dangling entry and tells the user to <c>dotquay remove</c> (DESIGN-v0.19 §4). Returns false when
    /// <paramref name="name"/> isn't locked as a package at all, so the caller's "not declared" error
    /// still applies.
    /// </summary>
    /// <remarks>
    /// Before v0.19 this fell through to "not declared in dotquay.toml", which made doctor's own advice
    /// a dead end. It refuses when a reachable addon still declares the package. Removing the reference
    /// would work for exactly one command; the next <c>update</c> would union it straight back in
    /// (requirement P1-5), which would look like dotquay ignoring the removal.
    /// </remarks>
    private bool TryRemoveOrphanedDotnet(string projectDirectory, string name)
    {
        var (lockfile, lockfilePath) = ReadOrCreateLockfile(projectDirectory);
        if (lockfile.Dotnet.All(d => d.Name != name))
        {
            return false;
        }

        var (manifest, _) = ReadManifestOrThrow(projectDirectory);
        var lockedByName = lockfile.Addons.ToDictionary(a => a.Name, StringComparer.Ordinal);
        var reachable = ComputeReachableAddonNames(lockedByName, manifest.Addons.Keys);

        if (AddonDeclaredDotnetNames(projectDirectory, lockfile, reachable).TryGetValue(name, out var declarers))
        {
            var addons = string.Join(", ", declarers.Select(a => $"'{a}'"));
            throw new WorkflowException(
                $"'{name}' is declared by {addons}, not by your {ManifestFileName}, so there is nothing of "
                + $"yours to remove — it would come straight back on the next update. Remove "
                + $"{(declarers.Count == 1 ? "that addon" : "those addons")} instead.");
        }

        WriteLockfile(lockfilePath, lockfile with { Dotnet = lockfile.Dotnet.Where(d => d.Name != name).ToList() });

        try
        {
            _dotnetInstaller.Remove(projectDirectory, name);
        }
        catch (DotnetSyncException ex)
        {
            throw new WorkflowException(ex.Message, ex);
        }

        return true;
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
            ?? throw new WorkflowException($"'{name}' is not a locked addon. Run 'dotquay add' or 'dotquay install' first.");

        var installedPath = InstallPaths.For(_fileSystem, projectDirectory, locked);
        if (!_fileSystem.DirectoryExists(installedPath))
        {
            throw new WorkflowException(
                $"'addons/{locked.EffectiveFolder}' does not exist on disk — nothing to diff. Run 'dotquay install' first.");
        }

        // Deliberately its own staging subfolder, not the one AddonInstaller uses for the same
        // addon name — this is a read-only scratch copy for diffing, a different concern from the
        // install pipeline's stage-then-swap target.
        var baseStagingPath = _fileSystem.CombinePath(projectDirectory, ".dotquay", "staging", $"{name}-patch-base");

        try
        {
            var cachedRepoPath = await _repoCache.GetOrFetchAsync(locked.Git, locked.Resolved, cancellationToken);
            SubtreeExtractor.Extract(_fileSystem, cachedRepoPath, locked.Path, baseStagingPath);

            var patchText = await DiffForPatchOrThrowAsync(baseStagingPath, installedPath, name, cancellationToken);

            var layout = ReadLayout(projectDirectory);
            var patchFilePath = layout.PatchFilePath(_fileSystem, name);

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
                EnsurePatchesStayTracked(layout);
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

    /// <summary>
    /// Finishes a conflicted patch re-apply: takes the files the user resolved in staging, installs
    /// them, and recaptures the patch against the new upstream base (SPEC.md §10).
    /// </summary>
    /// <remarks>
    /// Replaces three manual steps the conflict message used to spell out — copy these files there,
    /// then run this, then remember to recapture. The middle step is the one people skipped, and
    /// skipping it leaves the lockfile describing a patch that no longer matches the tree.
    /// Deliberately refuses while conflict markers remain: installing a file with <c>&lt;&lt;&lt;&lt;&lt;&lt;&lt;</c>
    /// in it produces a Godot script that won't parse, reported far from its cause.
    /// </remarks>
    public async Task<PatchCaptureResult> ResolvePatchAsync(
        string projectDirectory, string name, CancellationToken cancellationToken = default)
    {
        var (lockfile, lockfilePath) = ReadOrCreateLockfile(projectDirectory);
        var locked = lockfile.Addons.FirstOrDefault(a => a.Name == name)
            ?? throw new WorkflowException($"'{name}' is not a locked addon.");

        var layout = ReadLayout(projectDirectory);
        var stagingPath = layout.StagingPath(_fileSystem, name);
        if (!_fileSystem.DirectoryExists(stagingPath))
        {
            throw new WorkflowException(
                $"There is no conflicted update waiting for '{name}' ({stagingPath} doesn't exist). "
                + $"Run 'dotquay update {name}' first — this command only finishes an update that stopped on a "
                + "patch conflict.");
        }

        var unresolved = FindConflictMarkers(stagingPath).ToList();
        if (unresolved.Count > 0)
        {
            throw new WorkflowException(
                $"'{name}': {unresolved.Count} file(s) in {stagingPath} still contain conflict markers: "
                + $"{string.Join(", ", unresolved.Take(5))}. Edit them first — installing them as-is would give "
                + "Godot a script that doesn't parse.");
        }

        var installedPath = InstallPaths.For(_fileSystem, projectDirectory, locked);
        _fileSystem.MoveDirectory(stagingPath, installedPath);

        // Finish the update the conflict aborted. The staged tree came from a commit the lockfile
        // never got to record, so advance it here — otherwise the recapture below would diff against
        // the *old* pristine base and produce a patch that conflicts against upstream forever.
        var markerPath = layout.ConflictMarkerPath(_fileSystem, name);
        if (_fileSystem.FileExists(markerPath))
        {
            var lines = _fileSystem.ReadAllText(markerPath)
                .Split('\n')
                .Select(l => l.Trim('\r', ' '))
                .ToArray();

            if (lines.Length > 0 && lines[0].Length > 0)
            {
                var version = lines.Length > 1 && lines[1].Length > 0 ? lines[1] : locked.Version;
                WriteLockfile(lockfilePath, lockfile with
                {
                    Addons = lockfile.Addons
                        .Select(a => a.Name == name ? a with { Resolved = lines[0], Version = version } : a)
                        .ToList(),
                });
            }

            _fileSystem.DeleteFile(markerPath);
        }

        // Recapture against the now-current commit. The resolved tree is the new intent, and leaving
        // the old patch and base SHA in the lockfile would describe a state that no longer exists.
        return await PatchAsync(projectDirectory, name, cancellationToken);
    }

    /// <summary>
    /// Drops an addon's patch and takes upstream as it is (SPEC.md §10). The other way out of a
    /// conflict, for when the local change has been upstreamed or is no longer wanted.
    /// </summary>
    public async Task<PatchCaptureResult> AbandonPatchAsync(
        string projectDirectory, string name, bool force, CancellationToken cancellationToken = default)
    {
        var (lockfile, lockfilePath) = ReadOrCreateLockfile(projectDirectory);
        var locked = lockfile.Addons.FirstOrDefault(a => a.Name == name)
            ?? throw new WorkflowException($"'{name}' is not a locked addon.");

        var layout = ReadLayout(projectDirectory);
        var patchFilePath = layout.PatchFilePath(_fileSystem, name);

        if (_fileSystem.FileExists(patchFilePath))
        {
            _fileSystem.DeleteFile(patchFilePath);
        }

        // Clear the patch bookkeeping before reinstalling, or the installer would dutifully re-apply
        // the patch this call exists to discard.
        WriteLockfile(lockfilePath, lockfile with
        {
            Addons = lockfile.Addons
                .Select(a => a.Name == name ? a with { PatchHash = null, PatchBaseSha = null } : a)
                .ToList(),
        });

        // force: the installed tree is the patched one, so it will not match the pristine hash — and
        // discarding exactly that is what was asked for.
        await UpdateAsync(projectDirectory, name, force: true, cancellationToken);

        return new PatchCaptureResult(name, Captured: false, patchFilePath);
    }

    /// <summary>Files under <paramref name="root"/> that still carry a git conflict marker.</summary>
    private IEnumerable<string> FindConflictMarkers(string root)
    {
        foreach (var file in _fileSystem.EnumerateFiles(root))
        {
            string contents;
            try
            {
                contents = _fileSystem.ReadAllText(file);
            }
            catch (Exception)
            {
                // Binary files (.png, .ttf) routinely fail to decode as text and can't carry markers
                // anyway — an addon is full of them, and a crash here would block every resolve.
                continue;
            }

            if (contents.Contains("<<<<<<<", StringComparison.Ordinal)
                && contents.Contains(">>>>>>>", StringComparison.Ordinal))
            {
                yield return file;
            }
        }
    }

    /// <summary>The project's layout, honouring <c>[project] patches_dir</c> when the manifest can be
    /// read. A missing manifest isn't fatal here — <c>patch</c> works off the lockfile and disk, and
    /// refusing to capture a patch because <c>dotquay.toml</c> is absent would be a rule with no
    /// purpose.</summary>
    private ProjectLayout ReadLayout(string projectDirectory)
    {
        var manifestPath = _projectFiles.ManifestPath(projectDirectory);
        var manifest = _fileSystem.FileExists(manifestPath)
            ? ManifestReader.ReadProject(_fileSystem.ReadAllText(manifestPath), manifestPath)
            : null;

        return ProjectLayout.For(_fileSystem, projectDirectory, manifest);
    }

    /// <summary>
    /// Writes <c>.dotquay/.gitignore</c> so cache and staging stay out of git while patches stay in.
    /// </summary>
    /// <remarks>
    /// Patches only do their job if teammates get them (SPEC.md §10). Since v0.6 they default to
    /// living inside <c>.dotquay/</c>, next to two directories that must never be committed — so the
    /// folder carries its own rules rather than relying on every project's root <c>.gitignore</c>
    /// getting the distinction right. Written when a patch is captured rather than at <c>init</c>, so
    /// projects that predate this get it the moment it starts mattering.
    /// </remarks>
    private void EnsurePatchesStayTracked(ProjectLayout layout)
    {
        if (!layout.PatchesAreInsideDotquay)
        {
            return;
        }

        var gitignorePath = _fileSystem.CombinePath(layout.ProjectDirectory, ProjectLayout.DotquayDirectory, ".gitignore");
        if (_fileSystem.FileExists(gitignorePath))
        {
            return;
        }

        var patchesFolder = layout.PatchesDirectory.Replace('\\', '/')[(ProjectLayout.DotquayDirectory.Length + 1)..].Trim('/');

        _fileSystem.WriteAllText(gitignorePath, string.Join(
            Environment.NewLine,
            "# dotquay keeps throwaway state here (cache, staging) and it must not be committed.",
            $"# {patchesFolder}/ is the exception: patches are source, and teammates need them.",
            "*",
            "!.gitignore",
            $"!{patchesFolder}/",
            $"!{patchesFolder}/**",
            string.Empty));
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

    private static LockedAddon WithPatch(LockedAddon locked, string contentHash, string? patchHash, string? patchBaseSha) =>
        locked with { ContentHash = contentHash, PatchHash = patchHash, PatchBaseSha = patchBaseSha };

    private static string ComputePatchHash(string patchText) =>
        "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(patchText))).ToLowerInvariant();

    /// <summary>Reads the lockfile's addons as-is — <c>dotquay list</c> shows what's actually
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

        var results = lockfile.Addons
            .Select(locked => new AddonStatusInfo(locked.Name, AddonStatusEvaluator.Evaluate(_fileSystem, projectDirectory, locked)))
            .ToList();

        return new StatusResult(results);
    }

    /// <summary>
    /// The two ways a local .NET link (v0.11) stops working without saying so: its target project is
    /// gone, or the overlay is no longer imported.
    /// </summary>
    /// <remarks>
    /// Both fail in ways that never mention dotquay — the first as an MSBuild error about a project
    /// path, the second not at all. The second is the more dangerous: the build simply keeps using the
    /// published package, which is the exact silence the overlay design was chosen to avoid.
    /// </remarks>
    /// <summary>
    /// Reports an <c>addons/&lt;folder&gt;</c> that is a directory link disagreeing with what this
    /// machine declares (v0.16).
    /// </summary>
    /// <remarks>
    /// The junction and <c>dotquay.local.toml</c> are two independent statements about one folder,
    /// and nothing keeps them in step. The disagreement is silent until an install reaches that
    /// folder, and by then the only choices are writing a package into somebody else's repository or
    /// quietly replacing their live link with a copy. <see cref="AddonLinkGuard"/> refuses both, and
    /// refusing mid-install is a worse place to learn this than a check that costs nothing — so the
    /// same classifier answers here, rather than a second implementation that can drift from it.
    /// </remarks>
    private void CheckAddonLinks(string projectDirectory, DotquayLockfile lockfile, List<DoctorIssue> issues)
    {
        IReadOnlyDictionary<string, LocalLink> declared;
        try
        {
            declared = _localLinks.Read(projectDirectory);
        }
        catch (LocalLinkException)
        {
            // An unreadable local file is its own problem and is reported where it is read for real.
            // It says nothing about any particular addon's folder, so this check has no opinion.
            return;
        }

        foreach (var locked in lockfile.Addons)
        {
            declared.TryGetValue(locked.Name, out var link);

            var verdict = AddonLinkGuard.Inspect(
                _fileSystem, InstallPaths.For(_fileSystem, projectDirectory, locked), link);

            if (!verdict.ForbidsWriting)
            {
                continue;
            }

            issues.Add(new DoctorIssue(
                DoctorIssueKind.AddonLinkMismatch,
                AddonLinkGuard.Explain(locked.Name, locked.EffectiveFolder, locked.Version ?? locked.Resolved, verdict)));
        }
    }

    /// <summary>
    /// Refuses, before anything is written, to link <paramref name="name"/> over an installed folder
    /// holding hand-edits — the same refusal the install would reach, with the same message and the
    /// changed files named.
    /// </summary>
    /// <remarks>
    /// <c>dotquay link</c> declares the link and then installs. When the install refused, the
    /// declaration had already landed in <c>dotquay.local.toml</c>: the folder kept its real content,
    /// yet every later <c>update</c> treated the addon as linked and held it back. A refusal has to
    /// leave the project as it was, so the check runs before the declaration does. The installer keeps
    /// its own check; this one only moves the answer earlier for the case that can be known up front,
    /// an addon the lockfile already records.
    /// </remarks>
    public async Task EnsureAddonLinkableAsync(
        string projectDirectory,
        string name,
        string linkPath,
        bool force,
        CancellationToken cancellationToken = default)
    {
        if (force)
        {
            return;
        }

        var (lockfile, _) = ReadOrCreateLockfile(projectDirectory);
        var locked = lockfile.Addons.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.Ordinal));

        if (locked is null)
        {
            return;
        }

        var installPath = InstallPaths.For(_fileSystem, projectDirectory, locked);

        if (_fileSystem.IsDirectoryLink(installPath)
            || !DirtyChecker.Check(_fileSystem, installPath, locked.ContentHash).IsDirty)
        {
            return;
        }

        var changes = await DescribeLocalChangesAsync(projectDirectory, locked, installPath, cancellationToken);
        throw new WorkflowException(
            AddonInstaller.DescribeLinkOverModified(name, locked.EffectiveFolder, linkPath) + changes);
    }

    /// <summary>
    /// Drops this machine's link for <paramref name="name"/>: removes the declaration and, when
    /// <c>addons/&lt;folder&gt;</c> is a directory link, removes the link itself. Returns false when
    /// nothing was declared.
    /// </summary>
    /// <remarks>
    /// Unlinking is the one act that authorises replacing a link with installed content, so it is
    /// also the act that clears the link out of the way. Leaving that to the reinstall that follows
    /// would put the installer in the position of having to tell consent apart from the drifted state
    /// v0.16 exists to refuse — and it cannot, because on disk the two look identical.
    /// <para>
    /// Removing a link never touches the folder it points at
    /// (<see cref="Abstractions.IFileSystem.DeleteDirectory"/>), so the developer's checkout is safe
    /// here by the same invariant that protects it everywhere else.
    /// </para>
    /// </remarks>
    public bool RemoveAddonLink(string projectDirectory, string name)
    {
        var declarationRemoved = _localLinks.Remove(projectDirectory, name);
        var linkRemoved = false;

        var (lockfile, _) = ReadOrCreateLockfile(projectDirectory);
        var locked = lockfile.Addons.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.Ordinal));

        if (locked is not null)
        {
            var installPath = InstallPaths.For(_fileSystem, projectDirectory, locked);

            if (_fileSystem.IsDirectoryLink(installPath))
            {
                _fileSystem.DeleteDirectory(installPath);
                linkRemoved = true;
            }
        }

        // A junction with no declaration behind it is precisely the state an install refuses, and the
        // refusal offers this command as one of its two ways out. Requiring a declaration to be there
        // would make that advice impossible to follow in the one case it matters most.
        return declarationRemoved || linkRemoved;
    }

    private void CheckDotnetLinks(string projectDirectory, List<DoctorIssue> issues)
    {
        IReadOnlyDictionary<string, DotnetLink> links;
        try
        {
            links = _localLinks.ReadDotnet(projectDirectory);
        }
        catch (LocalLinkException ex)
        {
            issues.Add(new DoctorIssue(DoctorIssueKind.LinkedProjectMissing, ex.Message));
            return;
        }

        foreach (var link in links.Values.OrderBy(l => l.PackageId, StringComparer.Ordinal))
        {
            if (!_fileSystem.FileExists(link.ProjectPath))
            {
                issues.Add(new DoctorIssue(
                    DoctorIssueKind.LinkedProjectMissing,
                    $"'{link.PackageId}' is linked to {link.ProjectPath}, which no longer exists. Run "
                    + $"'dotquay link {link.PackageId} --remove' to go back to the published package."));
            }
        }

        if (links.Count == 0)
        {
            return;
        }

        // The overlay only does anything if the .csproj imports it, and the import lives in a
        // committed file where a merge can lose it.
        string csprojPath;
        try
        {
            csprojPath = CsprojLocator.Locate(_fileSystem, projectDirectory);
        }
        catch (DotnetSyncException)
        {
            return;
        }

        if (!LocalTargetsWriter.HasHook(_fileSystem, csprojPath))
        {
            issues.Add(new DoctorIssue(
                DoctorIssueKind.LocalOverlayNotImported,
                $"{csprojPath} does not import {LocalTargetsWriter.FileName}, so this machine's .NET "
                + "links do nothing and the build is quietly using the published packages. Run "
                + "'dotquay install' to restore the import."));
        }
    }

    /// <summary>
    /// Asset metadata in the <c>.csproj</c> that disagrees with what is declared for the package
    /// (v0.10). Purely local — it compares two files already on disk.
    /// </summary>
    /// <remarks>
    /// Only declared slots are compared. A slot the manifest says nothing about is not dotquay's to
    /// have an opinion on, so a hand-added <c>&lt;ExcludeAssets&gt;</c> beside a declared
    /// <c>&lt;PrivateAssets&gt;</c> is left alone here exactly as the writer leaves it alone.
    /// </remarks>
    private void CheckPackageAssetDrift(
        string projectDirectory, ProjectManifest manifest, DotquayLockfile lockfile, List<DoctorIssue> issues)
    {
        string csprojPath;
        try
        {
            csprojPath = CsprojLocator.Locate(_fileSystem, projectDirectory);
        }
        catch (DotnetSyncException)
        {
            // No .csproj, or more than one. A GDScript-only project is perfectly healthy, and the
            // ambiguous case is already reported by anything that actually tries to write.
            return;
        }

        foreach (var locked in lockfile.Dotnet)
        {
            // The project's own declaration wins, exactly as it does at install time. For an
            // addon-declared package the lockfile is the only record of what was asked for.
            var expected = manifest.Dotnet.TryGetValue(locked.Name, out var spec) ? spec.Assets : locked.Assets;
            if (expected.IsEmpty)
            {
                continue;
            }

            PackageAssets? actual;
            try
            {
                actual = CsprojPackageReferenceWriter.ReadAssets(_fileSystem, csprojPath, locked.Name);
            }
            catch (DotnetSyncException)
            {
                return;
            }

            if (actual is null)
            {
                issues.Add(new DoctorIssue(
                    DoctorIssueKind.PackageAssetMetadataDrift,
                    $"'{locked.Name}' is locked with asset metadata but has no PackageReference in "
                    + $"{csprojPath}. Run 'dotquay install' to write it."));
                continue;
            }

            if (!DeclaredSlotsMatch(expected, actual))
            {
                issues.Add(new DoctorIssue(
                    DoctorIssueKind.PackageAssetMetadataDrift,
                    $"'{locked.Name}': the asset metadata in {csprojPath} differs from what is declared "
                    + "for it. Run 'dotquay update' to rewrite the reference, or bring the manifest into "
                    + "line with the .csproj."));
            }
        }
    }

    /// <summary>
    /// Reports a package the lockfile binds to a private feed that <c>NuGet.Config</c> does not bind
    /// there.
    /// </summary>
    /// <remarks>
    /// <para>The mapping is all-or-nothing per config file: a package matching no pattern falls to
    /// the <c>*</c> catch-all, which points at nuget.org. A missing pattern is therefore not a
    /// weaker binding but a different feed, and the machine that caused the drift is the least
    /// likely to notice - a warm package cache or a local link answers the restore it would have
    /// broken. That is how the 2026-09-15 add bug survived: the file was wrong, every command
    /// reported success, and only a clean restore elsewhere would have failed.</para>
    /// <para>Only missing bindings are reported, never extra ones. A pattern for a package the
    /// project no longer references costs nothing at restore time, and pruning it is the writer's
    /// job on the next install.</para>
    /// </remarks>
    private void CheckNuGetSourceMappingDrift(
        string projectDirectory, ProjectManifest manifest, DotquayLockfile lockfile, List<DoctorIssue> issues)
    {
        // Both gates are the writer's own conditions: with mapping switched off, or with no private
        // feed declared, dotquay writes no mapping at all and there is nothing to have drifted from.
        if (!manifest.NuGetSourceMapping)
        {
            return;
        }

        var sources = manifest.SourceSet();
        if (!sources.HasCustomSources)
        {
            return;
        }

        var mapping = NuGetConfigWriter.ReadSourceMapping(_fileSystem, projectDirectory);

        foreach (var locked in lockfile.Dotnet.Where(dependency => dependency.Source is not null))
        {
            // A manifest pin outranks the lockfile's record here for the same reason it does when the
            // mapping is written: the pin is what the next write will honour.
            var expected = manifest.Dotnet.TryGetValue(locked.Name, out var spec) && spec.Source is not null
                ? spec.Source
                : locked.Source!;

            var source = sources.ByName(expected);

            // A feed the manifest no longer declares is the dangling-entry check's business, and
            // nuget.org needs no pattern of its own - the catch-all already points there.
            if (source is null || source.IsPublicDefault)
            {
                continue;
            }

            if (mapping.TryGetValue(source.ConfigKey, out var patterns)
                && patterns.Any(pattern => PatternCovers(pattern, locked.Name)))
            {
                continue;
            }

            issues.Add(new DoctorIssue(
                DoctorIssueKind.NuGetSourceMappingDrift,
                $"'{locked.Name}' is locked to NuGet feed '{expected}', but NuGet.Config's "
                + $"<packageSourceMapping> does not bind it to '{source.ConfigKey}'. 'dotnet restore' will "
                + "look for it on nuget.org instead, and fail if it was never published there. Run "
                + "'dotquay install' to rewrite the mapping from the lockfile."));
        }
    }

    /// <summary>NuGet's pattern rule, as much of it as this check needs: an exact package ID, or a
    /// prefix ending in <c>*</c>. Case-insensitive, like package IDs themselves.</summary>
    private static bool PatternCovers(string pattern, string packageId) =>
        pattern.EndsWith('*')
            ? packageId.StartsWith(pattern[..^1], StringComparison.OrdinalIgnoreCase)
            : string.Equals(pattern, packageId, StringComparison.OrdinalIgnoreCase);

    private static bool DeclaredSlotsMatch(PackageAssets expected, PackageAssets actual) =>
        (expected.Include is null || PackageAssets.SameTags(expected.Include, actual.Include))
        && (expected.Exclude is null || PackageAssets.SameTags(expected.Exclude, actual.Exclude))
        && (expected.Private is null || PackageAssets.SameTags(expected.Private, actual.Private));

    /// <summary>
    /// Runs the four sanity checks SPEC.md §6 lists for <c>dotquay doctor</c> (requirement P1-4): git
    /// on PATH, lockfile entries no longer declared in the manifest, installed addons whose own
    /// <c>plugin.cfg</c> disagrees with what's locked, and folders under <c>addons/</c> no locked
    /// addon accounts for. Unlike most of this class, a missing manifest doesn't abort the whole
    /// run — it just means the dangling-lockfile-entry check (which needs both files to diff) is
    /// skipped; the other three checks only need the lockfile and disk, so "no dotquay.toml" still
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
            CheckDanglingLockfileEntries(projectDirectory, manifest, lockfile, issues);
            CheckManifestForSecrets(manifestPath, issues);
            CheckTrustedHosts(manifest, manifestPath, issues);
            CheckPackageAssetDrift(projectDirectory, manifest, lockfile, issues);
            CheckNuGetSourceMappingDrift(projectDirectory, manifest, lockfile, issues);
        }

        foreach (var locked in lockfile.Addons)
        {
            var folderIsGodotProject = CheckAddonFolderIsGodotProject(projectDirectory, manifest, lockfile, locked, issues);
            CheckPluginCfgDrift(projectDirectory, locked, issues);
            CheckPackageManifestReadable(projectDirectory, locked, folderIsGodotProject, issues);
            CheckAddonFolderCase(projectDirectory, locked, issues);
            CheckLfsPointers(projectDirectory, locked, issues);
        }

        CheckInstallFolderDrift(projectDirectory, manifest, lockfile, issues);
        CheckOrphanedAddonFolders(projectDirectory, lockfile, issues);
        CheckCredentialFilePermissions(issues);
        CheckSshHostTrust(lockfile, issues);
        CheckFragileUidReferences(projectDirectory, issues);
        CheckDotnetLinks(projectDirectory, issues);
        CheckAddonLinks(projectDirectory, lockfile, issues);

        return new DoctorResult(issues) { Trust = SummarizeTrust(manifest) };
    }

    /// <summary>
    /// Reports an installed addon whose real files are Git LFS placeholders. See <see cref="LfsPointerScanner"/>.
    /// </summary>
    private void CheckLfsPointers(string projectDirectory, LockedAddon locked, List<DoctorIssue> issues)
    {
        var addonPath = InstallPaths.For(_fileSystem, projectDirectory, locked);
        var pointers = LfsPointerScanner.Scan(_fileSystem, addonPath);
        if (pointers.Count == 0)
        {
            return;
        }

        var folder = locked.EffectiveFolder;
        var example = pointers.Count == 1
            ? $"addons/{folder}/{pointers[0]}"
            : $"{pointers.Count} files, such as addons/{folder}/{pointers[0]}";

        issues.Add(new DoctorIssue(
            DoctorIssueKind.LfsPointerFile,
            $"'{locked.Name}' contains Git LFS placeholders where its real files belong ({example}). It was "
            + "fetched on a machine without git-lfs, so git left small text files instead of the binaries, and "
            + "Godot will not be able to load them. Install git-lfs (https://git-lfs.com), run 'git lfs install' "
            + $"once, delete addons/{folder}, and run 'dotquay install'."));
    }

    /// <summary>
    /// Reports an addon installed under a folder its own files disagree with, by case.
    /// </summary>
    /// <remarks>
    /// The symptom points nowhere near the cause: the addon reports as permanently hand-modified and
    /// the editor fills with errors, because Godot keeps repairing case-sensitive
    /// <c>res://addons/…</c> references to match the real folder and re-saving the files it touched.
    /// Finding it by hand meant diffing the installed addon against a fresh clone of its own locked
    /// commit — while the evidence sat in the addon's own files the whole time. The fix is
    /// <c>install_as</c>, which the message names.
    /// </remarks>
    private void CheckAddonFolderCase(string projectDirectory, LockedAddon locked, List<DoctorIssue> issues)
    {
        var addonPath = InstallPaths.For(_fileSystem, projectDirectory, locked);
        var mismatch = AddonFolderCaseChecker.Check(_fileSystem, addonPath, locked.EffectiveFolder);

        if (mismatch is null)
        {
            return;
        }

        issues.Add(new DoctorIssue(
            DoctorIssueKind.AddonFolderCaseMismatch,
            $"'{locked.Name}' is installed at addons/{mismatch.Folder}, but {mismatch.ReferenceCount} of its own "
            + $"files refer to res://addons/{mismatch.Expected}/. Godot's res:// paths are case-sensitive, so it "
            + $"rewrites those references on every open — which is why this addon never reads as clean. Set "
            + $"install_as = \"{mismatch.Expected}\" under [addons.{locked.Name}] and run 'dotquay update {locked.Name}'."));
    }

    /// <summary>
    /// Reports project code that loads an addon resource by a bare <c>uid://</c> — the one reference
    /// shape that breaks when an addon's UIDs change.
    /// </summary>
    /// <remarks>
    /// dotquay works hard not to cause this: <see cref="SyncEngine"/> carries generated <c>.uid</c>
    /// files across an update precisely so uids stay stable. But an addon that starts shipping its own
    /// uids legitimately replaces the local ones, and then only this pattern breaks — scenes and
    /// resources carry a path beside each uid and repair themselves.
    /// <para>
    /// Reported, never rewritten. Fixing it means editing the project's own scripts, and the CLI's
    /// contract is that it writes the manifest, the lockfile, and <c>addons/</c> — nothing else.
    /// </para>
    /// </remarks>
    private void CheckFragileUidReferences(string projectDirectory, List<DoctorIssue> issues)
    {
        var addonsDirectory = _fileSystem.CombinePath(projectDirectory, "addons");

        foreach (var finding in UidReferenceScanner.Scan(_fileSystem, projectDirectory, addonsDirectory))
        {
            issues.Add(new DoctorIssue(
                DoctorIssueKind.FragileUidReference,
                $"{finding.File} loads addons/{finding.Target} by {finding.Uid}. A uid:// literal has no "
                + "path to fall back on, so it breaks if that addon's uid ever changes — load it by its "
                + "res:// path instead.",
                new UidReferenceFix(finding.File, finding.Uid, $"res://addons/{finding.Target}")));
        }
    }

    /// <summary>
    /// Reports an ssh host in the lockfile that this machine has no trusted host key for.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Purely local and offline: it reads known_hosts rather than connecting, so it stays as cheap as
    /// every other doctor check and works on a plane. What it buys is a failure moved earlier -- from
    /// a stranger's first `dotquay install`, which fails deep inside a fetch with git's generic
    /// "correct access rights" wording, to a check they can run before starting.
    /// </para>
    /// <para>
    /// Reported per host rather than per addon: the entry is per host, so ten addons on one GitLab
    /// need one fix, and ten identical issues would bury the nine other things doctor found.
    /// </para>
    /// <para>
    /// A revoked key shares this issue kind rather than getting its own. It is rare enough that a
    /// second wire value -- which the editor plugin would have to learn, and every other consumer of
    /// the JSON with it -- costs more than the message saying so plainly.
    /// </para>
    /// </remarks>
    private void CheckSshHostTrust(DotquayLockfile lockfile, List<DoctorIssue> issues)
    {
        var paths = KnownHostsReader.DefaultPaths(EnvironmentReader, _fileSystem);

        var sshHosts = lockfile.Addons
            .Select(addon => (addon.Name, Url: GitUrl.TryParse(addon.Git, out var url) ? url : null))
            .Where(entry => entry.Url is { Transport: GitTransport.Ssh })
            .GroupBy(entry => (entry.Url!.Host, entry.Url!.Port));

        foreach (var host in sshHosts)
        {
            var status = KnownHostsReader.Lookup(_fileSystem, paths, host.Key.Host, host.Key.Port);
            if (status == KnownHostStatus.Trusted)
            {
                continue;
            }

            var names = host.Select(entry => $"'{entry.Name}'").Order(StringComparer.Ordinal).ToList();
            var subject = names.Count <= 3
                ? string.Join(", ", names)
                : $"{string.Join(", ", names.Take(3))} and {names.Count - 3} more";

            issues.Add(new DoctorIssue(
                DoctorIssueKind.SshHostNotTrusted,
                status == KnownHostStatus.Revoked
                    ? $"known_hosts marks the host key for {host.Key.Host} as revoked, and {subject} "
                      + "fetch from it over ssh. ssh will refuse to connect until that entry is removed or "
                      + "replaced -- confirm with whoever runs the server that the key really did change before "
                      + "trusting a new one."
                    : $"{subject} fetch over ssh from {host.Key.Host}, but no known_hosts entry on this machine "
                      + $"matches it, so the first fetch will fail with 'Host key verification failed'. Get the "
                      + $"fingerprint from whoever runs {host.Key.Host}, check it against 'ssh-keyscan "
                      + $"{host.Key.Host}', then add the key. Checked {string.Join(", ", paths)}."));
        }
    }
    /// <summary>Reports <c>trusted_hosts</c> entries that can't do what they read as (SPEC.md §11.3).
    /// The presence of trust is not a fault and is reported through
    /// <see cref="DoctorResult.Trust"/> instead.</summary>
    private static void CheckTrustedHosts(ProjectManifest manifest, string manifestPath, List<DoctorIssue> issues)
    {
        foreach (var entry in manifest.TrustedHosts)
        {
            var problem = SourceAdopter.DescribeTrustedHostProblem(entry);
            if (problem is not null)
            {
                issues.Add(new DoctorIssue(
                    DoctorIssueKind.TrustedHostUnusable,
                    $"{manifestPath}: [nuget] trusted_hosts entry '{entry}' {problem}"));
            }
        }
    }

    /// <summary>Gathers what the project trusts and which feeds arrived through that trust — so an
    /// over-broad entry, or a feed nobody remembers agreeing to, is visible rather than forgotten.</summary>
    private static DoctorTrust SummarizeTrust(ProjectManifest? manifest)
    {
        if (manifest is null)
        {
            return DoctorTrust.Empty;
        }

        var adopted = manifest.NuGetSources
            .Where(source => source.DeclaredByAddon is not null)
            .Select(source => new DoctorAdoptedSource(source.Name, source.Url, source.DeclaredByAddon!))
            .ToList();

        return new DoctorTrust(manifest.TrustedHosts, adopted);
    }

    /// <summary>Flags a token pasted into the committed manifest — the one mistake dotquay's own
    /// layout invites, since the feed URL lives there and the credential looks like it belongs beside
    /// it (SPEC.md §11.2).</summary>
    private void CheckManifestForSecrets(string manifestPath, List<DoctorIssue> issues)
    {
        var found = Doctor.SecretScanner.Describe(_fileSystem.ReadAllText(manifestPath));
        if (found is null)
        {
            return;
        }

        // The description never quotes the match: a message naming the secret would put it into
        // terminal scrollback and any bug report that follows.
        issues.Add(new DoctorIssue(
            DoctorIssueKind.SecretInManifest,
            $"{manifestPath} contains {found}. This file is committed — move it out with "
            + "'dotquay login <host>', then rotate the exposed credential."));
    }

    /// <summary>
    /// Reports a credential file readable by anyone but its owner.
    /// </summary>
    /// <remarks>
    /// Checked rather than trusted: the store sets <c>0600</c> when it writes, but a file can arrive
    /// by hand, by copy from another machine, or from a backup that flattened permissions. On Windows
    /// this only confirms the file lives under the user profile, whose default ACL is already
    /// owner-only — rewriting a DACL from here would need Windows-only APIs for a check that is
    /// already satisfied by location.
    /// </remarks>
    private void CheckCredentialFilePermissions(List<DoctorIssue> issues)
    {
        var environment = new SystemEnvironmentReader();
        if (environment.HomeDirectory is null)
        {
            return;
        }

        var path = TomlCredentialStore.DefaultPath(environment, _fileSystem);
        if (!_fileSystem.FileExists(path) || OperatingSystem.IsWindows())
        {
            return;
        }

        var mode = File.GetUnixFileMode(path);
        var openToOthers = mode & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite
            | UnixFileMode.OtherRead | UnixFileMode.OtherWrite);

        if (openToOthers != UnixFileMode.None)
        {
            issues.Add(new DoctorIssue(
                DoctorIssueKind.CredentialFilePermissions,
                $"{path} is readable by more than its owner. It holds plaintext tokens — "
                + "run 'chmod 600' on it, and consider the tokens exposed."));
        }
    }

    /// <summary>
    /// Reports lockfile entries nothing accounts for any more.
    /// </summary>
    /// <remarks>
    /// "Not in the project manifest" is <em>not</em> the test, and treating it as one was a real bug:
    /// since the dependency graph landed (v0.4, requirement P1-2/P1-5), a transitive addon and an
    /// addon-declared NuGet package are both legitimately locked while appearing in no manifest the
    /// project owns. Checking membership alone made doctor flag every single transitive dependency —
    /// noise that arrives precisely when the graph feature starts working, and that teaches people to
    /// ignore the check.
    /// <para>
    /// Addons use the lockfile's own <see cref="LockedAddon.Dependencies"/> edges for reachability,
    /// the same rule <see cref="RemoveAsync"/> prunes with. NuGet has no such edges, so the installed
    /// addons' package manifests are read off disk instead — they live inside
    /// <c>addons/&lt;name&gt;/</c> (SPEC.md §7.2), which makes this answerable without any network.
    /// </para>
    /// </remarks>
    private void CheckDanglingLockfileEntries(
        string projectDirectory, ProjectManifest manifest, DotquayLockfile lockfile, List<DoctorIssue> issues)
    {
        var lockedByName = lockfile.Addons.ToDictionary(a => a.Name, StringComparer.Ordinal);
        var reachable = ComputeReachableAddonNames(lockedByName, manifest.Addons.Keys);

        foreach (var locked in lockfile.Addons.Where(a => !reachable.Contains(a.Name)))
        {
            issues.Add(new DoctorIssue(
                DoctorIssueKind.DanglingLockfileEntry,
                $"'{locked.Name}' is locked but nothing requires it any more — run "
                + $"'dotquay remove {locked.Name}' to clean it up, or re-add the [addons.{locked.Name}] "
                + "entry if that was unintentional."));
        }

        var declaredDotnet = new HashSet<string>(manifest.Dotnet.Keys, StringComparer.Ordinal);
        declaredDotnet.UnionWith(AddonDeclaredDotnetNames(projectDirectory, lockfile, reachable).Keys);

        foreach (var locked in lockfile.Dotnet.Where(d => !declaredDotnet.Contains(d.Name)))
        {
            issues.Add(new DoctorIssue(
                DoctorIssueKind.DanglingLockfileEntry,
                $"'{locked.Name}' (NuGet) is locked but nothing requires it any more — run "
                + $"'dotquay remove {locked.Name}' to clean it up, or re-add the [dotnet.{locked.Name}] "
                + "entry if that was unintentional."));
        }
    }

    /// <summary>
    /// Package ID to the reachable addons whose installed package manifests declare it. This is the
    /// offline answer to "does anything still need this NuGet entry?" that doctor's dangling check
    /// and <see cref="RemoveAsync"/> both ask, read through the same
    /// <see cref="InstalledPackageManifests"/> the graph view uses.
    /// </summary>
    /// <remarks>
    /// An addon that ships no manifest, or one that doesn't parse, vouches for nothing. The unreadable
    /// case is <see cref="CheckPackageManifestReadable"/>'s to report, and reporting it twice would be
    /// worse than reporting it once.
    /// </remarks>
    private Dictionary<string, List<string>> AddonDeclaredDotnetNames(
        string projectDirectory, DotquayLockfile lockfile, IReadOnlySet<string> reachable)
    {
        var declarations = _installedManifests.DotnetDeclarationsByAddon(
            projectDirectory, lockfile.Addons.Where(a => reachable.Contains(a.Name)));
        var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (var (addon, specs) in declarations.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            foreach (var spec in specs)
            {
                if (!result.TryGetValue(spec.Name, out var addons))
                {
                    addons = [];
                    result[spec.Name] = addons;
                }

                addons.Add(addon);
            }
        }

        return result;
    }

    /// <summary>The package manifest an installed addon ships, or null if it ships none or ships one
    /// that doesn't parse. See <see cref="InstalledPackageManifests.TryRead"/>.</summary>
    private PackageManifest? TryReadInstalledPackageManifest(string projectDirectory, string folder) =>
        _installedManifests.TryRead(projectDirectory, folder);

    /// <summary>
    /// Reports an addon installed somewhere other than where the manifest declaring it asks for.
    /// </summary>
    /// <remarks>
    /// The drift is invisible by construction: <c>install</c> replays the lockfile and never re-reads
    /// the manifest (SPEC.md §5), so editing <c>install_as</c> moves nothing until an <c>update</c>
    /// re-resolves — and until then the manifest and disk quietly describe two different projects.
    /// </remarks>
    private void CheckInstallFolderDrift(
        string projectDirectory, ProjectManifest? manifest, DotquayLockfile lockfile, List<DoctorIssue> issues)
    {
        var declarations = CollectFolderDeclarations(projectDirectory, manifest, lockfile);

        foreach (var locked in lockfile.Addons)
        {
            if (!declarations.TryGetValue(locked.Name, out var declaration))
            {
                continue;
            }

            var drift = InstallFolderDriftChecker.Check(locked, declaration);
            if (drift is null)
            {
                continue;
            }

            issues.Add(new DoctorIssue(
                DoctorIssueKind.InstallFolderDrift,
                $"'{drift.Name}' is installed at addons/{drift.Actual}, but {drift.Source} asks for "
                + $"addons/{drift.Expected}. 'dotquay install' replays the lockfile and never re-reads the "
                + $"manifest, so nothing will move until you run 'dotquay update {drift.Name}' — or declare "
                + $"install_as = \"{drift.Actual}\" for it in your own {ManifestFileName} if the folder on disk "
                + "is the one you meant."));
        }
    }

    /// <summary>
    /// Gathers, per addon name, the one declaration that decides its install folder.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Precedence mirrors resolution rather than inventing its own: a project-level
    /// <c>[overrides.*]</c> wins (SPEC.md §8), then the project's own <c>[addons.*]</c>, and only then
    /// what a dependent addon's package manifest asks for. Anything the project itself declares is
    /// therefore never second-guessed by an addon.
    /// </para>
    /// <para>
    /// Transitive addons are covered because that is where this goes wrong most quietly — a parent
    /// that starts declaring <c>install_as</c> for a dependency it shares changes nothing about that
    /// dependency's commit, so nothing else notices it needs to move. Two parents asking for different
    /// folders is deliberately silent here: that is a resolution conflict
    /// (<see cref="Resolve.GraphConflictKind.DisagreeingInstallFolders"/>) with its own reporting, and
    /// doctor guessing a winner would contradict it.
    /// </para>
    /// </remarks>
    private Dictionary<string, InstallFolderDriftChecker.FolderDeclaration> CollectFolderDeclarations(
        string projectDirectory, ProjectManifest? manifest, DotquayLockfile lockfile)
    {
        var declarations = new Dictionary<string, InstallFolderDriftChecker.FolderDeclaration>(StringComparer.Ordinal);
        var disputed = new HashSet<string>(StringComparer.Ordinal);

        foreach (var locked in lockfile.Addons)
        {
            var package = TryReadInstalledPackageManifest(projectDirectory, locked.EffectiveFolder);
            if (package is null)
            {
                continue;
            }

            foreach (var (name, spec) in package.Addons)
            {
                var declaration = Declare(spec, $"addons/{locked.EffectiveFolder}/{ManifestFileName}");

                if (declarations.TryGetValue(name, out var existing)
                    && !string.Equals(existing.Folder, declaration.Folder, StringComparison.Ordinal))
                {
                    disputed.Add(name);
                    continue;
                }

                declarations[name] = declaration;
            }
        }

        foreach (var name in disputed)
        {
            declarations.Remove(name);
        }

        if (manifest is null)
        {
            return declarations;
        }

        foreach (var (name, spec) in manifest.Addons)
        {
            declarations[name] = Declare(spec, $"[addons.{name}] in {ManifestFileName}");
        }

        foreach (var (name, spec) in manifest.Overrides)
        {
            declarations[name] = Declare(spec, $"[overrides.{name}] in {ManifestFileName}");
        }

        return declarations;

        static InstallFolderDriftChecker.FolderDeclaration Declare(AddonSpec spec, string source) =>
            new(spec.EffectiveFolder, spec.InstallAs is not null, source);
    }

    private void CheckPluginCfgDrift(string projectDirectory, LockedAddon locked, List<DoctorIssue> issues)
    {
        if (locked.Version is null || locked.Version.StartsWith("0.0.0-", StringComparison.Ordinal))
        {
            // A pseudo-version (untagged repo, rev-pinned) has no real upstream release version to
            // compare plugin.cfg against — comparing would just flag every such addon, always.
            return;
        }

        var installedPath = InstallPaths.For(_fileSystem, projectDirectory, locked);

        // A linked addon's plugin.cfg is the developer's own live working copy (SPEC.md §7.4) — the
        // lockfile deliberately keeps naming the last upstream-resolved version through a link (see
        // LocalLinks), so comparing it against whatever version the developer has written locally
        // would flag ordinary in-progress work as drift, every time. Same reasoning as
        // AddonStatusEvaluator.Evaluate skipping the content hash for a link.
        if (_fileSystem.IsDirectoryLink(installedPath))
        {
            return;
        }

        var pluginCfgPath = _fileSystem.CombinePath(installedPath, "plugin.cfg");
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

    /// <summary>
    /// An installed addon that ships its own <c>dotquay.toml</c> is declaring dependencies (SPEC.md
    /// §7.2). If that file doesn't parse as a package manifest, the graph walk treats the addon as a
    /// leaf and skips every dependency it names — correct behaviour, but invisible. This is the local
    /// half of the same warning the resolver emits, and it needs no network at all: the installed
    /// folder <em>is</em> the extracted subtree, so what's on disk is exactly what the walk reads.
    /// </summary>
    /// <param name="folderIsGodotProject">Whether <see cref="CheckAddonFolderIsGodotProject"/> already
    /// reported this folder as a project root. A project root carries the project's own
    /// <c>[project]</c> manifest, which was never meant to be read as a package manifest; reporting
    /// its missing <c>[package]</c> header on top would send the reader off to "fix" a file that
    /// should not be in <c>addons/</c> at all.</param>
    private void CheckPackageManifestReadable(
        string projectDirectory, LockedAddon locked, bool folderIsGodotProject, List<DoctorIssue> issues)
    {
        if (folderIsGodotProject)
        {
            return;
        }

        var manifestPath = _fileSystem.CombinePath(
            InstallPaths.For(_fileSystem, projectDirectory, locked), ManifestFileName);
        if (!_fileSystem.FileExists(manifestPath))
        {
            // No manifest is the normal case for a third-party addon and costs it nothing
            // (SPEC.md §7.2). Only a manifest that exists but can't be read is worth reporting.
            return;
        }

        try
        {
            ManifestReader.ReadPackage(_fileSystem.ReadAllText(manifestPath), manifestPath);
        }
        catch (ManifestParseException ex)
        {
            issues.Add(new DoctorIssue(
                DoctorIssueKind.UnreadablePackageManifest,
                $"'{locked.Name}' ships a dotquay.toml that isn't a valid package manifest, so any dependencies "
                + $"it declares are being ignored. {ex.Message}"));
        }
    }

    /// <summary>
    /// Reports an installed addon whose folder is a Godot project root rather than an addon — the
    /// offline half of the resolver's <see cref="GraphWarningKind.SubtreeIsGodotProject"/>. Returns
    /// whether it fired, so the package-manifest check can stand down for the same folder.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Nothing on the way here fails: the subtree exists, extracts, hashes, and installs, and
    /// <c>install</c> replays the same lockfile faithfully. The only symptom is the editor reporting
    /// the plugin as missing, which points at Godot rather than at a <c>path</c> in somebody else's
    /// package manifest — and that is the part worth saying. Which manifest declares the addon
    /// depends on who asked for it: the project's own <c>[overrides.*]</c> or <c>[addons.*]</c>, or
    /// the package manifest of a locked addon that lists it under <c>dependencies</c>. The message
    /// names that file, because a consumer of vbeami has no other way to learn that vrm's
    /// <c>path</c> lives in vbeami's manifest.
    /// </para>
    /// <para>
    /// The override hint pins the locked commit with <c>--rev</c>: unlike the resolver, <c>doctor</c>
    /// has no declaring spec to quote a branch or range from for a transitive addon, and a pin that
    /// names exactly what is installed is the one hint that is never wrong.
    /// </para>
    /// </remarks>
    private bool CheckAddonFolderIsGodotProject(
        string projectDirectory, ProjectManifest? manifest, DotquayLockfile lockfile, LockedAddon locked, List<DoctorIssue> issues)
    {
        var finding = GodotProjectRootChecker.Check(
            _fileSystem, InstallPaths.For(_fileSystem, projectDirectory, locked), locked.Name, locked.Path);
        if (finding is null)
        {
            return false;
        }

        var declaredIn = DescribeDeclaringManifest(manifest, lockfile, locked.Name);
        var fix = finding.SuggestedPath is null
            ? $"Set path in {declaredIn} to the folder that holds this addon's plugin.cfg, then run "
              + $"'dotquay update {locked.Name}'."
            : $"Set path = \"{finding.SuggestedPath}\" in {declaredIn} and run 'dotquay update {locked.Name}', "
              + "or pin it from here: "
              + GodotProjectRootChecker.OverrideCommand(locked.Name, "--rev", locked.Resolved, finding.SuggestedPath) + ".";

        issues.Add(new DoctorIssue(
            DoctorIssueKind.AddonFolderIsGodotProject,
            $"'{locked.Name}' is installed at addons/{locked.EffectiveFolder}/, but that folder is a Godot project, not "
            + $"an addon: it has a project.godot and no plugin.cfg — {GodotProjectRootChecker.DescribeWhereTheAddonIs(finding)}. "
            + $"The lockfile extracts path \"{locked.Path}\" from its repository, so Godot finds no plugin under "
            + $"addons/{locked.EffectiveFolder}/. {fix}"));

        return true;
    }

    /// <summary>Names the manifest whose <c>path</c> for <paramref name="name"/> is the one to edit,
    /// in the same precedence resolution uses (SPEC.md §8): an override, then the project's own
    /// declaration, then whichever locked addon lists it as a dependency.</summary>
    private static string DescribeDeclaringManifest(ProjectManifest? manifest, DotquayLockfile lockfile, string name)
    {
        if (manifest?.Overrides.ContainsKey(name) == true)
        {
            return $"this project's dotquay.toml ([overrides.{name}])";
        }

        if (manifest?.Addons.ContainsKey(name) == true)
        {
            return $"this project's dotquay.toml ([addons.{name}])";
        }

        var parent = lockfile.Addons.FirstOrDefault(a => a.Dependencies.Contains(name, StringComparer.Ordinal));

        return parent is null
            ? "the manifest that declares it"
            : $"'{parent.Name}''s package manifest (addons/{parent.EffectiveFolder}/dotquay.toml)";
    }

    private void CheckOrphanedAddonFolders(string projectDirectory, DotquayLockfile lockfile, List<DoctorIssue> issues)
    {
        var addonsPath = InstallPaths.AddonsRoot(_fileSystem, projectDirectory);
        if (!_fileSystem.DirectoryExists(addonsPath))
        {
            return;
        }

        // Compared against install folders, not addon names: with install_as they differ, and an
        // addon deliberately installed under another name is the opposite of an orphan.
        var lockedFolders = lockfile.Addons.Select(a => a.EffectiveFolder).ToHashSet(StringComparer.Ordinal);

        // A folder Godot never scans (addons/.backup, one holding a .gdignore) cannot load as an
        // addon, so there is nothing for it to be an orphan of.
        var visibility = new GodotVisibility(_fileSystem, addonsPath);

        foreach (var dir in _fileSystem.EnumerateDirectories(addonsPath))
        {
            var name = dir.Replace('\\', '/').TrimEnd('/').Split('/').Last();
            if (!lockedFolders.Contains(name) && visibility.IsVisible(name, isDirectory: true))
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
    ///
    /// An addon named in <c>[overrides]</c> is checked against the override's spec, not the base
    /// <c>[addons]</c> spec — same "override wins unconditionally" rule
    /// <see cref="DependencyGraphResolver"/> applies during real resolution (SPEC.md §8). Checking
    /// the un-overridden spec instead would compare the locked commit against a constraint nothing
    /// actually resolves against, which can surface an unrelated, older-looking tag as if it were
    /// "newer" simply because it satisfies a different range.
    /// </summary>
    public async Task<OutdatedResult> OutdatedAsync(string projectDirectory, CancellationToken cancellationToken = default)
    {
        var (manifest, _) = ReadManifestOrThrow(projectDirectory);
        var (lockfile, _) = ReadOrCreateLockfile(projectDirectory);

        var addonResults = new List<AddonOutdatedInfo>();
        var checkedCount = 0;
        foreach (var locked in lockfile.Addons)
        {
            if (!manifest.Overrides.TryGetValue(locked.Name, out var spec) &&
                !manifest.Addons.TryGetValue(locked.Name, out spec))
            {
                continue;
            }

            // One `git ls-remote` per addon, each a network round trip — the exact shape of work that
            // looks like a hang on a project with a dozen dependencies.
            _progress.Report($"checking {locked.Name}", ++checkedCount, lockfile.Addons.Count);

            var info = await CheckAddonOutdatedOrThrowAsync(spec, locked, cancellationToken);
            if (info is not null)
            {
                addonResults.Add(info);
            }
        }

        var dotnetResults = new List<DotnetOutdatedInfo>();
        foreach (var locked in lockfile.Dotnet)
        {
            if (!manifest.Dotnet.TryGetValue(locked.Name, out var spec))
            {
                continue;
            }

            // Checked against the feed it is pinned to, or the one it actually resolved from — not
            // the whole search order. "Is there a newer version?" has to mean "on the feed this
            // package comes from", or a public mirror's version number would report an upgrade that
            // install would never produce.
            var nuGetClient = _nuGetClientFactory.Create(manifest.SourceSet(), spec.Source ?? locked.Source);

            string? latest;
            try
            {
                // The manifest's own range, the same one `update` resolves with (DESIGN-v0.21 §3.5): an
                // upgrade `update` would not make is not reported as outdated.
                latest = await NuGetVersionSelector.CheckForNewerWithinAsync(
                    nuGetClient, locked.Name, locked.Resolved, ParseDotnetConstraintOrThrow(locked.Name, spec.Version), cancellationToken);
            }
            catch (Exception ex) when (ex is NuGetAuthenticationException or NuGetSourceException)
            {
                // Translated here rather than left to escape: the CLI commands catch
                // WorkflowException, so an untranslated feed error reaches the user as an unhandled
                // exception with a stack trace — and, under --json, with no envelope at all.
                throw new WorkflowException($"'{locked.Name}': {ex.Message}", ex);
            }

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

    /// <summary>Resolves a version and reports which feed answered, so the lockfile can record it
    /// (SPEC.md §7.3). A <see cref="NuGetAuthenticationException"/> is translated rather than
    /// swallowed: a feed that refuses to answer must stop the operation, not quietly become "package
    /// not found" and let a later public feed supply a same-named package.</summary>
    /// <remarks>
    /// <paramref name="constraint"/> selects the highest version inside it (DESIGN-v0.21 §3.5); null
    /// means none was declared, which selects the newest stable release.
    /// </remarks>
    private async Task<(string Version, string? SourceName)> ResolveDotnetVersionOrThrowAsync(
        INuGetClient nuGetClient, string packageId, VersionConstraint? constraint, CancellationToken cancellationToken)
    {
        try
        {
            var query = await nuGetClient.GetVersionsAsync(packageId, cancellationToken);

            if (constraint is not null)
            {
                var match = NuGetVersionSelector.SelectHighestMatching(query.Versions, constraint);
                if (match is null)
                {
                    var newest = NuGetVersionSelector.SelectLatestStable(query.Versions);
                    throw new WorkflowException(
                        $"'{packageId}' has no version matching '{constraint}' on NuGet"
                        + (newest is null ? "." : $" (newest stable: {newest}).")
                        + " Change the range, or pin an existing version with 'dotquay add "
                        + $"{packageId} --version <version>'."
                        + DescribeCheckedSources(query));
                }

                return (match, query.SourceName);
            }

            var latest = NuGetVersionSelector.SelectLatestStable(query.Versions);
            if (latest is null)
            {
                throw new WorkflowException(DescribeNoStableRelease(packageId, query));
            }

            return (latest, query.SourceName);
        }
        catch (Exception ex) when (ex is NuGetAuthenticationException or NuGetSourceException)
        {
            throw new WorkflowException($"'{packageId}': {ex.Message}", ex);
        }
    }

    /// <summary>
    /// What <c>add</c> declares for a NuGet package when no range was asked for: <c>^</c> on the
    /// version it resolved, as <c>npm install</c> writes (DESIGN-v0.21 §3.6).
    /// </summary>
    private static string CompatibleRange(string resolvedVersion) => "^" + resolvedVersion;

    /// <summary>
    /// Runs one package's install-and-restore, putting the <c>.csproj</c> and <c>NuGet.Config</c> back
    /// if it fails, so a refused restore leaves the project as it was (DESIGN-v0.21 §4).
    /// </summary>
    private async Task<LockedDotnet> InstallDotnetRestoringOnFailureAsync(
        string projectDirectory, Func<Task<LockedDotnet>> install)
    {
        var snapshot = DotnetFilesSnapshot.Capture(_fileSystem, projectDirectory);
        try
        {
            return await install();
        }
        catch
        {
            snapshot.Restore();
            throw;
        }
    }

    /// <summary>
    /// Resolves one package for <c>update</c>/<c>add</c>'s dotnet sync against every range on it
    /// (DESIGN-v0.21 §3.5).
    /// </summary>
    /// <remarks>
    /// The project's own entry wins outright, as it does for asset metadata (SPEC.md §8). Otherwise the
    /// version must satisfy every addon that declared the package, and a failure names each of them and
    /// its range, as <see cref="GraphConflictKind.NoCommonVersion"/> does for git addons. A package the
    /// walk recorded no ranges for falls back to its declared entry.
    /// </remarks>
    private async Task<(string Version, string? SourceName)> ResolveDotnetForSyncAsync(
        INuGetClient nuGetClient,
        string packageId,
        DotnetSpec? projectSpec,
        IReadOnlyDictionary<string, DotnetSpec>? addonDeclaredByName,
        IReadOnlyDictionary<string, IReadOnlyList<AddonDotnetRequirement>>? addonRequirements,
        CancellationToken cancellationToken)
    {
        if (projectSpec is not null)
        {
            return await ResolveDotnetVersionOrThrowAsync(
                nuGetClient, packageId, ParseDotnetConstraintOrThrow(packageId, projectSpec.Version), cancellationToken);
        }

        IReadOnlyList<AddonDotnetRequirement> ranges =
            addonRequirements is not null && addonRequirements.TryGetValue(packageId, out var recorded) && recorded.Count > 0
                ? recorded
                : addonDeclaredByName is not null && addonDeclaredByName.TryGetValue(packageId, out var declared)
                    ? [new AddonDotnetRequirement("an addon", declared.Version)]
                    : [];

        VersionConstraint? combined = null;
        foreach (var range in ranges)
        {
            if (!VersionConstraint.TryParse(range.Version, out var constraint) || constraint is null)
            {
                throw new WorkflowException(
                    $"'{packageId}': '{range.Version}' (from {range.AddonName}) is not a valid version constraint.");
            }

            combined = combined is null ? constraint : combined.Intersect(constraint);
        }

        try
        {
            return await ResolveDotnetVersionOrThrowAsync(nuGetClient, packageId, combined, cancellationToken);
        }
        catch (WorkflowException ex) when (ranges.Count > 1)
        {
            throw new WorkflowException(
                $"'{packageId}': no version satisfies every addon that declares it — "
                + string.Join("; ", ranges.Select(r => $"{r.AddonName} wants {r.Version}"))
                + $". Declare [dotnet.\"{packageId}\"] in your own dotquay.toml to settle it.",
                ex);
        }
    }

    /// <summary>
    /// The version a package's first link records, before anything was ever resolved for it: the
    /// declared version when it names one (<c>1.2.3</c>, or the base of <c>^1.2.3</c>/<c>~1.2.3</c>).
    /// </summary>
    /// <remarks>
    /// A link is never resolved remotely (SPEC.md §7.4), so there is nothing to pick a version inside a
    /// range from, and the <c>.csproj</c> needs a real one. A range with no single version in it
    /// (<c>1.2.*</c>, <c>&gt;=1 &lt;2</c>) is refused rather than guessed at.
    /// </remarks>
    private static string? LinkedFirstVersion(string packageId, DotnetSpec? spec)
    {
        if (spec is null)
        {
            return null;
        }

        var text = spec.Version.Trim().TrimStart('^', '~', '=');
        if (AddonVersion.TryParse(text, out var version) && version is not null)
        {
            return text;
        }

        throw new WorkflowException(
            $"'{packageId}' is linked but has never been resolved, and '{spec.Version}' names no single version "
            + "to record. Unlink it and run 'dotquay update' once, or declare an exact version.");
    }

    /// <summary>
    /// Parses a <c>[dotnet.*]</c> <c>version</c> (or an <c>add --version</c>), naming the package when
    /// it isn't in the constraint language. Null in, null out: nothing was declared.
    /// </summary>
    private static VersionConstraint? ParseDotnetConstraintOrThrow(string packageId, string? text)
    {
        if (text is null)
        {
            return null;
        }

        if (!VersionConstraint.TryParse(text, out var constraint) || constraint is null)
        {
            throw new WorkflowException(
                $"'{packageId}': '{text}' is not a valid version constraint. Use an exact version (1.2.3), a "
                + "range (^1.2, ~1.2, 1.2.*, >=1.2 <2), or a prerelease (5.1.0-rc1). NuGet's interval "
                + "notation ([1.0,2.0)) is not accepted; see DESIGN-v0.21.");
        }

        return constraint;
    }

    /// <summary>
    /// The "no stable release" message, sharpened with which feeds were actually asked.
    /// </summary>
    /// <remarks>
    /// Without this, "no stable release on NuGet" reads identically whether nothing was ever
    /// declared, or a named private feed answered and came back empty — and the second case is
    /// exactly the confusing one: the feed was reached and even authenticated, and the package still
    /// looks absent. Naming it, plus the GitLab-specific ambiguity <see cref="MultiSourceNuGetClient"/>
    /// already calls out in its own remarks, turns "why is my package missing" into two concrete
    /// things to go check (was it actually published? does the token have read_package_registry?)
    /// instead of a dead end that looks like a typo in the package ID.
    /// </remarks>
    private static string DescribeNoStableRelease(string packageId, NuGetVersionQuery query)
    {
        return $"'{packageId}' has no stable release on NuGet — pass an explicit version (including "
            + "prerelease) if you want to add it anyway."
            + DescribeCheckedSources(query);
    }

    /// <summary>
    /// Which feeds were asked, and the GitLab ambiguity, as a sentence to append to a "not found"
    /// message — or nothing when only nuget.org was ever going to be asked.
    /// </summary>
    /// <remarks>
    /// Shared by "no stable release" and "no version in the range" (DESIGN-v0.21): both can mean the
    /// package, or the versions the range wants, sit on a private feed the token can't see.
    /// </remarks>
    private static string DescribeCheckedSources(NuGetVersionQuery query)
    {
        var privateSourcesChecked = query.CheckedSources.Where(s => s != NuGetSourceSpec.PublicName).ToList();
        if (privateSourcesChecked.Count == 0)
        {
            return "";
        }

        return $" Checked: {string.Join(", ", query.CheckedSources)}. If it's meant to come from "
            + $"{string.Join(" or ", privateSourcesChecked)}, note that GitLab answers 404 both for a package "
            + "that was never published and one your token isn't allowed to see — confirm it's actually in the "
            + "project's package registry, and that the token has the read_package_registry scope.";
    }

    /// <summary>
    /// Regenerates <c>dotquay.local.targets</c> from this machine's links (v0.11). Derived state, so
    /// it is rebuilt from the link file rather than edited — and a no-op, with no file left behind,
    /// for the overwhelmingly common case of a project with no dotnet links.
    /// </summary>
    private void SyncLocalTargets(string projectDirectory)
    {
        try
        {
            LocalTargetsWriter.Sync(_fileSystem, projectDirectory, _localLinks.ReadDotnet(projectDirectory));
        }
        catch (LocalLinkException ex)
        {
            throw new WorkflowException(ex.Message, ex);
        }
    }

    private async Task<LockedDotnet> InstallDotnetOrThrowAsync(
        string projectDirectory,
        string packageId,
        string version,
        ProjectManifest manifest,
        IReadOnlyList<LockedDotnet> known,
        bool restore,
        string? resolvedSourceName,
        CancellationToken cancellationToken,
        PackageAssets? assets = null,
        PackageAssets? previouslyWritten = null)
    {
        try
        {
            return await _dotnetInstaller.InstallAsync(
                projectDirectory,
                packageId,
                version,
                manifest.SourceSet(),
                restore,
                resolvedSourceName,
                MappingForInstall(manifest, known, packageId, version, resolvedSourceName),
                manifest.NuGetSourceMapping,
                assets,
                previouslyWritten,
                cancellationToken);
        }
        catch (DotnetSyncException ex)
        {
            throw new WorkflowException($"'{packageId}': {ex.Message}", ex);
        }
        catch (Exception ex) when (ex is NuGetAuthenticationException or NuGetSourceException)
        {
            throw new WorkflowException($"'{packageId}': {ex.Message}", ex);
        }
    }

    /// <summary>
    /// The <c>&lt;packageSourceMapping&gt;</c> this one install needs: every package whose feed is
    /// already known, plus the one being installed.
    /// </summary>
    /// <remarks>
    /// <para>That second half is the fix for a real failure. The mapping used to come from
    /// <see cref="ProjectNuGetSources.PinnedPackages"/> alone, read from the manifest <em>as it is on
    /// disk</em> — and during <c>add</c> the package being added is not in it yet, because the manifest
    /// is written after the install succeeds. So the only pattern in the file was the <c>*</c>
    /// catch-all pointing at nuget.org, and <c>dotnet restore</c> refused a package that had just been
    /// resolved successfully from a private feed:
    /// <c>NU1101 ... the following source(s) were not considered: dotquay-&lt;name&gt;</c>. Found by
    /// running the published binary against a real GitLab feed, which is the only place it shows: the
    /// fake CLI runner does not read NuGet.Config, and every unit test passed throughout.</para>
    /// <para>SPEC.md §11.1 already asks for exactly this — "<c>dotnet restore</c> is bound to the same
    /// feed via <c>&lt;packageSourceMapping&gt;</c>", without which multi-feed support "would be a
    /// report rather than a guarantee". It was true of the final restore and not of the per-package
    /// install.</para>
    /// <para>Manifest pins are applied last so they win, mirroring
    /// <see cref="ProjectNuGetSources.MappedPackages"/>: a pin is a statement of intent, a resolved
    /// source is a record of what answered.</para>
    /// <para>The <em>rest</em> of the picture is the fix for a second failure, seen 2026-09-15: a
    /// <c>dotquay add</c> in a project that already had VBeami.Core bound to a private feed left a
    /// <c>NuGet.Config</c> naming only the package just added. <see cref="NuGetConfigWriter"/>
    /// rewrites the whole <c>&lt;packageSourceMapping&gt;</c> element on every write, so a partial
    /// mapping is not merely incomplete: it un-maps every other private package, which then matches
    /// the <c>*</c> catch-all and is looked for on nuget.org. A machine-local link hid it there; on
    /// any other machine restore would have failed. Hence the whole picture, taken from the lockfile
    /// (the record of what each package actually resolved from) and built through
    /// <see cref="ProjectNuGetSources.MappedPackages"/>, the same function the closing restore uses,
    /// so the file an install leaves behind and the file that restore writes cannot disagree.</para>
    /// </remarks>
    private static IReadOnlyDictionary<string, string> MappingForInstall(
        ProjectManifest manifest,
        IReadOnlyList<LockedDotnet> known,
        string packageId,
        string version,
        string? resolvedSourceName)
    {
        // What is being written now wins over whatever the lockfile still says about the same
        // package, including when it resolves from nuget.org this time: dropping the stale entry is
        // what keeps the old feed's pattern from outliving the change.
        var complete = known
            .Where(locked => !string.Equals(locked.Name, packageId, StringComparison.OrdinalIgnoreCase))
            .Append(new LockedDotnet { Name = packageId, Resolved = version, Source = resolvedSourceName })
            .ToList();

        return manifest.MappedPackages(complete);
    }

    /// <summary>
    /// The best picture available part-way through a graph sync: what this run has installed so far,
    /// plus the previous lockfile entry for every package it has not reached yet.
    /// </summary>
    /// <remarks>
    /// The closing restore rewrites the mapping from the complete set either way, so this is about
    /// the file left behind in between: what survives a run that fails half-way, and what a build
    /// triggered from the editor in that window would restore against.
    /// </remarks>
    private static IReadOnlyList<LockedDotnet> KnownDotnet(
        IReadOnlyDictionary<string, LockedDotnet> previous,
        IReadOnlyDictionary<string, LockedDotnet> installed)
    {
        var known = new Dictionary<string, LockedDotnet>(previous, StringComparer.Ordinal);

        foreach (var (name, locked) in installed)
        {
            known[name] = locked;
        }

        return known.Values.ToList();
    }

    private async Task RestoreDotnetOrThrowAsync(
        string projectDirectory,
        ProjectManifest manifest,
        CancellationToken cancellationToken,
        IReadOnlyList<LockedDotnet>? resolvedDotnet = null)
    {
        try
        {
            // The final restore is where NuGet.Config's mapping is written from the complete set:
            // per-package installs only ever know part of it (SPEC.md §11.1).
            await _dotnetInstaller.RestoreAsync(
                projectDirectory,
                manifest.SourceSet(),
                cancellationToken,
                resolvedDotnet is null ? null : manifest.MappedPackages(resolvedDotnet),
                manifest.NuGetSourceMapping);
        }
        catch (DotnetSyncException ex)
        {
            throw new WorkflowException(ex.Message, ex);
        }
    }

    private async Task<AddonSpec> BuildDefaultSpecAsync(
        string name, string gitUrl, string? pathOverride, string? installAs, CancellationToken cancellationToken)
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
                Name = name,
                Git = gitUrl,
                Version = "~" + string.Join('.', minorComponents),
                Path = pathOverride,
                InstallAs = installAs,
            };
        }

        // No usable release tag — pin the default branch's current tip as a hard `rev` instead
        // (SPEC.md §6: "No tags found -> pin default-branch tip as rev and print a hint").
        var branch = await _gitClient.ResolveDefaultBranchAsync(gitUrl, cancellationToken);
        var sha = await _gitClient.ResolveBranchTipAsync(gitUrl, branch, cancellationToken);
        return new AddonSpec { Name = name, Git = gitUrl, Rev = sha, Path = pathOverride, InstallAs = installAs };
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

    /// <summary>Installs one addon, honouring a local link for it if this machine declares one
    /// (SPEC.md §7.4). The link lookup happens here rather than inside
    /// <see cref="AddonInstaller"/> so the installer stays a pure "put this commit at this path"
    /// step and every caller gets link behaviour without asking for it.</summary>
    private async Task<LockedAddon> InstallAddonOrThrowAsync(
        ResolvedAddon resolved, string projectDirectory, LockedAddon? previous, bool force, CancellationToken cancellationToken)
    {
        try
        {
            _localLinks.Read(projectDirectory).TryGetValue(resolved.Name, out var link);
            return await _addonInstaller.InstallAsync(
                resolved, ReadLayout(projectDirectory), previous, force, link, cancellationToken);
        }
        catch (LocalLinkException ex)
        {
            throw new WorkflowException(ex.Message, ex);
        }
        catch (ModifiedAddonException ex)
        {
            var changes = await DescribeLocalChangesAsync(
                projectDirectory, previous, ex.ModifiedPath, cancellationToken);
            throw new WorkflowException(ex.Message + changes, ex);
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
    private async Task<GraphSyncResult> SyncAddonGraphAsync(
        string projectDirectory,
        ProjectManifest manifest,
        DotquayLockfile lockfile,
        IReadOnlySet<string> freshNames,
        bool force,
        CancellationToken cancellationToken)
    {
        var previousAddonsByName = lockfile.Addons.ToDictionary(a => a.Name, StringComparer.Ordinal);
        var rootSpecs = BuildRootSpecs(manifest, previousAddonsByName, freshNames);

        // The graph walk is the slowest part of an update by a wide margin — it fetches every addon in
        // the graph to read its manifest — and until v0.6 it produced no output at all, so a large
        // project looked hung for the whole of it.
        _progress.Report("resolving the dependency graph");

        var discoveryRoot = _fileSystem.CombinePath(projectDirectory, ProjectLayout.DotquayDirectory, "staging", "graph-discovery");
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
        var warnings = graph.Warnings.ToList();

        // Read once, for the report below rather than for the install — InstallAddonOrThrowAsync
        // still does its own lookup, so link behaviour stays a property of installing rather than of
        // having been told about a link here.
        var declaredLinks = ReadAddonLinksOrThrow(projectDirectory);

        var syncedCount = 0;
        foreach (var entry in graph.Addons)
        {
            var addonName = entry.Resolved.Name;
            _progress.Report($"installing {addonName}", ++syncedCount, graph.Addons.Count);
            previousAddonsByName.TryGetValue(addonName, out var previous);

            // "Unchanged" has to mean the installed result would be identical, not merely that the
            // commit is. Comparing only git+sha meant a transitive addon whose *parent* started
            // declaring `install_as` — or changed `path` — was skipped, because the commit it resolves
            // to never moved. The addon then stayed in its old folder while the manifest that asked
            // for the move reported success, which for a folder-sensitive addon (vrm preloads
            // res://addons/Godot-MToon-Shader/…) is a silently broken project.
            //
            // Top-level addons never hit this: a full `update` puts every root in freshNames. Only
            // transitive addons, whose specs come from someone else's package manifest, were exposed.
            var unchanged = previous is not null
                && !freshNames.Contains(addonName)
                && previous.Git == entry.Resolved.Git
                && previous.Resolved == entry.Resolved.ResolvedSha
                && string.Equals(previous.EffectiveFolder, entry.Resolved.EffectiveFolder, StringComparison.Ordinal)
                && string.Equals(previous.Path, entry.Resolved.Path, StringComparison.Ordinal);

            // WithDependencies wraps *both* branches on purpose. The walk just read this addon's own
            // package manifest, so its edges are the authoritative ones; reusing `previous` wholesale
            // on the unchanged path meant a lockfile whose edges had been damaged (by an older
            // dotquay, or a hand-edit) kept its damage forever, because the addon whose edges were
            // wrong is exactly the addon that never moves. Rewriting them makes every walk
            // self-healing, and costs nothing when they already agree.
            resultByName[addonName] = WithDependencies(
                unchanged
                    ? previous!
                    : await InstallAddonOrThrowAsync(entry.Resolved, projectDirectory, previous, force, cancellationToken),
                entry.DependencyNames);

            ReportIfHeldBackByALink(declaredLinks, addonName, previous, entry.Resolved.ResolvedSha, warnings);
        }

        foreach (var orphan in previousAddonsByName.Values.Where(a => !resultByName.ContainsKey(a.Name)))
        {
            // InstallPaths, not addons/<name>: with install_as those differ, and deleting by name
            // would miss the real folder and leave the orphan on disk — where Godot keeps loading it.
            _fileSystem.DeleteDirectory(InstallPaths.For(_fileSystem, projectDirectory, orphan));
        }

        // Evaluated after the graph is known, because until the walk finishes there is no list of
        // addons and therefore nothing to have declared a feed.
        var adoption = new SourceAdopter(SourceApproval).Evaluate(
            manifest, graph.AddonDeclaredSources.Select(s => (s.AddonName, s.Source)).ToList(), graph.DotnetPackagesByAddon);

        return new GraphSyncResult(resultByName.Values.ToList(), graph.AddonDeclaredDotnet, warnings, adoption)
        {
            AddonDotnetRequirements = graph.AddonDotnetRequirements,
        };
    }

    /// <summary>
    /// Says so when a linked addon resolved to a newer commit than the lockfile names and was left
    /// where it was (<see cref="AddonInstaller"/>'s link path holds the commit, its version and its
    /// content hash together, because it installs none of them).
    /// </summary>
    /// <remarks>
    /// Without this the outcome is correct and completely silent: the entry does not move, so
    /// <c>update</c> prints the same "unchanged" it prints for an addon that genuinely has nothing
    /// newer upstream. The two states look identical and mean opposite things, and the addon in
    /// question is the one somebody is actively working on — so the reading people would take from
    /// the silence ("upstream has not moved") is exactly the wrong one. It is also the same silence
    /// this workflow already refuses to accept for a .NET link whose overlay has stopped being
    /// imported.
    /// </remarks>
    private static void ReportIfHeldBackByALink(
        IReadOnlyDictionary<string, LocalLink> declaredLinks,
        string addonName,
        LockedAddon? previous,
        string resolvedSha,
        List<GraphWarning> warnings)
    {
        // No previous entry means nothing was held back: the link path locked the commit it was
        // given, because there was no earlier one to keep.
        if (previous is null
            || !declaredLinks.TryGetValue(addonName, out var link)
            || string.Equals(previous.Resolved, resolvedSha, StringComparison.Ordinal))
        {
            return;
        }

        warnings.Add(new GraphWarning(
            addonName,
            GraphWarningKind.LinkedAddonHeldBack,
            $"'{addonName}' is linked to {link.Path}, so the lockfile stays at "
            + $"{ShortSha(previous.Resolved)} even though it resolves to {ShortSha(resolvedSha)} "
            + "upstream. A link is not installed, so its version and content hash cannot be written "
            + "for the newer commit, and moving the commit alone would leave a fresh clone installing "
            + $"content the lockfile does not describe. Run 'dotquay link {addonName} --remove' and "
            + "update again to move the project onto it for real."));
    }

    /// <summary>Reads this machine's addon links, reporting an unreadable
    /// <c>dotquay.local.toml</c> as a workflow failure.</summary>
    /// <remarks>
    /// The same translation <see cref="InstallAddonOrThrowAsync"/> performs, in one place, so the
    /// two cannot end up disagreeing about whether a broken local file stops the run.
    /// </remarks>
    private IReadOnlyDictionary<string, LocalLink> ReadAddonLinksOrThrow(string projectDirectory)
    {
        try
        {
            return _localLinks.Read(projectDirectory);
        }
        catch (LocalLinkException ex)
        {
            throw new WorkflowException(ex.Message, ex);
        }
    }

    /// <summary>
    /// Turns the manifest's <c>[addons.*]</c> into the specs a graph walk starts from: the names in
    /// <paramref name="freshNames"/> keep their live constraints, and every other already-locked name
    /// is frozen to the exact commit and folder it currently has.
    /// </summary>
    /// <remarks>
    /// Extracted so <c>add --preview</c> walks from the identical starting point as the <c>add</c> it
    /// is predicting. A preview built from slightly different roots would be a preview of a different
    /// operation, which is worse than no preview at all.
    /// </remarks>
    private static Dictionary<string, AddonSpec> BuildRootSpecs(
        ProjectManifest manifest,
        IReadOnlyDictionary<string, LockedAddon> previousAddonsByName,
        IReadOnlySet<string> freshNames)
    {
        var rootSpecs = new Dictionary<string, AddonSpec>(StringComparer.Ordinal);

        foreach (var (addonName, spec) in manifest.Addons)
        {
            if (freshNames.Contains(addonName) || !previousAddonsByName.TryGetValue(addonName, out var previousLocked))
            {
                rootSpecs[addonName] = spec;
                continue;
            }

            rootSpecs[addonName] = new AddonSpec
            {
                Name = addonName,
                Git = previousLocked.Git,
                Rev = previousLocked.Resolved,
                Path = previousLocked.Path,
                // Carried through, or freezing an addon would quietly relocate it back to the
                // default folder — an "untouched" addon moving on disk is the opposite of frozen.
                InstallAs = previousLocked.Folder,
            };
        }

        return rootSpecs;
    }

    /// <summary>What one graph walk produced: the installed addons, the NuGet packages those addons
    /// declared, non-fatal observations, and the outcome of evaluating any feeds the addons
    /// declared.</summary>
    private sealed record GraphSyncResult(
        IReadOnlyList<LockedAddon> Addons,
        IReadOnlyList<DotnetSpec> AddonDeclaredDotnet,
        IReadOnlyList<GraphWarning> Warnings,
        SourceAdopter.Result Adoption)
    {
        /// <summary>Every addon's range per NuGet package (DESIGN-v0.21 §3.5).</summary>
        public IReadOnlyDictionary<string, IReadOnlyList<AddonDotnetRequirement>> AddonDotnetRequirements { get; init; } =
            new Dictionary<string, IReadOnlyList<AddonDotnetRequirement>>(StringComparer.Ordinal);
    }

    /// <summary>
    /// Folds adopted feeds into the manifest that will be written and used for resolution, and turns
    /// refused ones into per-package hints.
    /// </summary>
    /// <remarks>
    /// A refusal is deliberately <em>not</em> fatal on its own. The package may well be on nuget.org
    /// anyway, and failing an update because a feed nobody needed was declined would be obnoxious.
    /// Instead the refusal is remembered per package, so if resolution does go on to fail for one of
    /// that addon's packages, the error explains the actual cause rather than reporting a mysterious
    /// "no stable release".
    /// </remarks>
    private static (ProjectManifest Manifest, IReadOnlyDictionary<string, string> RefusalHints) ApplyAdoption(
        ProjectManifest manifest, SourceAdopter.Result adoption)
    {
        var hints = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var refused in adoption.Refused)
        {
            foreach (var packageId in refused.PackageIds)
            {
                hints[packageId] =
                    $"Addon '{refused.AddonName}' declares the NuGet feed {refused.Source.Url}, which was not "
                    + $"adopted. Add its host to [nuget] trusted_hosts, or declare the feed yourself under "
                    + "[nuget.sources.*].";
            }
        }

        if (adoption.Adopted.Count == 0)
        {
            return (manifest, hints);
        }

        return (manifest with { NuGetSources = [.. manifest.NuGetSources, .. adoption.Adopted] }, hints);
    }

    /// <summary>
    /// The dotnet-side twin of <see cref="SyncAddonGraphAsync"/>'s "fresh vs. frozen, prune-or-keep"
    /// pattern: <paramref name="targetNames"/> are (re-)resolved to the highest version their ranges
    /// allow (DESIGN-v0.21 §3.5) and installed if they changed; every other previously-locked package not in <paramref name="targetNames"/> is
    /// either dropped (<paramref name="pruneUnreachable"/> — used for a full <c>update</c>, where the
    /// full picture of what's still declared/reachable is known) or carried forward untouched
    /// (used by <c>add</c> and a single-name <c>update</c>, which must never silently drop or move an
    /// unrelated dotnet package).
    /// </summary>
    /// <remarks>
    /// Any failure puts the <c>.csproj</c> and <c>NuGet.Config</c> back as they were (DESIGN-v0.21 §4):
    /// the lockfile is only written after this returns, so a restore NuGet refuses would otherwise
    /// leave a <c>.csproj</c> that matches neither the manifest nor the lockfile.
    /// </remarks>
    private async Task<IReadOnlyList<LockedDotnet>> SyncDotnetAsync(
        string projectDirectory,
        ProjectManifest manifest,
        IReadOnlyList<LockedDotnet> previousDotnet,
        IReadOnlySet<string> targetNames,
        bool pruneUnreachable,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? refusalHints = null,
        IReadOnlyList<DotnetSpec>? addonDeclared = null,
        IReadOnlyDictionary<string, IReadOnlyList<AddonDotnetRequirement>>? addonRequirements = null)
    {
        var snapshot = DotnetFilesSnapshot.Capture(_fileSystem, projectDirectory);
        try
        {
            return await SyncDotnetUnguardedAsync(
                projectDirectory, manifest, previousDotnet, targetNames, pruneUnreachable, cancellationToken,
                refusalHints, addonDeclared, addonRequirements);
        }
        catch
        {
            snapshot.Restore();
            throw;
        }
    }

    private async Task<IReadOnlyList<LockedDotnet>> SyncDotnetUnguardedAsync(
        string projectDirectory,
        ProjectManifest manifest,
        IReadOnlyList<LockedDotnet> previousDotnet,
        IReadOnlySet<string> targetNames,
        bool pruneUnreachable,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? refusalHints,
        IReadOnlyList<DotnetSpec>? addonDeclared,
        IReadOnlyDictionary<string, IReadOnlyList<AddonDotnetRequirement>>? addonRequirements)
    {
        var sources = manifest.SourceSet();
        var addonDeclaredByName = addonDeclared?.ToDictionary(s => s.Name, StringComparer.Ordinal);
        var previousByName = previousDotnet.ToDictionary(d => d.Name, StringComparer.Ordinal);
        var dotnetLinks = _localLinks.ReadDotnet(projectDirectory);
        var resultByName = new Dictionary<string, LockedDotnet>(StringComparer.Ordinal);
        var anyChanged = false;

        foreach (var targetName in targetNames)
        {
            previousByName.TryGetValue(targetName, out var previous);

            // Per package, because a pin narrows the search to one feed and the pins differ between
            // packages. Clients are cached inside the factory, so this doesn't re-fetch anything.
            var pin = manifest.Dotnet.TryGetValue(targetName, out var spec) ? spec.Source : null;
            var nuGetClient = _nuGetClientFactory.Create(sources, pin);

            // A linked package is never resolved remotely (SPEC.md §7.4, v0.11). The point of linking
            // is to work on a library before it is published, so demanding that a published version
            // exist would refuse exactly the case the feature is for. The lockfile keeps saying
            // whatever it already said — or, on a first link, the version the manifest declares — so
            // unlinking still puts this machine back on something real.
            var linkedVersion = dotnetLinks.ContainsKey(targetName)
                ? previous?.Resolved ?? LinkedFirstVersion(targetName, spec)
                : null;

            string version;
            string? resolvedSource;
            try
            {
                (version, resolvedSource) = linkedVersion is not null
                    ? (linkedVersion, previous?.Source)
                    : await ResolveDotnetForSyncAsync(
                        nuGetClient, targetName, spec, addonDeclaredByName, addonRequirements, cancellationToken);
            }
            catch (WorkflowException ex) when (refusalHints?.TryGetValue(targetName, out var hint) == true)
            {
                // The package failed to resolve and a feed that might have had it was declined.
                // Saying so here is the difference between "no stable release on NuGet" — which
                // sounds like the package doesn't exist — and an answer the user can act on.
                throw new WorkflowException($"{ex.Message} {hint}", ex);
            }

            // A project declaration wins over any addon's, including when it declares no metadata at
            // all (SPEC.md §8) — an addon must not be able to attach asset metadata to a package the
            // project has spoken for. An addon-declared package appears in no manifest the project
            // owns, so its metadata reaches this point only through the graph.
            var assets = spec?.Assets
                ?? (addonDeclaredByName is not null && addonDeclaredByName.TryGetValue(targetName, out var addonSpec)
                    ? addonSpec.Assets
                    : PackageAssets.None);

            // Asset metadata belongs in the change check for the same reason install_as does: a
            // manifest that changes only its metadata moves no version and switches no feed, so a
            // check on those two alone would call it unchanged and never write the .csproj.
            var changed = previous?.Resolved != version
                || previous?.Source != resolvedSource
                || !(previous?.Assets ?? PackageAssets.None).SameAs(assets);

            resultByName[targetName] = changed
                ? await InstallDotnetOrThrowAsync(
                    projectDirectory, targetName, version, manifest, KnownDotnet(previousByName, resultByName),
                    restore: false, resolvedSource, cancellationToken, assets, previous?.Assets)
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

        // Before the restore for the same reason as in InstallAsync: the overlay decides where a
        // linked package's assemblies come from, and restore is what acts on that.
        SyncLocalTargets(projectDirectory);

        if (anyChanged)
        {
            await RestoreDotnetOrThrowAsync(projectDirectory, manifest, cancellationToken, resultByName.Values.ToList());
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

    private static LockedAddon WithDependencies(LockedAddon locked, IReadOnlyList<string> dependencyNames) =>
        locked with { Dependencies = dependencyNames };

    private (ProjectManifest Manifest, string Path) ReadManifestOrThrow(string projectDirectory) =>
        _projectFiles.ReadManifestOrThrow(projectDirectory);

    private (DotquayLockfile Lockfile, string Path) ReadOrCreateLockfile(string projectDirectory) =>
        _projectFiles.ReadOrCreateLockfile(projectDirectory);

    private (DotquayLockfile Lockfile, string Path) ReadLockfileOrThrow(string projectDirectory) =>
        _projectFiles.ReadLockfileOrThrow(projectDirectory);

    private void WriteManifest(string manifestPath, ProjectManifest updated) =>
        _projectFiles.WriteManifest(manifestPath, updated);

    private void WriteLockfile(string lockfilePath, DotquayLockfile updated) =>
        _projectFiles.WriteLockfile(lockfilePath, updated);

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
