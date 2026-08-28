using Dotquay.Core.Abstractions;
using Dotquay.Core.Dotnet;
using Dotquay.Core.Lockfile;
using Dotquay.Core.Manifest;
using Dotquay.Core.Patch;
using Dotquay.Core.Sync;
using Dotquay.Core.Workflows;
using Dotquay.Tests.Support;
using Xunit;

namespace Dotquay.Tests.Workflows;

public class DependencyWorkflowTests
{
    private const string RepoUrl = "https://example.com/dialogue_manager.git";
    private const string ProjectDir = "project";
    private const string CsprojText = """
        <Project Sdk="Godot.NET.Sdk/4.3.0">
          <PropertyGroup>
            <TargetFramework>net8.0</TargetFramework>
          </PropertyGroup>
        </Project>
        """;

    private static (DependencyWorkflow Workflow, InMemoryFileSystem FileSystem, FakeGitClient GitClient) Build()
    {
        var fileSystem = new InMemoryFileSystem();
        fileSystem.WriteAllText($"{ProjectDir}/dotquay.toml", "[project]\ngodot = \"4.3\"\n");

        var gitClient = new FakeGitClient { FileSystemForFetchedContent = fileSystem };
        gitClient.FetchedFilesByUrl[RepoUrl] = new Dictionary<string, string>
        {
            ["addons/dialogue_manager/plugin.cfg"] = "cfg",
        };

        var workflow = new DependencyWorkflow(fileSystem, gitClient, "cache");
        return (workflow, fileSystem, gitClient);
    }

    private static (
        DependencyWorkflow Workflow, InMemoryFileSystem FileSystem, FakeGitClient GitClient, FakeNuGetClient NuGetClient, FakeDotnetCliRunner Runner)
        BuildWithDotnet()
    {
        var fileSystem = new InMemoryFileSystem();
        fileSystem.WriteAllText($"{ProjectDir}/dotquay.toml", "[project]\ngodot = \"4.3\"\n");
        fileSystem.WriteAllText($"{ProjectDir}/Game.csproj", CsprojText);

        var gitClient = new FakeGitClient { FileSystemForFetchedContent = fileSystem };
        var nuGetClient = new FakeNuGetClient();
        var nuGetClientFactory = new FakeNuGetClientFactory(nuGetClient);
        var runner = new FakeDotnetCliRunner();

        var addonInstaller = new AddonInstaller(
            fileSystem, new RepoCache(fileSystem, gitClient, "cache"), new SyncEngine(fileSystem), new FakePatchTool(fileSystem));
        var dotnetInstaller = new DotnetInstaller(fileSystem, runner, TestCredentials.None());
        var workflow = new DependencyWorkflow(fileSystem, gitClient, nuGetClientFactory, addonInstaller, dotnetInstaller);

        return (workflow, fileSystem, gitClient, nuGetClient, runner);
    }

    /// <summary>Full seam, with a <see cref="FakePatchTool"/> in place of the real git-shelling
    /// <c>ProcessPatchTool</c> — everything <see cref="DependencyWorkflow.PatchAsync"/> touches stays
    /// in-memory and deterministic (SPEC.md §12).</summary>
    private static (DependencyWorkflow Workflow, InMemoryFileSystem FileSystem, FakeGitClient GitClient, FakePatchTool PatchTool) BuildWithPatchTool()
    {
        var fileSystem = new InMemoryFileSystem();
        fileSystem.WriteAllText($"{ProjectDir}/dotquay.toml", "[project]\ngodot = \"4.3\"\n");

        var gitClient = new FakeGitClient { FileSystemForFetchedContent = fileSystem };
        gitClient.FetchedFilesByUrl[RepoUrl] = new Dictionary<string, string>
        {
            ["addons/dialogue_manager/plugin.cfg"] = "cfg",
        };

        var repoCache = new RepoCache(fileSystem, gitClient, "cache");
        var patchTool = new FakePatchTool(fileSystem);
        var addonInstaller = new AddonInstaller(fileSystem, repoCache, new SyncEngine(fileSystem), patchTool);
        var nuGetClientFactory = new FakeNuGetClientFactory(new FakeNuGetClient());
        var dotnetInstaller = new DotnetInstaller(fileSystem, new FakeDotnetCliRunner(), TestCredentials.None());

        var workflow = new DependencyWorkflow(fileSystem, gitClient, nuGetClientFactory, addonInstaller, repoCache, patchTool, dotnetInstaller);
        return (workflow, fileSystem, gitClient, patchTool);
    }

    /// <summary>The staging path <see cref="DependencyWorkflow.PatchAsync"/> extracts the pristine
    /// base into — tests need this to key a <see cref="FakePatchTool.DiffResults"/> entry the same
    /// way production code will look it up.</summary>
    private static string PatchBaseStagingPath(InMemoryFileSystem fileSystem, string addonName) =>
        fileSystem.CombinePath(ProjectDir, ".dotquay", "staging", $"{addonName}-patch-base");

    private static void SetTags(FakeGitClient gitClient, params string[] tags) =>
        gitClient.RemoteRefsByUrl[RepoUrl] = tags
            .Select((tag, i) => new GitRef(tag, new string('a', 39) + i, GitRefKind.Tag))
            .ToList();

    [Fact]
    public async Task AddAsync_NoManifest_Throws()
    {
        var fileSystem = new InMemoryFileSystem();
        var workflow = new DependencyWorkflow(fileSystem, new FakeGitClient(), "cache");

        var ex = await Assert.ThrowsAsync<WorkflowException>(() => workflow.AddAsync(ProjectDir, RepoUrl));
        Assert.Contains("dotquay init", ex.Message);
    }

    [Fact]
    public async Task AddAsync_RepoWithTags_DerivesNameAndTildeConstraintFromLatestTag()
    {
        var (workflow, fileSystem, gitClient) = Build();
        SetTags(gitClient, "v2.44.0", "v2.43.0", "v2.44.0-rc1");

        var locked = await workflow.AddAsync(ProjectDir, RepoUrl);

        Assert.Equal("dialogue_manager", locked.Name);
        Assert.Equal("v2.44.0", locked.Version);
        Assert.True(fileSystem.FileExists($"{ProjectDir}/addons/dialogue_manager/plugin.cfg"));

        var manifest = ManifestReader.ReadProject(fileSystem.ReadAllText($"{ProjectDir}/dotquay.toml"));
        Assert.Equal("~2.44", manifest.Addons["dialogue_manager"].Version);

        var lockfile = LockfileReader.Read(fileSystem.ReadAllText($"{ProjectDir}/dotquay.lock"));
        Assert.Single(lockfile.Addons);
    }

    [Fact]
    public async Task AddAsync_RepoWithNoTags_PinsDefaultBranchTipAsRev()
    {
        var (workflow, fileSystem, gitClient) = Build();
        gitClient.DefaultBranches[RepoUrl] = "main";
        var tipSha = new string('b', 40);
        gitClient.BranchTips[(RepoUrl, "main")] = tipSha;

        var locked = await workflow.AddAsync(ProjectDir, RepoUrl);

        Assert.Equal(tipSha, locked.Resolved);
        Assert.StartsWith("0.0.0-", locked.Version);

        var manifest = ManifestReader.ReadProject(fileSystem.ReadAllText($"{ProjectDir}/dotquay.toml"));
        Assert.Equal(tipSha, manifest.Addons["dialogue_manager"].Rev);
    }

    [Fact]
    public async Task AddAsync_PathOverride_ExtractsConfiguredSubtreeInsteadOfDefault()
    {
        // Mirrors a real repo layout hit during end-to-end verification: the repo is named
        // "godot_dialogue_manager" (so the auto-derived name and default guessed path both become
        // "godot_dialogue_manager"), but the addon folder inside it is actually "dialogue_manager".
        const string mismatchedRepoUrl = "https://example.com/godot_dialogue_manager.git";
        var fileSystem = new InMemoryFileSystem();
        fileSystem.WriteAllText($"{ProjectDir}/dotquay.toml", "[project]\ngodot = \"4.3\"\n");
        var gitClient = new FakeGitClient { FileSystemForFetchedContent = fileSystem };
        gitClient.FetchedFilesByUrl[mismatchedRepoUrl] = new Dictionary<string, string>
        {
            ["addons/dialogue_manager/plugin.cfg"] = "cfg",
        };
        gitClient.RemoteRefsByUrl[mismatchedRepoUrl] =
            [new GitRef("v1.0.0", new string('a', 40), GitRefKind.Tag)];
        var workflow = new DependencyWorkflow(fileSystem, gitClient, "cache");

        var locked = await workflow.AddAsync(ProjectDir, mismatchedRepoUrl, new AddonAddOptions { Path = "addons/dialogue_manager" });

        Assert.Equal("godot_dialogue_manager", locked.Name); // still derived from the URL
        Assert.Equal("addons/dialogue_manager", locked.Path); // but extracted from the overridden subtree
        Assert.True(fileSystem.FileExists($"{ProjectDir}/addons/godot_dialogue_manager/plugin.cfg"));

        var manifest = ManifestReader.ReadProject(fileSystem.ReadAllText($"{ProjectDir}/dotquay.toml"));
        Assert.Equal("addons/dialogue_manager", manifest.Addons["godot_dialogue_manager"].Path);
    }

    [Fact]
    public async Task AddAsync_NameAlreadyDeclared_Throws()
    {
        var (workflow, fileSystem, gitClient) = Build();
        SetTags(gitClient, "v1.0.0");
        await workflow.AddAsync(ProjectDir, RepoUrl);

        var ex = await Assert.ThrowsAsync<WorkflowException>(() => workflow.AddAsync(ProjectDir, RepoUrl));
        Assert.Contains("already declared", ex.Message);
    }

    [Fact]
    public async Task InstallAsync_NoLockfile_Throws()
    {
        var (workflow, _, _) = Build();

        var ex = await Assert.ThrowsAsync<WorkflowException>(() => workflow.InstallAsync(ProjectDir, force: false));
        Assert.Contains("dotquay add", ex.Message);
    }

    [Fact]
    public async Task InstallAsync_ManifestDeclaresAddonMissingFromLockfile_Throws()
    {
        var (workflow, fileSystem, _) = Build();
        fileSystem.WriteAllText($"{ProjectDir}/dotquay.toml", "[addons.ghost]\ngit = \"https://example.com/ghost.git\"\nrev = \"" + new string('c', 40) + "\"\n");
        fileSystem.WriteAllText($"{ProjectDir}/dotquay.lock", "");

        var ex = await Assert.ThrowsAsync<WorkflowException>(() => workflow.InstallAsync(ProjectDir, force: false));
        Assert.Contains("ghost", ex.Message);
    }

    [Fact]
    public async Task InstallAsync_ReplaysLockedAddonsWithoutReResolving()
    {
        var (workflow, fileSystem, gitClient) = Build();
        SetTags(gitClient, "v1.0.0");
        await workflow.AddAsync(ProjectDir, RepoUrl);
        fileSystem.DeleteDirectory($"{ProjectDir}/addons/dialogue_manager"); // simulate a fresh clone without addons/

        var installed = await workflow.InstallAsync(ProjectDir, force: false);

        Assert.Single(installed.Addons);
        Assert.Empty(installed.Dotnet);
        Assert.True(fileSystem.FileExists($"{ProjectDir}/addons/dialogue_manager/plugin.cfg"));
    }

    [Fact]
    public async Task RemoveAsync_RemovesFromManifestLockfileAndDisk()
    {
        var (workflow, fileSystem, gitClient) = Build();
        SetTags(gitClient, "v1.0.0");
        await workflow.AddAsync(ProjectDir, RepoUrl);

        await workflow.RemoveAsync(ProjectDir, "dialogue_manager");

        var manifest = ManifestReader.ReadProject(fileSystem.ReadAllText($"{ProjectDir}/dotquay.toml"));
        Assert.Empty(manifest.Addons);
        var lockfile = LockfileReader.Read(fileSystem.ReadAllText($"{ProjectDir}/dotquay.lock"));
        Assert.Empty(lockfile.Addons);
        Assert.False(fileSystem.DirectoryExists($"{ProjectDir}/addons/dialogue_manager"));
    }

    [Fact]
    public async Task RemoveAsync_UnknownName_Throws()
    {
        var (workflow, _, _) = Build();

        var ex = await Assert.ThrowsAsync<WorkflowException>(() => workflow.RemoveAsync(ProjectDir, "nope"));
        Assert.Contains("nope", ex.Message);
    }

    [Fact]
    public async Task ListInstalled_ReturnsLockedAddons()
    {
        var (workflow, _, gitClient) = Build();
        SetTags(gitClient, "v1.0.0");
        await workflow.AddAsync(ProjectDir, RepoUrl);

        var listed = workflow.ListInstalled(ProjectDir);

        Assert.Single(listed);
        Assert.Equal("dialogue_manager", listed[0].Name);
    }

    [Fact]
    public void ListInstalled_NoLockfileYet_ReturnsEmpty()
    {
        var (workflow, _, _) = Build();

        Assert.Empty(workflow.ListInstalled(ProjectDir));
    }

    [Fact]
    public async Task UpdateAsync_NoNameArg_ReResolvesEveryManifestAddon()
    {
        var (workflow, fileSystem, gitClient) = Build();
        SetTags(gitClient, "v2.43.0");
        await workflow.AddAsync(ProjectDir, RepoUrl); // locks the manifest to the "~2.43" constraint

        // A newer patch within the same "~2.43" range appears upstream — still satisfies the
        // constraint recorded at `add` time, unlike a new minor (v2.44.0) would.
        SetTags(gitClient, "v2.43.0", "v2.43.1");
        var results = await workflow.UpdateAsync(ProjectDir, name: null, force: false);

        Assert.Single(results.Addons);
        Assert.True(results.Addons[0].Changed);
        Assert.Equal("v2.43.1", results.Addons[0].Locked.Version);

        var lockfile = LockfileReader.Read(fileSystem.ReadAllText($"{ProjectDir}/dotquay.lock"));
        Assert.Equal("v2.43.1", lockfile.Addons.Single().Version);
    }

    [Fact]
    public async Task UpdateAsync_NoNewerMatchingTag_ReportsUnchanged()
    {
        var (workflow, _, gitClient) = Build();
        SetTags(gitClient, "v2.44.0");
        await workflow.AddAsync(ProjectDir, RepoUrl);

        var results = await workflow.UpdateAsync(ProjectDir, name: null, force: false);

        Assert.Single(results.Addons);
        Assert.False(results.Addons[0].Changed);
    }

    [Fact]
    public async Task UpdateAsync_ByName_OnlyTouchesThatAddon()
    {
        const string otherUrl = "https://example.com/other.git";
        var (workflow, fileSystem, gitClient) = Build();
        SetTags(gitClient, "v1.0.0");
        await workflow.AddAsync(ProjectDir, RepoUrl);

        gitClient.FetchedFilesByUrl[otherUrl] = new Dictionary<string, string> { ["addons/other/plugin.cfg"] = "cfg" };
        gitClient.RemoteRefsByUrl[otherUrl] = [new GitRef("v1.0.0", new string('d', 40), GitRefKind.Tag)];
        await workflow.AddAsync(ProjectDir, otherUrl);

        var otherBefore = LockfileReader.Read(fileSystem.ReadAllText($"{ProjectDir}/dotquay.lock"))
            .Addons.Single(a => a.Name == "other");

        var results = await workflow.UpdateAsync(ProjectDir, "dialogue_manager", force: false);

        Assert.Single(results.Addons);
        Assert.Equal("dialogue_manager", results.Addons[0].Name);

        var lockfile = LockfileReader.Read(fileSystem.ReadAllText($"{ProjectDir}/dotquay.lock"));
        Assert.Equal(2, lockfile.Addons.Count);
        Assert.Equal(otherBefore.Resolved, lockfile.Addons.Single(a => a.Name == "other").Resolved);
    }

    [Fact]
    public async Task UpdateAsync_HandAddedManifestEntryWithNoPriorLock_LocksAndInstallsIt()
    {
        var (workflow, fileSystem, gitClient) = Build();
        SetTags(gitClient, "v1.0.0");
        fileSystem.WriteAllText(
            $"{ProjectDir}/dotquay.toml",
            $"[addons.dialogue_manager]\ngit = \"{RepoUrl}\"\nversion = \"~1.0\"\n");

        var results = await workflow.UpdateAsync(ProjectDir, name: null, force: false);

        Assert.Single(results.Addons);
        Assert.Null(results.Addons[0].PreviousResolved);
        Assert.True(results.Addons[0].Changed);
        Assert.True(fileSystem.FileExists($"{ProjectDir}/addons/dialogue_manager/plugin.cfg"));
    }

    [Fact]
    public async Task UpdateAsync_UnknownName_Throws()
    {
        var (workflow, _, _) = Build();

        var ex = await Assert.ThrowsAsync<WorkflowException>(() => workflow.UpdateAsync(ProjectDir, "nope", force: false));
        Assert.Contains("nope", ex.Message);
    }

    [Fact]
    public async Task UpdateAsync_DirtyDestinationWithoutForce_Throws()
    {
        var (workflow, fileSystem, gitClient) = Build();
        SetTags(gitClient, "v1.0.0");
        await workflow.AddAsync(ProjectDir, RepoUrl);
        fileSystem.WriteAllText($"{ProjectDir}/addons/dialogue_manager/plugin.cfg", "hand-edited");

        await Assert.ThrowsAsync<WorkflowException>(() => workflow.UpdateAsync(ProjectDir, name: null, force: false));
    }

    [Fact]
    public async Task AddDotnetAsync_NoVersionGiven_PicksLatestStableAndInstalls()
    {
        var (workflow, fileSystem, _, nuGetClient, runner) = BuildWithDotnet();
        nuGetClient.VersionsByPackageId["Newtonsoft.Json"] = ["13.0.1", "13.0.3", "13.0.4-beta1"];

        var locked = await workflow.AddDotnetAsync(ProjectDir, "Newtonsoft.Json");

        Assert.Equal("13.0.3", locked.Resolved);
        Assert.Contains("Newtonsoft.Json", fileSystem.ReadAllText($"{ProjectDir}/Game.csproj"));
        Assert.Equal([$"{ProjectDir}/Game.csproj"], runner.RestoredCsprojPaths);

        var manifest = ManifestReader.ReadProject(fileSystem.ReadAllText($"{ProjectDir}/dotquay.toml"));
        Assert.Equal("13.0.3", manifest.Dotnet["Newtonsoft.Json"].Version);

        var lockfile = LockfileReader.Read(fileSystem.ReadAllText($"{ProjectDir}/dotquay.lock"));
        Assert.Equal("13.0.3", lockfile.Dotnet.Single().Resolved);
    }

    [Fact]
    public async Task AddDotnetAsync_ExplicitVersion_UsesItWhenItExists()
    {
        var (workflow, _, _, nuGetClient, _) = BuildWithDotnet();
        nuGetClient.VersionsByPackageId["Newtonsoft.Json"] = ["13.0.1", "13.0.3"];

        var locked = await workflow.AddDotnetAsync(ProjectDir, "Newtonsoft.Json", versionOverride: "13.0.1");

        Assert.Equal("13.0.1", locked.Resolved);
    }

    [Fact]
    public async Task AddDotnetAsync_ExplicitVersionNotFound_Throws()
    {
        var (workflow, _, _, nuGetClient, _) = BuildWithDotnet();
        nuGetClient.VersionsByPackageId["Newtonsoft.Json"] = ["13.0.1"];

        var ex = await Assert.ThrowsAsync<WorkflowException>(
            () => workflow.AddDotnetAsync(ProjectDir, "Newtonsoft.Json", versionOverride: "99.0.0"));
        Assert.Contains("99.0.0", ex.Message);
    }

    [Fact]
    public async Task AddDotnetAsync_NoStableReleaseAndNoOverride_Throws()
    {
        var (workflow, _, _, nuGetClient, _) = BuildWithDotnet();
        nuGetClient.VersionsByPackageId["Only.Prerelease"] = ["1.0.0-beta1"];

        await Assert.ThrowsAsync<WorkflowException>(() => workflow.AddDotnetAsync(ProjectDir, "Only.Prerelease"));
    }

    [Fact]
    public async Task AddDotnetAsync_AlreadyDeclared_Throws()
    {
        var (workflow, _, _, nuGetClient, _) = BuildWithDotnet();
        nuGetClient.VersionsByPackageId["Newtonsoft.Json"] = ["13.0.3"];
        await workflow.AddDotnetAsync(ProjectDir, "Newtonsoft.Json");

        var ex = await Assert.ThrowsAsync<WorkflowException>(() => workflow.AddDotnetAsync(ProjectDir, "Newtonsoft.Json"));
        Assert.Contains("already declared", ex.Message);
    }

    [Fact]
    public async Task InstallAsync_ReplaysDotnetLockEntriesAndRestoresOnce()
    {
        var (workflow, fileSystem, _, nuGetClient, runner) = BuildWithDotnet();
        nuGetClient.VersionsByPackageId["Newtonsoft.Json"] = ["13.0.3"];
        await workflow.AddDotnetAsync(ProjectDir, "Newtonsoft.Json");
        runner.RestoreCalls.Clear(); // clear the restore from `add` so we can see install's own call

        // Simulate a fresh clone: the .csproj still has the reference (it's committed), but pretend
        // nothing has been restored yet by just re-running install and checking it restores again.
        var result = await workflow.InstallAsync(ProjectDir, force: false);

        Assert.Empty(result.Addons);
        Assert.Single(result.Dotnet);
        Assert.Equal("13.0.3", result.Dotnet[0].Resolved);
        Assert.Equal([$"{ProjectDir}/Game.csproj"], runner.RestoredCsprojPaths); // exactly one restore, not per-package
    }

    [Fact]
    public async Task InstallAsync_ManifestDeclaresDotnetMissingFromLockfile_Throws()
    {
        var (workflow, fileSystem, _, _, _) = BuildWithDotnet();
        fileSystem.WriteAllText($"{ProjectDir}/dotquay.toml", "[dotnet.Newtonsoft.Json]\nversion = \"13.0.3\"\n");
        fileSystem.WriteAllText($"{ProjectDir}/dotquay.lock", "");

        var ex = await Assert.ThrowsAsync<WorkflowException>(() => workflow.InstallAsync(ProjectDir, force: false));
        Assert.Contains("Newtonsoft.Json", ex.Message);
    }

    [Fact]
    public async Task UpdateAsync_DotnetNewerStableAvailable_BumpsAndRestores()
    {
        var (workflow, fileSystem, _, nuGetClient, runner) = BuildWithDotnet();
        nuGetClient.VersionsByPackageId["Newtonsoft.Json"] = ["13.0.3"];
        await workflow.AddDotnetAsync(ProjectDir, "Newtonsoft.Json");
        runner.RestoreCalls.Clear();

        nuGetClient.VersionsByPackageId["Newtonsoft.Json"] = ["13.0.3", "13.0.4"];
        var result = await workflow.UpdateAsync(ProjectDir, name: null, force: false);

        Assert.Single(result.Dotnet);
        Assert.True(result.Dotnet[0].Changed);
        Assert.Equal("13.0.4", result.Dotnet[0].Locked.Resolved);
        Assert.Equal([$"{ProjectDir}/Game.csproj"], runner.RestoredCsprojPaths);

        var lockfile = LockfileReader.Read(fileSystem.ReadAllText($"{ProjectDir}/dotquay.lock"));
        Assert.Equal("13.0.4", lockfile.Dotnet.Single().Resolved);
    }

    [Fact]
    public async Task UpdateAsync_DotnetNoNewerVersion_ReportsUnchangedAndSkipsRestore()
    {
        var (workflow, _, _, nuGetClient, runner) = BuildWithDotnet();
        nuGetClient.VersionsByPackageId["Newtonsoft.Json"] = ["13.0.3"];
        await workflow.AddDotnetAsync(ProjectDir, "Newtonsoft.Json");
        runner.RestoreCalls.Clear();

        var result = await workflow.UpdateAsync(ProjectDir, name: null, force: false);

        Assert.Single(result.Dotnet);
        Assert.False(result.Dotnet[0].Changed);
        Assert.Empty(runner.RestoreCalls);
    }

    [Fact]
    public async Task RemoveAsync_DotnetName_RemovesFromManifestLockfileAndCsproj()
    {
        var (workflow, fileSystem, _, nuGetClient, _) = BuildWithDotnet();
        nuGetClient.VersionsByPackageId["Newtonsoft.Json"] = ["13.0.3"];
        await workflow.AddDotnetAsync(ProjectDir, "Newtonsoft.Json");

        await workflow.RemoveAsync(ProjectDir, "Newtonsoft.Json");

        var manifest = ManifestReader.ReadProject(fileSystem.ReadAllText($"{ProjectDir}/dotquay.toml"));
        Assert.Empty(manifest.Dotnet);
        var lockfile = LockfileReader.Read(fileSystem.ReadAllText($"{ProjectDir}/dotquay.lock"));
        Assert.Empty(lockfile.Dotnet);
        Assert.DoesNotContain("Newtonsoft.Json", fileSystem.ReadAllText($"{ProjectDir}/Game.csproj"));
    }

    [Fact]
    public async Task AddDotnetAsync_ManifestDeclaresNuGetSource_RoutesToThatSourcesClient()
    {
        const string privateSource = "https://gitlab.example.com/api/v4/projects/1/packages/nuget/index.json";

        var fileSystem = new InMemoryFileSystem();
        fileSystem.WriteAllText($"{ProjectDir}/dotquay.toml", $"[project]\ngodot = \"4.3\"\nnuget_source = \"{privateSource}\"\n");
        fileSystem.WriteAllText($"{ProjectDir}/Game.csproj", CsprojText);

        var gitClient = new FakeGitClient();
        var defaultClient = new FakeNuGetClient(); // should NOT be used — the manifest names a different source
        var privateClient = new FakeNuGetClient(NuGetSourceSpec.LegacyName);
        privateClient.VersionsByPackageId["Acme.Internal"] = ["2.1.0"];
        var nuGetClientFactory = new FakeNuGetClientFactory(defaultClient);
        // Keyed by declared source name — a legacy [project] nuget_source is read as "default".
        nuGetClientFactory.ClientsBySource[NuGetSourceSpec.LegacyName] = privateClient;

        var addonInstaller = new AddonInstaller(
            fileSystem, new RepoCache(fileSystem, gitClient, "cache"), new SyncEngine(fileSystem), new FakePatchTool(fileSystem));
        var dotnetInstaller = new DotnetInstaller(fileSystem, new FakeDotnetCliRunner(), TestCredentials.None());
        var workflow = new DependencyWorkflow(fileSystem, gitClient, nuGetClientFactory, addonInstaller, dotnetInstaller);

        var locked = await workflow.AddDotnetAsync(ProjectDir, "Acme.Internal");

        // "Acme.Internal" only exists in privateClient's configured versions — succeeding here (and
        // matching the version privateClient reports) is itself proof the manifest's nuget_source
        // routed to that client, not the default one (which would have reported "no stable release").
        Assert.Equal("2.1.0", locked.Resolved);
        Assert.Contains(privateSource, nuGetClientFactory.RequestedSources);

        // The feed that answered is recorded, so a teammate's install can't resolve it elsewhere.
        Assert.Equal(NuGetSourceSpec.LegacyName, locked.Source);
    }

    [Fact]
    public async Task AddDotnetAsync_TwoFeeds_TakesTheFirstThatHasThePackage_NotTheHighestVersion()
    {
        var fileSystem = new InMemoryFileSystem();
        fileSystem.WriteAllText($"{ProjectDir}/dotquay.toml", """
            [nuget.sources.core]
            url = "https://gitlab.example.com/api/v4/projects/1/packages/nuget/index.json"

            [nuget.sources.tools]
            url = "https://gitlab.example.com/api/v4/projects/2/packages/nuget/index.json"
            """);
        fileSystem.WriteAllText($"{ProjectDir}/Game.csproj", CsprojText);

        var gitClient = new FakeGitClient();
        var factory = new FakeNuGetClientFactory(new FakeNuGetClient());
        factory.ClientsBySource["core"] = new FakeNuGetClient("core").With("Acme.Internal", "1.0.0");
        factory.ClientsBySource["tools"] = new FakeNuGetClient("tools").With("Acme.Internal", "99.0.0");

        var addonInstaller = new AddonInstaller(
            fileSystem, new RepoCache(fileSystem, gitClient, "cache"), new SyncEngine(fileSystem), new FakePatchTool(fileSystem));
        var workflow = new DependencyWorkflow(
            fileSystem, gitClient, factory, addonInstaller,
            new DotnetInstaller(fileSystem, new FakeDotnetCliRunner(), TestCredentials.None()));

        var locked = await workflow.AddDotnetAsync(ProjectDir, "Acme.Internal");

        // 99.0.0 is higher and is deliberately not chosen. Taking the newest version wherever it
        // lives is a dependency-confusion substitution attack with extra steps.
        Assert.Equal("1.0.0", locked.Resolved);
        Assert.Equal("core", locked.Source);
    }

    [Fact]
    public async Task AddDotnetAsync_FeedRejectsCredentials_FailsInsteadOfFallingThroughToAPublicFeed()
    {
        var fileSystem = new InMemoryFileSystem();
        fileSystem.WriteAllText($"{ProjectDir}/dotquay.toml", """
            [nuget.sources.core]
            url = "https://gitlab.example.com/api/v4/projects/1/packages/nuget/index.json"
            """);
        fileSystem.WriteAllText($"{ProjectDir}/Game.csproj", CsprojText);

        var gitClient = new FakeGitClient();

        // The private feed 401s; nuget.org would happily serve a same-named package. Falling through
        // is exactly the substitution this must prevent.
        var privateClient = new FakeNuGetClient("core");
        privateClient.UnauthorizedPackageIds.Add("Acme.Internal");

        var factory = new FakeNuGetClientFactory(new FakeNuGetClient().With("Acme.Internal", "99.0.0"));
        factory.ClientsBySource["core"] = privateClient;

        var addonInstaller = new AddonInstaller(
            fileSystem, new RepoCache(fileSystem, gitClient, "cache"), new SyncEngine(fileSystem), new FakePatchTool(fileSystem));
        var workflow = new DependencyWorkflow(
            fileSystem, gitClient, factory, addonInstaller,
            new DotnetInstaller(fileSystem, new FakeDotnetCliRunner(), TestCredentials.None()));

        var exception = await Assert.ThrowsAsync<WorkflowException>(
            () => workflow.AddDotnetAsync(ProjectDir, "Acme.Internal"));

        Assert.IsType<NuGetAuthenticationException>(exception.InnerException);
    }

    [Fact]
    public async Task AddDotnetAsync_PinnedPackage_SearchesOnlyThatFeed()
    {
        var fileSystem = new InMemoryFileSystem();
        fileSystem.WriteAllText($"{ProjectDir}/dotquay.toml", """
            [nuget.sources.core]
            url = "https://gitlab.example.com/api/v4/projects/1/packages/nuget/index.json"

            [nuget.sources.tools]
            url = "https://gitlab.example.com/api/v4/projects/2/packages/nuget/index.json"

            [dotnet.Acme.Internal]
            version = "1.0.0"
            source = "tools"
            """);
        fileSystem.WriteAllText($"{ProjectDir}/Game.csproj", CsprojText);

        var gitClient = new FakeGitClient();
        var factory = new FakeNuGetClientFactory(new FakeNuGetClient());
        factory.ClientsBySource["core"] = new FakeNuGetClient("core").With("Acme.Internal", "1.0.0");
        factory.ClientsBySource["tools"] = new FakeNuGetClient("tools").With("Acme.Internal", "2.0.0");

        var addonInstaller = new AddonInstaller(
            fileSystem, new RepoCache(fileSystem, gitClient, "cache"), new SyncEngine(fileSystem), new FakePatchTool(fileSystem));
        var workflow = new DependencyWorkflow(
            fileSystem, gitClient, factory, addonInstaller,
            new DotnetInstaller(fileSystem, new FakeDotnetCliRunner(), TestCredentials.None()));

        await workflow.UpdateAsync(ProjectDir, "Acme.Internal", force: false);

        Assert.Contains("tools", factory.RequestedPins);
        Assert.DoesNotContain(
            "https://gitlab.example.com/api/v4/projects/1/packages/nuget/index.json", factory.RequestedSources);
    }

    // -- v0.7: an addon's own [dotnet] dependency resolving from the *consuming project's* private
    //    feed. The real shape people hit: an addon (vbeami) declares a NuGet package that only exists
    //    on a private GitLab feed, and a project (botv2) depends on the addon.

    private const string PrivateFeedUrl = "https://gitlab.example.com/api/v4/projects/42/packages/nuget/index.json";
    private const string VbeamiUrl = "https://gitlab.example.com/wild-alliance/vbeami.git";

    /// <summary>Approves every adoption request and records what it was asked.</summary>
    private sealed class ApproveAllSources : ISourceApproval
    {
        public List<SourceAdoptionRequest> Seen { get; } = [];

        public bool Approve(SourceAdoptionRequest request)
        {
            Seen.Add(request);
            return true;
        }
    }

    /// <summary>Project declares the private feed and depends on the addon; the addon's own package
    /// manifest declares the NuGet package, which exists only on that feed.</summary>
    private static (DependencyWorkflow Workflow, FakeNuGetClientFactory Factory, InMemoryFileSystem FileSystem)
        BuildAddonDeclaredPrivatePackage(
            string projectManifest, string addonDotnetBlock, ISourceApproval? approval = null)
    {
        var fileSystem = new InMemoryFileSystem();
        fileSystem.WriteAllText($"{ProjectDir}/dotquay.toml", projectManifest);
        fileSystem.WriteAllText($"{ProjectDir}/Game.csproj", CsprojText);

        var gitClient = new FakeGitClient { FileSystemForFetchedContent = fileSystem };
        gitClient.RemoteRefsByUrl[VbeamiUrl] = [new GitRef("v1.0.0", new string('b', 40), GitRefKind.Tag)];
        gitClient.FetchedFilesByUrl[VbeamiUrl] = new Dictionary<string, string>
        {
            ["addons/vbeami/plugin.cfg"] = "vbeami",
            ["addons/vbeami/dotquay.toml"] = $"""
                [package]
                name = "vbeami"

                {addonDotnetBlock}
                """,
        };

        // VBeami.Core exists ONLY on the private feed. If resolution reached nuget.org instead, the
        // package would simply not be found and the test would fail loudly.
        //
        // Registered under both names the feed can end up with: "gitlab" when the project declares it
        // (v0.7), "vbeami" when it is adopted from the addon that declared it (v0.8), since adoption
        // keeps the addon's own name for the block.
        var factory = new FakeNuGetClientFactory(new FakeNuGetClient());
        factory.ClientsBySource["gitlab"] = new FakeNuGetClient("gitlab").With("VBeami.Core", "1.2.0");
        factory.ClientsBySource["vbeami"] = new FakeNuGetClient("vbeami").With("VBeami.Core", "1.2.0");

        var addonInstaller = new AddonInstaller(
            fileSystem, new RepoCache(fileSystem, gitClient, "cache"), new SyncEngine(fileSystem), new FakePatchTool(fileSystem));
        var workflow = new DependencyWorkflow(
            fileSystem, gitClient, factory, addonInstaller,
            new DotnetInstaller(fileSystem, new FakeDotnetCliRunner(), TestCredentials.None()))
        {
            SourceApproval = approval ?? DenyAllSourceApproval.Instance,
        };

        return (workflow, factory, fileSystem);
    }

    [Fact]
    public async Task Update_AddonDeclaredNuGetPackage_ResolvesFromTheProjectsPrivateFeed()
    {
        var (workflow, factory, fileSystem) = BuildAddonDeclaredPrivatePackage(
            $"""
            [nuget.sources.gitlab]
            url = "{PrivateFeedUrl}"

            [addons.vbeami]
            git = "{VbeamiUrl}"
            version = "~1"
            """,
            """
            [dotnet.VBeami.Core]
            version = "1.0.0"
            """);

        var result = await workflow.UpdateAsync(ProjectDir, name: null, force: false);

        // The package the project never mentioned, pulled from the feed the project declared.
        var locked = Assert.Single(result.Dotnet);
        Assert.Equal("VBeami.Core", locked.Locked.Name);
        Assert.Equal("1.2.0", locked.Locked.Resolved);
        Assert.Equal("gitlab", locked.Locked.Source);

        Assert.Contains(PrivateFeedUrl, factory.RequestedSources);
        Assert.Contains("VBeami.Core", fileSystem.ReadAllText($"{ProjectDir}/Game.csproj"));

        // NuGet.Config must declare the feed too, or `dotnet restore` — a separate process — can't
        // reach it however well dotquay's own query went.
        Assert.Contains(PrivateFeedUrl, fileSystem.ReadAllText($"{ProjectDir}/NuGet.Config"));
    }

    [Fact]
    public async Task Update_AddonDeclaredNuGetPackage_ProjectDidNotDeclareTheFeed_FailsInsteadOfSilentlyMissing()
    {
        // The feed lives in the *project's* manifest; an addon cannot bring one with it. Without it,
        // only nuget.org is searched and the package genuinely isn't there.
        var (workflow, _, _) = BuildAddonDeclaredPrivatePackage(
            $"""
            [addons.vbeami]
            git = "{VbeamiUrl}"
            version = "~1"
            """,
            """
            [dotnet.VBeami.Core]
            version = "1.0.0"
            """);

        var exception = await Assert.ThrowsAsync<WorkflowException>(
            () => workflow.UpdateAsync(ProjectDir, name: null, force: false));

        Assert.Contains("VBeami.Core", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Found in the field: adding vbeami to a real project failed exactly like the test above (the
    /// feed genuinely wasn't adopted) -- but a second, identical attempt then failed a completely
    /// different way: <c>'addons/vbeami' has local modifications that don't match the lockfile</c>,
    /// against a folder nobody had touched by hand.
    /// </summary>
    /// <remarks>
    /// <see cref="SyncAddonGraphAsync"/> installs every addon before the dotnet step runs, so by the
    /// time dotnet resolution threw, vbeami was already sitting on disk with exactly the right
    /// content. But the lockfile was only ever written at the very end of <c>update</c>/<c>add</c> --
    /// after the throw -- so it still had no entry for vbeami. The next attempt's
    /// <see cref="DirtyChecker"/> sees a folder with content but no prior record and calls that
    /// "occupied but untracked", which is indistinguishable from a hand-edit it must not clobber
    /// (SPEC.md P0-6). The fix persists the addon-side lockfile/manifest write the moment
    /// <c>SyncAddonGraphAsync</c> returns, not after the dotnet step also succeeds -- the same
    /// "persist an approval immediately" reasoning already applied to feed adoption a few lines up.
    /// </remarks>
    [Fact]
    public async Task Update_AddonDeclaredNuGetPackage_FeedNotAdopted_RecordsTheInstalledAddonSoARetryDoesNotSeeItAsDirty()
    {
        var (workflow, _, fileSystem) = BuildAddonDeclaredPrivatePackage(
            $"""
            [addons.vbeami]
            git = "{VbeamiUrl}"
            version = "~1"
            """,
            """
            [dotnet.VBeami.Core]
            version = "1.0.0"
            """);

        var first = await Assert.ThrowsAsync<WorkflowException>(
            () => workflow.UpdateAsync(ProjectDir, name: null, force: false));
        Assert.Contains("VBeami.Core", first.Message, StringComparison.Ordinal);

        // vbeami itself installed fine before the dotnet step failed -- the lockfile must already
        // know about it.
        Assert.True(fileSystem.DirectoryExists($"{ProjectDir}/addons/vbeami"));
        var locked = LockfileReader.Read(fileSystem.ReadAllText($"{ProjectDir}/dotquay.lock"));
        Assert.Contains(locked.Addons, a => a.Name == "vbeami");

        // The retry fails for the same real reason -- not because dotquay's own install now looks
        // like an untracked hand-edit.
        var retry = await Assert.ThrowsAsync<WorkflowException>(
            () => workflow.UpdateAsync(ProjectDir, name: null, force: false));
        Assert.Contains("VBeami.Core", retry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("local modifications", retry.Message, StringComparison.Ordinal);
    }

    /// <summary>The same fix, exercised through <c>add</c> instead of <c>update</c> -- the code path
    /// the field report actually used. <c>add</c> writes the new <c>[addons.*]</c> entry itself, so
    /// this also covers the manifest write, not just the lockfile.</summary>
    [Fact]
    public async Task AddAsync_AddonDeclaredNuGetPackage_FeedNotAdopted_RecordsTheInstalledAddonSoARetryDoesNotSeeItAsDirty()
    {
        var (workflow, _, fileSystem) = BuildAddonDeclaredPrivatePackage(
            projectManifest: "",
            addonDotnetBlock: """
                [dotnet.VBeami.Core]
                version = "1.0.0"
                """);

        var first = await Assert.ThrowsAsync<WorkflowException>(() => workflow.AddAsync(ProjectDir, VbeamiUrl));
        Assert.Contains("VBeami.Core", first.Message, StringComparison.Ordinal);

        // The addon installed and got declared before the dotnet step failed.
        Assert.True(fileSystem.DirectoryExists($"{ProjectDir}/addons/vbeami"));
        Assert.Contains("[addons.vbeami]", fileSystem.ReadAllText($"{ProjectDir}/dotquay.toml"));
        var locked = LockfileReader.Read(fileSystem.ReadAllText($"{ProjectDir}/dotquay.lock"));
        Assert.Contains(locked.Addons, a => a.Name == "vbeami");

        // The error's own advice ("use 'dotquay update vbeami' instead") must lead somewhere sane --
        // a repeat of the same real failure, not a spurious dirty-folder refusal.
        var retry = await Assert.ThrowsAsync<WorkflowException>(
            () => workflow.UpdateAsync(ProjectDir, name: null, force: false));
        Assert.Contains("VBeami.Core", retry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("local modifications", retry.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// <see cref="AddonAddOptions.Force"/>, added alongside the fix above for the situation it
    /// doesn't cover: content installed by a binary built <em>before</em> that fix (or a manual copy
    /// predating dotquay entirely) leaves an occupied, untracked folder with no lockfile entry and no
    /// manifest declaration to fall back to <c>update</c> with. <c>add</c> itself has to be able to
    /// reclaim it, the same way <c>update --force</c> already can (SPEC.md P0-6).
    /// </summary>
    [Fact]
    public async Task AddAsync_DestinationAlreadyOccupiedByUntrackedContent_RefusesWithoutForce_SucceedsWithForce()
    {
        var (workflow, fileSystem, gitClient) = Build();
        SetTags(gitClient, "v1.0.0");

        // Predates this add entirely -- a manual copy, or an addon a pre-fix binary already installed
        // correctly but never recorded.
        fileSystem.WriteAllText($"{ProjectDir}/addons/dialogue_manager/plugin.cfg", "not from dotquay");

        var ex = await Assert.ThrowsAsync<WorkflowException>(() => workflow.AddAsync(ProjectDir, RepoUrl));
        Assert.Contains("local modifications", ex.Message, StringComparison.Ordinal);

        var locked = await workflow.AddAsync(ProjectDir, RepoUrl, new AddonAddOptions { Force = true });
        Assert.Equal("dialogue_manager", locked.Name);
        Assert.Equal("cfg", fileSystem.ReadAllText($"{ProjectDir}/addons/dialogue_manager/plugin.cfg"));
    }

    [Fact]
    public async Task Update_AddonDeclaredSourcePin_IsNotHonoured_ResolutionStillSearchesEveryFeed()
    {
        // An addon's `source = "..."` names a feed in the *consuming project's* namespace, which the
        // addon cannot know — the same feed might be called "kemomi" downstream. So the pin is
        // ignored and the package resolves by the normal first-match-in-declared-order rule.
        var (workflow, factory, _) = BuildAddonDeclaredPrivatePackage(
            $"""
            [nuget.sources.gitlab]
            url = "{PrivateFeedUrl}"

            [addons.vbeami]
            git = "{VbeamiUrl}"
            version = "~1"
            """,
            """
            [dotnet.VBeami.Core]
            version = "1.0.0"
            source = "some-name-the-project-never-declared"
            """);

        var result = await workflow.UpdateAsync(ProjectDir, name: null, force: false);

        // It still resolves — an unhonoured pin must not be a hard failure, or every addon would
        // break the moment a consumer renamed a feed.
        Assert.Equal("gitlab", Assert.Single(result.Dotnet).Locked.Source);

        // ...and it got there unpinned, searching the declared order rather than one named feed.
        Assert.All(factory.RequestedPins, pin => Assert.Null(pin));
    }

    // -- v0.8: the addon brings its own feed, so the consumer doesn't have to find it (SPEC.md §11.3)

    private const string AddonDeclaredFeed = """
        [nuget.sources.vbeami]
        url = "https://gitlab.example.com/api/v4/groups/7/-/packages/nuget/index.json"
        packages = ["VBeami.*"]

        [dotnet.VBeami.Core]
        version = "1.0.0"
        """;

    /// <summary>The whole point of v0.8: botv2 declares no feed at all, only the addon.</summary>
    private const string ProjectWithNoFeed = $"""
        [addons.vbeami]
        git = "{VbeamiUrl}"
        version = "~1"
        """;

    [Fact]
    public async Task Update_AddonDeclaresItsOwnFeed_TrustedHost_AdoptsWithoutAsking()
    {
        var approval = new ApproveAllSources();
        var (workflow, _, fileSystem) = BuildAddonDeclaredPrivatePackage(
            $"""
            [nuget]
            trusted_hosts = ["gitlab.example.com"]

            {ProjectWithNoFeed}
            """,
            AddonDeclaredFeed,
            approval);

        var result = await workflow.UpdateAsync(ProjectDir, name: null, force: false);

        // Resolved from a feed the project never declared and the user never typed.
        Assert.Equal("1.2.0", Assert.Single(result.Dotnet).Locked.Resolved);
        Assert.Empty(approval.Seen);

        // ...and it was written into the committed manifest, attributed, so it survives the next
        // wholesale rewrite and shows up in review.
        var manifest = fileSystem.ReadAllText($"{ProjectDir}/dotquay.toml");
        Assert.Contains("[nuget.sources.vbeami]", manifest, StringComparison.Ordinal);
        Assert.Contains("declared_by = \"vbeami\"", manifest, StringComparison.Ordinal);
        Assert.Contains("packages = [\"VBeami.*\"]", manifest, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Update_AddonDeclaresItsOwnFeed_UntrustedHost_AsksAndAdoptsOnYes()
    {
        var approval = new ApproveAllSources();
        var (workflow, _, _) = BuildAddonDeclaredPrivatePackage(ProjectWithNoFeed, AddonDeclaredFeed, approval);

        var result = await workflow.UpdateAsync(ProjectDir, name: null, force: false);

        Assert.Equal("1.2.0", Assert.Single(result.Dotnet).Locked.Resolved);

        // The prompt names the stakes, not just a URL.
        var asked = Assert.Single(approval.Seen);
        Assert.Equal("vbeami", asked.AddonName);
        Assert.Equal(["VBeami.Core"], asked.PackageIds);
    }

    [Fact]
    public async Task Update_AddonDeclaresItsOwnFeed_Refused_ExplainsWhyThePackageIsMissing()
    {
        // DenyAllSourceApproval is the default, so this is also what CI and --json do.
        var (workflow, _, fileSystem) = BuildAddonDeclaredPrivatePackage(ProjectWithNoFeed, AddonDeclaredFeed);

        var exception = await Assert.ThrowsAsync<WorkflowException>(
            () => workflow.UpdateAsync(ProjectDir, name: null, force: false));

        // Not a bare "no stable release on NuGet", which would read as "this package doesn't exist".
        Assert.Contains("was not adopted", exception.Message, StringComparison.Ordinal);
        Assert.Contains("trusted_hosts", exception.Message, StringComparison.Ordinal);

        // And nothing was written into the manifest on the way out.
        Assert.DoesNotContain("nuget.sources", fileSystem.ReadAllText($"{ProjectDir}/dotquay.toml"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Update_AdoptionSurvivesALaterFailure_SoARetryDoesNotReAsk()
    {
        // Found by running it: adoption used to be persisted only after dotnet resolution, so a
        // resolution failure discarded a decision the user had already made — and the next attempt
        // asked again, forever.
        var approval = new ApproveAllSources();
        var (workflow, factory, fileSystem) = BuildAddonDeclaredPrivatePackage(
            ProjectWithNoFeed, AddonDeclaredFeed, approval);

        // The adopted feed exists but has nothing on it, so resolution fails after adoption.
        factory.ClientsBySource["vbeami"] = new FakeNuGetClient("vbeami");

        await Assert.ThrowsAsync<WorkflowException>(() => workflow.UpdateAsync(ProjectDir, name: null, force: false));

        Assert.Single(approval.Seen);
        Assert.Contains(
            "[nuget.sources.vbeami]", fileSystem.ReadAllText($"{ProjectDir}/dotquay.toml"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Install_NeverAdopts_EvenFromATrustedHost()
    {
        var approval = new ApproveAllSources();
        var (workflow, _, fileSystem) = BuildAddonDeclaredPrivatePackage(
            $"""
            [nuget]
            trusted_hosts = ["gitlab.example.com"]

            {ProjectWithNoFeed}
            """,
            AddonDeclaredFeed,
            approval);

        await workflow.UpdateAsync(ProjectDir, name: null, force: false);
        var afterUpdate = fileSystem.ReadAllText($"{ProjectDir}/dotquay.toml");

        // Strip the adopted block back out, then install: replaying a lockfile must not re-acquire a
        // feed the user just deleted (SPEC.md §5 — install resolves nothing).
        fileSystem.WriteAllText($"{ProjectDir}/dotquay.toml", ProjectWithNoFeed);
        await workflow.InstallAsync(ProjectDir, force: false);

        Assert.Contains("[nuget.sources.vbeami]", afterUpdate, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "[nuget.sources.vbeami]", fileSystem.ReadAllText($"{ProjectDir}/dotquay.toml"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Doctor_ReportsTrustWithoutMarkingTheProjectUnhealthy()
    {
        var (workflow, _, fileSystem) = BuildAddonDeclaredPrivatePackage(
            $"""
            [nuget]
            trusted_hosts = ["gitlab.example.com"]

            {ProjectWithNoFeed}
            """,
            AddonDeclaredFeed);

        await workflow.UpdateAsync(ProjectDir, name: null, force: false);
        var result = await workflow.DoctorAsync(ProjectDir);

        // Visible...
        Assert.Equal(["gitlab.example.com"], result.Trust.TrustedHosts);
        var adopted = Assert.Single(result.Trust.AdoptedSources);
        Assert.Equal("vbeami", adopted.DeclaredByAddon);

        // ...but not a fault. Reporting trust as an issue would make every project using the feature
        // permanently unhealthy and fail CI, which is how a checker teaches people to ignore it.
        Assert.DoesNotContain(result.Issues, i => i.Kind == DoctorIssueKind.TrustedHostUnusable);
    }

    [Fact]
    public async Task Doctor_WildcardTrustedHost_IsReportedAsUnusable()
    {
        var (workflow, _, _) = BuildAddonDeclaredPrivatePackage(
            $"""
            [nuget]
            trusted_hosts = ["*.example.com"]

            {ProjectWithNoFeed}
            """,
            AddonDeclaredFeed);

        var result = await workflow.DoctorAsync(ProjectDir);

        var issue = Assert.Single(result.Issues, i => i.Kind == DoctorIssueKind.TrustedHostUnusable);
        Assert.Contains("trusts nothing", issue.Message, StringComparison.Ordinal);
        Assert.False(result.IsHealthy);
    }

    [Fact]
    public async Task Doctor_NoTrustConfigured_SaysNothingAboutIt()
    {
        var (workflow, _, _) = BuildAddonDeclaredPrivatePackage(ProjectWithNoFeed, AddonDeclaredFeed);

        var result = await workflow.DoctorAsync(ProjectDir);

        // A project that never opted in gains no noise.
        Assert.True(result.Trust.IsEmpty);
    }

    [Fact]
    public async Task Update_AdoptedFeed_IsScopedByPackageSourceMapping()
    {
        var (workflow, _, fileSystem) = BuildAddonDeclaredPrivatePackage(
            $"""
            [nuget]
            trusted_hosts = ["gitlab.example.com"]

            {ProjectWithNoFeed}
            """,
            AddonDeclaredFeed);

        await workflow.UpdateAsync(ProjectDir, name: null, force: false);

        var config = fileSystem.ReadAllText($"{ProjectDir}/NuGet.Config");

        // The owned pattern, so VBeami.Core's own private transitive dependencies resolve from the
        // same feed instead of falling to the catch-all and being looked for on nuget.org.
        Assert.Contains("pattern=\"VBeami.*\"", config, StringComparison.Ordinal);
        Assert.Contains("pattern=\"*\"", config, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AddDotnetAsync_ManifestDeclaresNuGetSource_AlsoRegistersItInNuGetConfigForRestore()
    {
        const string privateSource = "https://gitlab.example.com/api/v4/projects/1/packages/nuget/index.json";

        var fileSystem = new InMemoryFileSystem();
        fileSystem.WriteAllText($"{ProjectDir}/dotquay.toml", $"[project]\ngodot = \"4.3\"\nnuget_source = \"{privateSource}\"\n");
        fileSystem.WriteAllText($"{ProjectDir}/Game.csproj", CsprojText);

        var gitClient = new FakeGitClient();
        var privateClient = new FakeNuGetClient();
        privateClient.VersionsByPackageId["Acme.Internal"] = ["2.1.0"];
        var nuGetClientFactory = new FakeNuGetClientFactory(new FakeNuGetClient());
        // Keyed by declared source name — a legacy [project] nuget_source is read as "default".
        nuGetClientFactory.ClientsBySource[NuGetSourceSpec.LegacyName] = privateClient;

        var addonInstaller = new AddonInstaller(
            fileSystem, new RepoCache(fileSystem, gitClient, "cache"), new SyncEngine(fileSystem), new FakePatchTool(fileSystem));
        var runner = new FakeDotnetCliRunner();
        var dotnetInstaller = new DotnetInstaller(fileSystem, runner, TestCredentials.None());
        var workflow = new DependencyWorkflow(fileSystem, gitClient, nuGetClientFactory, addonInstaller, dotnetInstaller);

        await workflow.AddDotnetAsync(ProjectDir, "Acme.Internal");

        // dotquay's own read-side query (asserted above) succeeding is not enough for `dotnet
        // restore` — a separate process with its own config — to also find the package. This is
        // the piece that closes that gap: NuGet.Config must declare the source too.
        var nuGetConfig = fileSystem.ReadAllText($"{ProjectDir}/NuGet.Config");
        Assert.Contains(privateSource, nuGetConfig);
        Assert.Single(runner.RestoreCalls);
    }

    [Fact]
    public async Task ListInstalledDotnet_ReturnsLockedDotnetDependencies()
    {
        var (workflow, _, _, nuGetClient, _) = BuildWithDotnet();
        nuGetClient.VersionsByPackageId["Newtonsoft.Json"] = ["13.0.3"];
        await workflow.AddDotnetAsync(ProjectDir, "Newtonsoft.Json");

        var listed = workflow.ListInstalledDotnet(ProjectDir);

        Assert.Single(listed);
        Assert.Equal("Newtonsoft.Json", listed[0].Name);
    }

    [Fact]
    public async Task OutdatedAsync_VersionSourcedAddonWithNewerMatchingTag_ReportsOutdated()
    {
        var (workflow, _, gitClient) = Build();
        SetTags(gitClient, "v2.44.0");
        await workflow.AddAsync(ProjectDir, RepoUrl); // locks ~2.44 @ v2.44.0

        SetTags(gitClient, "v2.44.0", "v2.44.1"); // a newer patch tag appears upstream

        var result = await workflow.OutdatedAsync(ProjectDir);

        var addon = Assert.Single(result.Addons);
        Assert.True(addon.IsOutdated);
        Assert.Equal("v2.44.0", addon.CurrentDisplay);
        Assert.Equal("v2.44.1", addon.LatestDisplay);
        Assert.Empty(result.Dotnet);
    }

    [Fact]
    public async Task OutdatedAsync_VersionSourcedAddonAlreadyAtHighestMatchingTag_ReportsNotOutdated()
    {
        var (workflow, _, gitClient) = Build();
        SetTags(gitClient, "v2.44.0");
        await workflow.AddAsync(ProjectDir, RepoUrl);

        var result = await workflow.OutdatedAsync(ProjectDir);

        var addon = Assert.Single(result.Addons);
        Assert.False(addon.IsOutdated);
        Assert.Null(addon.LatestDisplay);
    }

    [Fact]
    public async Task OutdatedAsync_BranchSourcedAddonTipMoved_ReportsOutdated()
    {
        var fileSystem = new InMemoryFileSystem();
        var lockedSha = new string('a', 40);
        var newTipSha = new string('b', 40);
        fileSystem.WriteAllText(
            $"{ProjectDir}/dotquay.toml", "[addons.tracked]\ngit = \"https://example.com/tracked.git\"\nbranch = \"main\"\n");
        fileSystem.WriteAllText(
            $"{ProjectDir}/dotquay.lock",
            $"[[addon]]\nname = \"tracked\"\ngit = \"https://example.com/tracked.git\"\nresolved = \"{lockedSha}\"\npath = \"addons/tracked\"\n");

        var gitClient = new FakeGitClient();
        gitClient.BranchTips[("https://example.com/tracked.git", "main")] = newTipSha;
        var workflow = new DependencyWorkflow(fileSystem, gitClient, "cache");

        var result = await workflow.OutdatedAsync(ProjectDir);

        var addon = Assert.Single(result.Addons);
        Assert.True(addon.IsOutdated);
        Assert.Equal(newTipSha[..7], addon.LatestDisplay);
    }

    [Fact]
    public async Task OutdatedAsync_RevSourcedAddon_IsSkippedEntirely()
    {
        var fileSystem = new InMemoryFileSystem();
        var pinnedSha = new string('c', 40);
        fileSystem.WriteAllText(
            $"{ProjectDir}/dotquay.toml", $"[addons.pinned]\ngit = \"https://example.com/pinned.git\"\nrev = \"{pinnedSha}\"\n");
        fileSystem.WriteAllText(
            $"{ProjectDir}/dotquay.lock",
            $"[[addon]]\nname = \"pinned\"\ngit = \"https://example.com/pinned.git\"\nresolved = \"{pinnedSha}\"\npath = \"addons/pinned\"\n");
        var workflow = new DependencyWorkflow(fileSystem, new FakeGitClient(), "cache");

        var result = await workflow.OutdatedAsync(ProjectDir);

        Assert.Empty(result.Addons); // a hard pin has nothing to check against — not even "up to date"
    }

    [Fact]
    public async Task OutdatedAsync_LockedButNoLongerDeclaredInManifest_IsSkipped()
    {
        var (workflow, fileSystem, gitClient) = Build();
        SetTags(gitClient, "v1.0.0");
        await workflow.AddAsync(ProjectDir, RepoUrl);

        // Simulate a teammate removing the [addons.*] entry by hand without running `dotquay remove`.
        fileSystem.WriteAllText($"{ProjectDir}/dotquay.toml", "[project]\ngodot = \"4.3\"\n");

        var result = await workflow.OutdatedAsync(ProjectDir);

        Assert.Empty(result.Addons);
    }

    [Fact]
    public async Task OutdatedAsync_DotnetPackageWithNewerStableVersion_ReportsOutdated()
    {
        var (workflow, _, _, nuGetClient, _) = BuildWithDotnet();
        nuGetClient.VersionsByPackageId["Newtonsoft.Json"] = ["13.0.3"];
        await workflow.AddDotnetAsync(ProjectDir, "Newtonsoft.Json");

        nuGetClient.VersionsByPackageId["Newtonsoft.Json"] = ["13.0.3", "13.0.4"];

        var result = await workflow.OutdatedAsync(ProjectDir);

        var dependency = Assert.Single(result.Dotnet);
        Assert.True(dependency.IsOutdated);
        Assert.Equal("13.0.3", dependency.CurrentVersion);
        Assert.Equal("13.0.4", dependency.LatestVersion);
    }

    [Fact]
    public async Task OutdatedAsync_DotnetPackageAlreadyLatest_ReportsNotOutdated()
    {
        var (workflow, _, _, nuGetClient, _) = BuildWithDotnet();
        nuGetClient.VersionsByPackageId["Newtonsoft.Json"] = ["13.0.3"];
        await workflow.AddDotnetAsync(ProjectDir, "Newtonsoft.Json");

        var result = await workflow.OutdatedAsync(ProjectDir);

        var dependency = Assert.Single(result.Dotnet);
        Assert.False(dependency.IsOutdated);
        Assert.Null(dependency.LatestVersion);
    }

    [Fact]
    public async Task OutdatedAsync_NoManifest_Throws()
    {
        var fileSystem = new InMemoryFileSystem();
        var workflow = new DependencyWorkflow(fileSystem, new FakeGitClient(), "cache");

        var ex = await Assert.ThrowsAsync<WorkflowException>(() => workflow.OutdatedAsync(ProjectDir));
        Assert.Contains("dotquay init", ex.Message);
    }

    [Fact]
    public void Status_NoLockfile_ReturnsEmptyRatherThanThrowing()
    {
        var (workflow, _, _) = Build();

        var result = workflow.Status(ProjectDir);

        Assert.Empty(result.Addons);
    }

    [Fact]
    public async Task Status_UntouchedAfterInstall_IsClean()
    {
        var (workflow, _, gitClient) = Build();
        SetTags(gitClient, "v1.0.0");
        await workflow.AddAsync(ProjectDir, RepoUrl);

        var result = workflow.Status(ProjectDir);

        var addon = Assert.Single(result.Addons);
        Assert.Equal(AddonStatusKind.Clean, addon.Kind);
    }

    [Fact]
    public async Task Status_HandEditedAfterInstall_IsModified()
    {
        var (workflow, fileSystem, gitClient) = Build();
        SetTags(gitClient, "v1.0.0");
        await workflow.AddAsync(ProjectDir, RepoUrl);

        fileSystem.WriteAllText($"{ProjectDir}/addons/dialogue_manager/hand-edit.txt", "surprise");

        var result = workflow.Status(ProjectDir);

        var addon = Assert.Single(result.Addons);
        Assert.Equal(AddonStatusKind.Modified, addon.Kind);
    }

    [Fact]
    public async Task Status_FolderDeletedAfterInstall_IsMissing()
    {
        var (workflow, fileSystem, gitClient) = Build();
        SetTags(gitClient, "v1.0.0");
        await workflow.AddAsync(ProjectDir, RepoUrl);

        fileSystem.DeleteDirectory($"{ProjectDir}/addons/dialogue_manager");

        var result = workflow.Status(ProjectDir);

        var addon = Assert.Single(result.Addons);
        Assert.Equal(AddonStatusKind.Missing, addon.Kind);
    }

    [Fact]
    public void Status_PatchHashRecordedAndContentMatches_IsPatchedAsExpected()
    {
        var fileSystem = new InMemoryFileSystem();
        fileSystem.WriteAllText($"{ProjectDir}/addons/patched_addon/plugin.cfg", "cfg");
        var contentHash = Dotquay.Core.Sync.ContentHasher.HashDirectory(fileSystem, $"{ProjectDir}/addons/patched_addon");
        fileSystem.WriteAllText(
            $"{ProjectDir}/dotquay.lock",
            "[[addon]]\nname = \"patched_addon\"\ngit = \"https://example.com/patched_addon.git\"\n"
            + $"resolved = \"{new string('a', 40)}\"\npath = \"addons/patched_addon\"\ncontent_hash = \"{contentHash}\"\n"
            + "patch_hash = \"sha256:deadbeef\"\n");
        var workflow = new DependencyWorkflow(fileSystem, new FakeGitClient(), "cache");

        var result = workflow.Status(ProjectDir);

        var addon = Assert.Single(result.Addons);
        Assert.Equal(AddonStatusKind.PatchedAsExpected, addon.Kind);
    }

    [Fact]
    public async Task DoctorAsync_HealthyProject_ReportsNoIssues()
    {
        var (workflow, _, gitClient) = Build();
        SetTags(gitClient, "v1.0.0");
        await workflow.AddAsync(ProjectDir, RepoUrl);

        var result = await workflow.DoctorAsync(ProjectDir);

        Assert.True(result.IsHealthy);
    }

    [Fact]
    public async Task DoctorAsync_GitNotAvailable_ReportsIt()
    {
        var (workflow, _, gitClient) = Build();
        gitClient.IsGitAvailable = false;

        var result = await workflow.DoctorAsync(ProjectDir);

        Assert.Contains(result.Issues, i => i.Kind == DoctorIssueKind.GitNotOnPath);
    }

    [Fact]
    public async Task DoctorAsync_LockedAddonNoLongerInManifest_ReportsDanglingEntry()
    {
        var (workflow, fileSystem, gitClient) = Build();
        SetTags(gitClient, "v1.0.0");
        await workflow.AddAsync(ProjectDir, RepoUrl);

        fileSystem.WriteAllText($"{ProjectDir}/dotquay.toml", "[project]\ngodot = \"4.3\"\n");

        var result = await workflow.DoctorAsync(ProjectDir);

        Assert.Contains(
            result.Issues, i => i.Kind == DoctorIssueKind.DanglingLockfileEntry && i.Message.Contains("dialogue_manager"));
    }

    [Fact]
    public async Task DoctorAsync_LockedDotnetNoLongerInManifest_ReportsDanglingEntry()
    {
        var (workflow, fileSystem, _, nuGetClient, _) = BuildWithDotnet();
        nuGetClient.VersionsByPackageId["Newtonsoft.Json"] = ["13.0.3"];
        await workflow.AddDotnetAsync(ProjectDir, "Newtonsoft.Json");

        fileSystem.WriteAllText($"{ProjectDir}/dotquay.toml", "[project]\ngodot = \"4.3\"\n");

        var result = await workflow.DoctorAsync(ProjectDir);

        Assert.Contains(
            result.Issues, i => i.Kind == DoctorIssueKind.DanglingLockfileEntry && i.Message.Contains("Newtonsoft.Json"));
    }

    [Fact]
    public async Task DoctorAsync_PluginCfgVersionDrift_ReportsIt()
    {
        var (workflow, fileSystem, gitClient) = Build();
        SetTags(gitClient, "v2.44.0");
        await workflow.AddAsync(ProjectDir, RepoUrl);

        fileSystem.WriteAllText($"{ProjectDir}/addons/dialogue_manager/plugin.cfg", "[plugin]\nversion=\"2.0.0\"\n");

        var result = await workflow.DoctorAsync(ProjectDir);

        Assert.Contains(
            result.Issues, i => i.Kind == DoctorIssueKind.PluginCfgVersionDrift && i.Message.Contains("dialogue_manager"));
    }

    [Fact]
    public async Task DoctorAsync_PluginCfgVersionMatchesLockfile_NoIssue()
    {
        var (workflow, fileSystem, gitClient) = Build();
        SetTags(gitClient, "v2.44.0");
        await workflow.AddAsync(ProjectDir, RepoUrl);

        fileSystem.WriteAllText($"{ProjectDir}/addons/dialogue_manager/plugin.cfg", "[plugin]\nversion=\"2.44.0\"\n");

        var result = await workflow.DoctorAsync(ProjectDir);

        Assert.DoesNotContain(result.Issues, i => i.Kind == DoctorIssueKind.PluginCfgVersionDrift);
    }

    [Fact]
    public async Task DoctorAsync_PseudoVersionedAddon_SkipsPluginCfgDriftCheck()
    {
        var (workflow, fileSystem, gitClient) = Build();
        gitClient.DefaultBranches[RepoUrl] = "main";
        var tipSha = new string('b', 40);
        gitClient.BranchTips[(RepoUrl, "main")] = tipSha;
        await workflow.AddAsync(ProjectDir, RepoUrl); // no tags -> pseudo-version pinned as rev

        fileSystem.WriteAllText($"{ProjectDir}/addons/dialogue_manager/plugin.cfg", "[plugin]\nversion=\"999.0.0\"\n");

        var result = await workflow.DoctorAsync(ProjectDir);

        Assert.DoesNotContain(result.Issues, i => i.Kind == DoctorIssueKind.PluginCfgVersionDrift);
    }

    [Fact]
    public async Task DoctorAsync_OrphanedAddonFolder_ReportsIt()
    {
        var (workflow, fileSystem, _) = Build();
        fileSystem.WriteAllText($"{ProjectDir}/addons/mystery/plugin.cfg", "cfg");

        var result = await workflow.DoctorAsync(ProjectDir);

        Assert.Contains(result.Issues, i => i.Kind == DoctorIssueKind.OrphanedAddonFolder && i.Message.Contains("mystery"));
    }

    [Fact]
    public async Task Status_AddonWithPathOverride_ChecksActualInstallLocationNotSourceSubtree()
    {
        // Regression test: LockedAddon.Path records the SOURCE subtree extracted from the upstream
        // repo (spec.EffectivePath), not the install destination — AddonInstaller always installs to
        // "addons/<name>" regardless of --path. Status() must check the real install location.
        const string mismatchedRepoUrl = "https://example.com/godot_dialogue_manager.git";
        var fileSystem = new InMemoryFileSystem();
        fileSystem.WriteAllText($"{ProjectDir}/dotquay.toml", "[project]\ngodot = \"4.3\"\n");
        var gitClient = new FakeGitClient { FileSystemForFetchedContent = fileSystem };
        gitClient.FetchedFilesByUrl[mismatchedRepoUrl] = new Dictionary<string, string>
        {
            ["addons/dialogue_manager/plugin.cfg"] = "cfg",
        };
        gitClient.RemoteRefsByUrl[mismatchedRepoUrl] = [new GitRef("v1.0.0", new string('a', 40), GitRefKind.Tag)];
        var workflow = new DependencyWorkflow(fileSystem, gitClient, "cache");

        var locked = await workflow.AddAsync(ProjectDir, mismatchedRepoUrl, new AddonAddOptions { Path = "addons/dialogue_manager" });
        Assert.Equal("addons/dialogue_manager", locked.Path); // source subtree
        Assert.True(fileSystem.FileExists($"{ProjectDir}/addons/godot_dialogue_manager/plugin.cfg")); // actual install location

        var result = workflow.Status(ProjectDir);

        var addon = Assert.Single(result.Addons);
        Assert.Equal(AddonStatusKind.Clean, addon.Kind); // not Missing, which is what the bug produced
    }

    [Fact]
    public async Task DoctorAsync_AddonWithPathOverride_ChecksActualInstallLocationNotSourceSubtree()
    {
        const string mismatchedRepoUrl = "https://example.com/godot_dialogue_manager.git";
        var fileSystem = new InMemoryFileSystem();
        fileSystem.WriteAllText($"{ProjectDir}/dotquay.toml", "[project]\ngodot = \"4.3\"\n");
        var gitClient = new FakeGitClient { FileSystemForFetchedContent = fileSystem };
        gitClient.FetchedFilesByUrl[mismatchedRepoUrl] = new Dictionary<string, string>
        {
            ["addons/dialogue_manager/plugin.cfg"] = "[plugin]\nversion=\"1.0.0\"\n",
        };
        gitClient.RemoteRefsByUrl[mismatchedRepoUrl] = [new GitRef("v1.0.0", new string('a', 40), GitRefKind.Tag)];
        var workflow = new DependencyWorkflow(fileSystem, gitClient, "cache");
        await workflow.AddAsync(ProjectDir, mismatchedRepoUrl, new AddonAddOptions { Path = "addons/dialogue_manager" });

        var result = await workflow.DoctorAsync(ProjectDir);

        // The bug would have looked for plugin.cfg drift at "addons/dialogue_manager/plugin.cfg"
        // (the source subtree) instead of "addons/godot_dialogue_manager/plugin.cfg" (the real
        // install location) — PluginCfgReader would find nothing there and silently skip, but
        // worse, it also wouldn't find the folder at all for the orphan check consistently. The
        // real assertion that matters: no false PluginCfgVersionDrift/OrphanedAddonFolder noise from
        // simply having used --path.
        Assert.True(result.IsHealthy);
    }

    [Fact]
    public async Task DoctorAsync_NoManifest_SkipsDanglingCheckButStillRunsOthers()
    {
        var fileSystem = new InMemoryFileSystem();
        fileSystem.WriteAllText($"{ProjectDir}/addons/mystery/plugin.cfg", "cfg");
        var workflow = new DependencyWorkflow(fileSystem, new FakeGitClient(), "cache");

        var result = await workflow.DoctorAsync(ProjectDir);

        Assert.DoesNotContain(result.Issues, i => i.Kind == DoctorIssueKind.DanglingLockfileEntry);
        Assert.Contains(result.Issues, i => i.Kind == DoctorIssueKind.OrphanedAddonFolder);
    }

    [Fact]
    public async Task PatchAsync_HandEditedAddon_WritesPatchFileAndRecordsHashAndBaseSha()
    {
        var (workflow, fileSystem, gitClient, patchTool) = BuildWithPatchTool();
        SetTags(gitClient, "v1.0.0");
        var locked = await workflow.AddAsync(ProjectDir, RepoUrl);

        fileSystem.WriteAllText($"{ProjectDir}/addons/dialogue_manager/plugin.cfg", "cfg-edited");

        var basePath = PatchBaseStagingPath(fileSystem, "dialogue_manager");
        var installedPath = fileSystem.CombinePath(ProjectDir, "addons", "dialogue_manager");
        patchTool.DiffResults[(basePath, installedPath)] = "fake-diff-text";

        var result = await workflow.PatchAsync(ProjectDir, "dialogue_manager");

        Assert.True(result.Captured);
        Assert.Equal("dialogue_manager", result.Name);
        Assert.Equal($"{ProjectDir}/.dotquay/patches/dialogue_manager.patch", result.PatchFilePath);
        Assert.Equal("fake-diff-text", fileSystem.ReadAllText(result.PatchFilePath));

        var lockedAfter = LockfileReader.Read(fileSystem.ReadAllText($"{ProjectDir}/dotquay.lock")).Addons.Single();
        Assert.StartsWith("sha256:", lockedAfter.PatchHash);
        Assert.Equal(locked.Resolved, lockedAfter.PatchBaseSha);
    }

    [Fact]
    public async Task PatchAsync_NoDifferenceFromPristineBase_DoesNotCapture()
    {
        var (workflow, _, gitClient, _) = BuildWithPatchTool();
        SetTags(gitClient, "v1.0.0");
        await workflow.AddAsync(ProjectDir, RepoUrl);

        // No DiffResults entry configured -> FakePatchTool's default is an empty diff, same as a
        // real `git diff --no-index` finding no differences.
        var result = await workflow.PatchAsync(ProjectDir, "dialogue_manager");

        Assert.False(result.Captured);
    }

    [Fact]
    public async Task PatchAsync_EmptyDiffAfterPreviouslyCaptured_ClearsPatchAndDeletesFile()
    {
        var (workflow, fileSystem, gitClient, patchTool) = BuildWithPatchTool();
        SetTags(gitClient, "v1.0.0");
        await workflow.AddAsync(ProjectDir, RepoUrl);

        var basePath = PatchBaseStagingPath(fileSystem, "dialogue_manager");
        var installedPath = fileSystem.CombinePath(ProjectDir, "addons", "dialogue_manager");
        patchTool.DiffResults[(basePath, installedPath)] = "fake-diff-text";
        var captured = await workflow.PatchAsync(ProjectDir, "dialogue_manager");
        Assert.True(captured.Captured);

        // The hand-edit was reverted (or never existed) since -> re-diffing now finds nothing.
        patchTool.DiffResults[(basePath, installedPath)] = "";
        var result = await workflow.PatchAsync(ProjectDir, "dialogue_manager");

        Assert.False(result.Captured);
        Assert.False(fileSystem.FileExists(result.PatchFilePath));

        var lockedAfter = LockfileReader.Read(fileSystem.ReadAllText($"{ProjectDir}/dotquay.lock")).Addons.Single();
        Assert.Null(lockedAfter.PatchHash);
        Assert.Null(lockedAfter.PatchBaseSha);
    }

    [Fact]
    public async Task PatchAsync_AddonNotLocked_Throws()
    {
        var (workflow, _, _, _) = BuildWithPatchTool();

        var ex = await Assert.ThrowsAsync<WorkflowException>(() => workflow.PatchAsync(ProjectDir, "unknown_addon"));
        Assert.Contains("unknown_addon", ex.Message);
    }

    [Fact]
    public async Task PatchAsync_AddonNotInstalledOnDisk_Throws()
    {
        var (workflow, fileSystem, gitClient, _) = BuildWithPatchTool();
        SetTags(gitClient, "v1.0.0");
        await workflow.AddAsync(ProjectDir, RepoUrl);
        fileSystem.DeleteDirectory($"{ProjectDir}/addons/dialogue_manager");

        var ex = await Assert.ThrowsAsync<WorkflowException>(() => workflow.PatchAsync(ProjectDir, "dialogue_manager"));
        Assert.Contains("dotquay install", ex.Message);
    }

    [Fact]
    public async Task PatchAsync_DiffToolFails_WrapsAsWorkflowException()
    {
        var (workflow, fileSystem, gitClient, patchTool) = BuildWithPatchTool();
        SetTags(gitClient, "v1.0.0");
        await workflow.AddAsync(ProjectDir, RepoUrl);

        var basePath = PatchBaseStagingPath(fileSystem, "dialogue_manager");
        var installedPath = fileSystem.CombinePath(ProjectDir, "addons", "dialogue_manager");
        patchTool.DiffFailures.Add((basePath, installedPath));

        var ex = await Assert.ThrowsAsync<WorkflowException>(() => workflow.PatchAsync(ProjectDir, "dialogue_manager"));
        Assert.Contains("dialogue_manager", ex.Message);
    }

    // -- v0.4: dependency graph (P1-2) and addon-declared dotnet (P1-5), end to end through the workflow --

    private const string AUrl = "https://example.com/a.git";
    private const string BUrl = "https://example.com/b.git";
    private const string CoreUrl = "https://example.com/core.git";

    /// <summary>Configures A and B's fake repos so each's own <c>dotquay.toml</c> declares a
    /// version constraint on a shared <c>core</c> addon — same overlapping-but-not-identical
    /// tilde ranges as <c>DependencyGraphResolverTests</c>' diamond scenario (<c>~1</c> vs.
    /// <c>~1.4</c>, overlapping only at 1.4.x), so unifying them must land on core's highest
    /// 1.4.x tag.</summary>
    private static void SetUpDiamond(FakeGitClient gitClient, string aCoreConstraint, string bCoreConstraint)
    {
        gitClient.RemoteRefsByUrl[CoreUrl] = new[] { "v1.2.0", "v1.3.0", "v1.4.0", "v1.4.2", "v2.0.0" }
            .Select((tag, i) => new GitRef(tag, new string('e', 39) + i, GitRefKind.Tag))
            .ToList();
        gitClient.FetchedFilesByUrl[CoreUrl] = new Dictionary<string, string> { ["addons/core/plugin.cfg"] = "core" };

        gitClient.RemoteRefsByUrl[AUrl] = [new GitRef("v1.0.0", new string('a', 40), GitRefKind.Tag)];
        gitClient.FetchedFilesByUrl[AUrl] = new Dictionary<string, string>
        {
            ["addons/a/plugin.cfg"] = "a",
            ["addons/a/dotquay.toml"] = $"""
                [package]
                name = "a"
                version = "1.0.0"

                [addons.core]
                git = "{CoreUrl}"
                version = "{aCoreConstraint}"
                """,
        };

        gitClient.RemoteRefsByUrl[BUrl] = [new GitRef("v1.0.0", new string('f', 40), GitRefKind.Tag)];
        gitClient.FetchedFilesByUrl[BUrl] = new Dictionary<string, string>
        {
            ["addons/b/plugin.cfg"] = "b",
            ["addons/b/dotquay.toml"] = $"""
                [package]
                name = "b"
                version = "1.0.0"

                [addons.core]
                git = "{CoreUrl}"
                version = "{bCoreConstraint}"
                """,
        };
    }

    [Fact]
    public async Task AddAsync_DiamondDependency_UnifiesToOneSharedCoreAddon()
    {
        var (workflow, fileSystem, gitClient) = Build();
        SetUpDiamond(gitClient, aCoreConstraint: "~1", bCoreConstraint: "~1.4");

        await workflow.AddAsync(ProjectDir, AUrl);
        // Adding "b" freezes "a" (already locked) to its exact commit for this walk, but "a" still
        // fully participates in discovery -- its own declared "core" requirement is re-read at that
        // frozen commit, so it still competes with "b"'s requirement during unification.
        await workflow.AddAsync(ProjectDir, BUrl);

        var lockfile = LockfileReader.Read(fileSystem.ReadAllText($"{ProjectDir}/dotquay.lock"));
        Assert.Equal(3, lockfile.Addons.Count); // a, b, core -- exactly one copy of core, not two
        var core = lockfile.Addons.Single(a => a.Name == "core");
        Assert.Equal("v1.4.2", core.Version);
        Assert.True(fileSystem.DirectoryExists($"{ProjectDir}/addons/core"));

        var a = lockfile.Addons.Single(a => a.Name == "a");
        Assert.Equal(["core"], a.Dependencies);
    }

    [Fact]
    public async Task AddAsync_DisjointVersionRangesOnSharedDependency_ThrowsWorkflowException()
    {
        var (workflow, _, gitClient) = Build();
        SetUpDiamond(gitClient, aCoreConstraint: "~1.2", bCoreConstraint: "~1.4"); // never overlap

        await workflow.AddAsync(ProjectDir, AUrl);

        var ex = await Assert.ThrowsAsync<WorkflowException>(() => workflow.AddAsync(ProjectDir, BUrl));
        Assert.Contains("core", ex.Message);
    }

    [Fact]
    public async Task RemoveAsync_TransitiveDependencyOrphanedByRemoval_IsPrunedFromDiskAndLockfile()
    {
        var (workflow, fileSystem, gitClient) = Build();
        gitClient.RemoteRefsByUrl[AUrl] = [new GitRef("v1.0.0", new string('a', 40), GitRefKind.Tag)];
        gitClient.FetchedFilesByUrl[AUrl] = new Dictionary<string, string>
        {
            ["addons/a/plugin.cfg"] = "a",
            ["addons/a/dotquay.toml"] = $"""
                [package]
                name = "a"
                version = "1.0.0"

                [addons.core]
                git = "{CoreUrl}"
                version = "~1"
                """,
        };
        gitClient.RemoteRefsByUrl[CoreUrl] = [new GitRef("v1.0.0", new string('e', 40), GitRefKind.Tag)];
        gitClient.FetchedFilesByUrl[CoreUrl] = new Dictionary<string, string> { ["addons/core/plugin.cfg"] = "core" };

        await workflow.AddAsync(ProjectDir, AUrl);
        Assert.True(fileSystem.DirectoryExists($"{ProjectDir}/addons/core")); // sanity: pulled in transitively

        await workflow.RemoveAsync(ProjectDir, "a");

        Assert.False(fileSystem.DirectoryExists($"{ProjectDir}/addons/a"));
        Assert.False(fileSystem.DirectoryExists($"{ProjectDir}/addons/core")); // nothing else needs it -> pruned
        var lockfile = LockfileReader.Read(fileSystem.ReadAllText($"{ProjectDir}/dotquay.lock"));
        Assert.Empty(lockfile.Addons);
    }

    [Fact]
    public async Task RemoveAsync_TransitiveDependencyStillNeededByAnotherRoot_IsKept()
    {
        var (workflow, fileSystem, gitClient) = Build();
        SetUpDiamond(gitClient, aCoreConstraint: "~1", bCoreConstraint: "~1"); // no conflict, both need core
        await workflow.AddAsync(ProjectDir, AUrl);
        await workflow.AddAsync(ProjectDir, BUrl);

        await workflow.RemoveAsync(ProjectDir, "a");

        Assert.False(fileSystem.DirectoryExists($"{ProjectDir}/addons/a"));
        Assert.True(fileSystem.DirectoryExists($"{ProjectDir}/addons/core")); // "b" still needs it
        var lockfile = LockfileReader.Read(fileSystem.ReadAllText($"{ProjectDir}/dotquay.lock"));
        Assert.Equal(2, lockfile.Addons.Count); // b, core
    }

    [Fact]
    public async Task UpdateAsync_NoNameArg_PrunesOrphanedTransitiveDependencyNoLongerDeclared()
    {
        var (workflow, fileSystem, gitClient) = Build();
        var originalSha = new string('a', 40);
        gitClient.RemoteRefsByUrl[AUrl] = [new GitRef("v1.0.0", originalSha, GitRefKind.Tag)];
        gitClient.FetchedFilesByUrl[AUrl] = new Dictionary<string, string>
        {
            ["addons/a/plugin.cfg"] = "a",
            ["addons/a/dotquay.toml"] = $"""
                [package]
                name = "a"
                version = "1.0.0"

                [addons.core]
                git = "{CoreUrl}"
                version = "~1"
                """,
        };
        gitClient.RemoteRefsByUrl[CoreUrl] = [new GitRef("v1.0.0", new string('e', 40), GitRefKind.Tag)];
        gitClient.FetchedFilesByUrl[CoreUrl] = new Dictionary<string, string> { ["addons/core/plugin.cfg"] = "core" };
        await workflow.AddAsync(ProjectDir, AUrl); // locks "~1.0" @ v1.0.0, pulling in core transitively

        // A new patch tag appears upstream whose own manifest dropped the dependency on core (and,
        // being a genuinely new commit, isn't already sitting in the content-addressed fetch cache
        // under the old SHA -- a full update must actually re-fetch it to see this).
        var newSha = new string('c', 40);
        gitClient.RemoteRefsByUrl[AUrl] = [new GitRef("v1.0.0", originalSha, GitRefKind.Tag), new GitRef("v1.0.1", newSha, GitRefKind.Tag)];
        gitClient.FetchedFilesByUrlAndSha[(AUrl, newSha)] = new Dictionary<string, string> { ["addons/a/plugin.cfg"] = "a" };

        await workflow.UpdateAsync(ProjectDir, name: null, force: false);

        Assert.False(fileSystem.DirectoryExists($"{ProjectDir}/addons/core"));
        var lockfile = LockfileReader.Read(fileSystem.ReadAllText($"{ProjectDir}/dotquay.lock"));
        Assert.Single(lockfile.Addons);
        Assert.Equal("a", lockfile.Addons[0].Name);
        Assert.Equal(newSha, lockfile.Addons[0].Resolved);
    }

    [Fact]
    public async Task AddAsync_AddonDeclaredDotnetDependency_IsInstalledIntoCsprojAndLocked()
    {
        var fileSystem = new InMemoryFileSystem();
        fileSystem.WriteAllText($"{ProjectDir}/dotquay.toml", "[project]\ngodot = \"4.3\"\n");
        fileSystem.WriteAllText($"{ProjectDir}/Game.csproj", CsprojText);

        var gitClient = new FakeGitClient { FileSystemForFetchedContent = fileSystem };
        gitClient.RemoteRefsByUrl[RepoUrl] = [new GitRef("v1.0.0", new string('a', 40), GitRefKind.Tag)];
        gitClient.FetchedFilesByUrl[RepoUrl] = new Dictionary<string, string>
        {
            ["addons/dialogue_manager/plugin.cfg"] = "cfg",
            ["addons/dialogue_manager/dotquay.toml"] = """
                [package]
                name = "dialogue_manager"
                version = "1.0.0"

                [dotnet.Newtonsoft.Json]
                version = "13.0.*"
                """,
        };

        var nuGetClient = new FakeNuGetClient();
        nuGetClient.VersionsByPackageId["Newtonsoft.Json"] = ["13.0.3"];
        var nuGetClientFactory = new FakeNuGetClientFactory(nuGetClient);
        var addonInstaller = new AddonInstaller(
            fileSystem, new RepoCache(fileSystem, gitClient, "cache"), new SyncEngine(fileSystem), new FakePatchTool(fileSystem));
        var runner = new FakeDotnetCliRunner();
        var dotnetInstaller = new DotnetInstaller(fileSystem, runner, TestCredentials.None());
        var workflow = new DependencyWorkflow(fileSystem, gitClient, nuGetClientFactory, addonInstaller, dotnetInstaller);

        await workflow.AddAsync(ProjectDir, RepoUrl);

        Assert.Contains("Newtonsoft.Json", fileSystem.ReadAllText($"{ProjectDir}/Game.csproj"));
        var lockfile = LockfileReader.Read(fileSystem.ReadAllText($"{ProjectDir}/dotquay.lock"));
        var dotnet = Assert.Single(lockfile.Dotnet);
        Assert.Equal("Newtonsoft.Json", dotnet.Name);
        Assert.Equal("13.0.3", dotnet.Resolved);

        // The manifest itself is unchanged by this -- P1-5 unions the addon-declared dependency into
        // the lockfile/csproj, it doesn't write a [dotnet.*] entry the user never asked for.
        var manifest = ManifestReader.ReadProject(fileSystem.ReadAllText($"{ProjectDir}/dotquay.toml"));
        Assert.Empty(manifest.Dotnet);
    }
}
