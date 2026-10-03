using Dotquay.Core.Abstractions;
using Dotquay.Core.Dotnet;
using Dotquay.Core.Git;
using Dotquay.Core.Json;
using Dotquay.Core.Sync;
using Dotquay.Core.Workflows;
using Dotquay.Tests.Support;
using Xunit;

namespace Dotquay.Tests.Workflows;

/// <summary>
/// Covers <c>dotquay add --preview</c> (SPEC.md §6, §11.3): the read-only resolve the editor plugin
/// shows before it commits to an install, and the <c>--adopt-source</c> pre-approval that completes
/// the flow.
/// </summary>
/// <remarks>
/// The first thing every test here asserts, directly or by construction, is that nothing was written.
/// A preview that quietly mutates is worse than no preview, because the user's "cancel" would already
/// be too late — so the no-write property is tested as its own fact rather than assumed from reading
/// the implementation.
/// </remarks>
public class AddPreviewTests
{
    private const string ProjectDir = "project";
    private const string VbeamiUrl = "https://gitlab.example.com/wild-alliance/vbeami.git";
    private const string CoreUrl = "https://gitlab.example.com/wild-alliance/vbeami_core.git";
    private const string FeedUrl = "https://gitlab.example.com/api/v4/groups/7/-/packages/nuget/index.json";

    private const string ManifestPath = $"{ProjectDir}/dotquay.toml";
    private const string LockfilePath = $"{ProjectDir}/dotquay.lock";

    private const string CsprojText = """
        <Project Sdk="Godot.NET.Sdk/4.3.0">
          <PropertyGroup>
            <TargetFramework>net8.0</TargetFramework>
          </PropertyGroup>
        </Project>
        """;

    /// <summary>The addon's own package manifest: it declares a private feed, the package that feed
    /// owns, and a dependency on a second addon — every ingredient a confirmation dialog exists to
    /// show.</summary>
    private const string VbeamiPackageManifest = $"""
        [package]
        name = "vbeami"
        version = "1.0.0"

        [nuget.sources.vbeami]
        url = "{FeedUrl}"
        packages = ["VBeami.*"]

        [dotnet.VBeami.Core]
        version = "^1.0"

        [addons.vbeami_core]
        git = "{CoreUrl}"
        version = "~1"
        """;

    private sealed class Fixture
    {
        public required DependencyWorkflow Workflow { get; init; }

        public required InMemoryFileSystem FileSystem { get; init; }
    }

    private static Fixture Build(string projectManifest, ISourceApproval? approval = null)
    {
        var fileSystem = new InMemoryFileSystem();
        fileSystem.WriteAllText(ManifestPath, projectManifest);
        fileSystem.WriteAllText($"{ProjectDir}/Game.csproj", CsprojText);

        var gitClient = new FakeGitClient { FileSystemForFetchedContent = fileSystem };

        gitClient.RemoteRefsByUrl[VbeamiUrl] = [new GitRef("v1.0.0", new string('b', 40), GitRefKind.Tag)];
        gitClient.FetchedFilesByUrl[VbeamiUrl] = new Dictionary<string, string>
        {
            ["addons/vbeami/plugin.cfg"] = "vbeami",
            ["addons/vbeami/dotquay.toml"] = VbeamiPackageManifest,
        };

        gitClient.RemoteRefsByUrl[CoreUrl] = [new GitRef("v1.1.0", new string('c', 40), GitRefKind.Tag)];
        gitClient.FetchedFilesByUrl[CoreUrl] = new Dictionary<string, string>
        {
            ["addons/vbeami_core/plugin.cfg"] = "vbeami_core",
        };

        var factory = new FakeNuGetClientFactory(new FakeNuGetClient());
        factory.ClientsBySource["vbeami"] = new FakeNuGetClient("vbeami").With("VBeami.Core", "1.2.0");

        var addonInstaller = new AddonInstaller(
            fileSystem, new RepoCache(fileSystem, gitClient, "cache"), new SyncEngine(fileSystem), new FakePatchTool(fileSystem));

        var workflow = new DependencyWorkflow(
            fileSystem, gitClient, factory, addonInstaller,
            new DotnetInstaller(fileSystem, new FakeDotnetCliRunner(), TestCredentials.None()))
        {
            SourceApproval = approval ?? DenyAllSourceApproval.Instance,
        };

        return new Fixture { Workflow = workflow, FileSystem = fileSystem };
    }

    private const string EmptyProject = """
        [project]
        godot = "4.3"
        """;

    private const string TrustingProject = """
        [project]
        godot = "4.3"

        [nuget]
        trusted_hosts = ["gitlab.example.com"]
        """;

    // -- the no-write guarantee ------------------------------------------------------------------

    [Fact]
    public async Task PreviewAddAsync_WritesNothing()
    {
        var fixture = Build(EmptyProject);
        var manifestBefore = fixture.FileSystem.ReadAllText(ManifestPath);

        await fixture.Workflow.PreviewAddAsync(ProjectDir, VbeamiUrl);

        // The manifest is byte-identical: no [addons.vbeami], and — the subtler one — no adopted
        // [nuget.sources.*] block either.
        Assert.Equal(manifestBefore, fixture.FileSystem.ReadAllText(ManifestPath));

        // No lockfile was conjured, and nothing landed in addons/. A preview that installs is just a
        // slow install with a misleading name.
        Assert.False(fixture.FileSystem.FileExists(LockfilePath));
        Assert.False(fixture.FileSystem.DirectoryExists($"{ProjectDir}/addons/vbeami"));
        Assert.False(fixture.FileSystem.DirectoryExists($"{ProjectDir}/addons/vbeami_core"));
    }

    [Fact]
    public async Task PreviewAddAsync_TrustedHost_StillWritesNothing()
    {
        // The dangerous case: adoption would succeed, so the code path that persists it is live.
        var fixture = Build(TrustingProject);
        var manifestBefore = fixture.FileSystem.ReadAllText(ManifestPath);

        var preview = await fixture.Workflow.PreviewAddAsync(ProjectDir, VbeamiUrl);

        Assert.Single(preview.SourcesToAdopt);
        Assert.Equal(manifestBefore, fixture.FileSystem.ReadAllText(ManifestPath));
        Assert.DoesNotContain(FeedUrl, fixture.FileSystem.ReadAllText(ManifestPath), StringComparison.Ordinal);
    }

    // -- what the dialog shows ------------------------------------------------------------------

    [Fact]
    public async Task PreviewAddAsync_ReportsTheTransitiveAddonsTheUserDidNotAskFor()
    {
        var preview = await Build(EmptyProject).Workflow.PreviewAddAsync(ProjectDir, VbeamiUrl);

        Assert.Equal("vbeami", preview.Name);
        Assert.Equal(["vbeami", "vbeami_core"], preview.Addons.Select(a => a.Name));

        // The whole reason for the dialog: vbeami_core is arriving because vbeami wants it, and the
        // user must be able to see that attribution rather than wonder where a folder came from.
        var core = preview.Addons.Single(a => a.Name == "vbeami_core");
        Assert.Equal(["vbeami"], core.RequiredBy);

        var requested = preview.Addons.Single(a => a.Name == "vbeami");
        Assert.Equal(["<project>"], requested.RequiredBy);
    }

    [Fact]
    public async Task PreviewAddAsync_NothingIsLockedYet_SoEveryAddonIsNewWithNoPreviousCommit()
    {
        var preview = await Build(EmptyProject).Workflow.PreviewAddAsync(ProjectDir, VbeamiUrl);

        Assert.All(preview.Addons, addon =>
        {
            Assert.True(addon.IsNew);
            Assert.Null(addon.PreviousResolved);
        });
    }

    [Fact]
    public async Task PreviewAddAsync_ReportsTheNuGetPackagesTheAddonBringsIn()
    {
        var preview = await Build(EmptyProject).Workflow.PreviewAddAsync(ProjectDir, VbeamiUrl);

        var package = Assert.Single(preview.Dotnet);
        Assert.Equal("VBeami.Core", package.Name);
        Assert.True(package.IsNew);

        // Attributed to the addon, not the project — the project never mentioned it (P1-5).
        Assert.Equal("vbeami", package.DeclaredByAddon);
    }

    // -- feed adoption ---------------------------------------------------------------------------

    [Fact]
    public async Task PreviewAddAsync_UntrustedFeed_NeedsApprovalAndNamesTheStakes()
    {
        var preview = await Build(EmptyProject).Workflow.PreviewAddAsync(ProjectDir, VbeamiUrl);

        Assert.Empty(preview.SourcesToAdopt);

        var pending = Assert.Single(preview.SourcesNeedingApproval);
        Assert.Equal(FeedUrl, pending.Url);
        Assert.Equal("vbeami", pending.DeclaredByAddon);
        Assert.False(pending.IsTrustedHost);

        // Both halves of the stakes: what the feed would own, and what actually depends on it.
        Assert.Equal(["VBeami.*"], pending.Packages);
        Assert.Equal(["VBeami.Core"], pending.PackageIds);
    }

    [Fact]
    public async Task PreviewAddAsync_TrustedHost_IsReportedAsAutomaticRatherThanAsAChoice()
    {
        var preview = await Build(TrustingProject).Workflow.PreviewAddAsync(ProjectDir, VbeamiUrl);

        Assert.Empty(preview.SourcesNeedingApproval);

        var adopting = Assert.Single(preview.SourcesToAdopt);
        Assert.Equal(FeedUrl, adopting.Url);

        // The flag a UI needs to say "already trusted" instead of offering a checkbox that does
        // nothing.
        Assert.True(adopting.IsTrustedHost);
    }

    [Fact]
    public async Task PreviewAddAsync_PreApprovedUrl_MovesTheFeedOutOfTheApprovalList()
    {
        // The second preview, after the user ticks the box: it must agree with what the real add is
        // about to do, or the dialog would still be showing an unanswered question.
        var preview = await Build(EmptyProject).Workflow.PreviewAddAsync(
            ProjectDir, VbeamiUrl, options: null, preApprovedSourceUrls: [FeedUrl]);

        Assert.Empty(preview.SourcesNeedingApproval);

        var adopting = Assert.Single(preview.SourcesToAdopt);
        Assert.Equal(FeedUrl, adopting.Url);

        // Pre-approval is per-run: it must not read as though the project now trusts the host.
        Assert.False(adopting.IsTrustedHost);
    }

    [Fact]
    public async Task PreviewAddAsync_PreApprovingSomeOtherUrl_LeavesThisFeedNeedingApproval()
    {
        var preview = await Build(EmptyProject).Workflow.PreviewAddAsync(
            ProjectDir, VbeamiUrl, options: null,
            preApprovedSourceUrls: ["https://gitlab.example.com/api/v4/groups/999/-/packages/nuget/index.json"]);

        Assert.Empty(preview.SourcesToAdopt);
        Assert.Single(preview.SourcesNeedingApproval);
    }

    // -- the pre-approval completing a real add --------------------------------------------------

    [Fact]
    public async Task AddAsync_WithPreApprovedFeed_AdoptsItWithoutAnyApprover()
    {
        // PreApprovedSourceApproval stands in for the console prompt the editor plugin cannot answer.
        var fixture = Build(EmptyProject, new PreApprovedSourceApproval([FeedUrl]));

        await fixture.Workflow.AddAsync(ProjectDir, VbeamiUrl);

        var manifest = fixture.FileSystem.ReadAllText(ManifestPath);
        Assert.Contains(FeedUrl, manifest, StringComparison.Ordinal);

        // Approving a feed must not silently widen trust for the next addon from the same host.
        Assert.DoesNotContain("trusted_hosts", manifest, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AddAsync_FeedNotPreApproved_FailsAndSaysHowToProceed()
    {
        var fixture = Build(EmptyProject, PreApprovedSourceApproval.For(["https://elsewhere.example.com/index.json"]));

        var exception = await Assert.ThrowsAsync<WorkflowException>(
            () => fixture.Workflow.AddAsync(ProjectDir, VbeamiUrl));

        Assert.Contains("VBeami.Core", exception.Message, StringComparison.Ordinal);
        Assert.Contains("trusted_hosts", exception.Message, StringComparison.Ordinal);
    }

    // -- refusals the preview must reproduce -----------------------------------------------------

    [Fact]
    public async Task PreviewAddAsync_AddonAlreadyDeclared_FailsTheSameWayAddDoes()
    {
        var fixture = Build($"""
            [project]
            godot = "4.3"

            [addons.vbeami]
            git = "{VbeamiUrl}"
            version = "~1"
            """);

        var exception = await Assert.ThrowsAsync<WorkflowException>(
            () => fixture.Workflow.PreviewAddAsync(ProjectDir, VbeamiUrl));

        // Same guidance as `add`: a preview that reported a resolvable graph here would be inviting
        // the user to press a button that then fails.
        Assert.Contains("already declared", exception.Message, StringComparison.Ordinal);
        Assert.Contains("dotquay update vbeami", exception.Message, StringComparison.Ordinal);
    }

    // -- the wire contract ------------------------------------------------------------------------

    [Fact]
    public async Task ToJson_Preview_SetsTheConvenienceFlagsAUiBranchesOn()
    {
        var pending = JsonMapper.ToJson(await Build(EmptyProject).Workflow.PreviewAddAsync(ProjectDir, VbeamiUrl));

        Assert.True(pending.NeedsApproval);
        Assert.False(pending.IsNoOp);

        // Precomputed so every consumer abbreviates a SHA the way the CLI prints it.
        Assert.All(pending.Addons, addon => Assert.Equal(7, addon.ShortSha.Length));

        var trusting = JsonMapper.ToJson(await Build(TrustingProject).Workflow.PreviewAddAsync(ProjectDir, VbeamiUrl));
        Assert.False(trusting.NeedsApproval);
    }

    [Fact]
    public async Task ToJson_Preview_SerializesUnderTheSnakeCaseKeysThePluginReads()
    {
        var payload = JsonMapper.ToJson(await Build(EmptyProject).Workflow.PreviewAddAsync(ProjectDir, VbeamiUrl));
        var json = DotquayJson.Write(JsonEnvelope<AddPreviewJson>.Success("add", payload));

        // Asserted as text because this is the contract the GDScript side parses by key name — a
        // rename that compiles fine on this side breaks the plugin silently.
        Assert.Contains("\"sources_needing_approval\"", json, StringComparison.Ordinal);
        Assert.Contains("\"sources_to_adopt\"", json, StringComparison.Ordinal);
        Assert.Contains("\"needs_approval\"", json, StringComparison.Ordinal);
        Assert.Contains("\"required_by\"", json, StringComparison.Ordinal);
        Assert.Contains("\"is_trusted_host\"", json, StringComparison.Ordinal);
        Assert.Contains("\"declared_by_addon\"", json, StringComparison.Ordinal);
        Assert.Contains("\"previous_resolved\"", json, StringComparison.Ordinal);
        Assert.Contains("\"is_new\"", json, StringComparison.Ordinal);
    }
}

/// <summary>
/// The whitelist that carries a preview's approvals into the add that follows it.
/// </summary>
public class PreApprovedSourceApprovalTests
{
    private const string FeedUrl = "https://gitlab.example.com/api/v4/groups/7/-/packages/nuget/index.json";

    private static SourceAdoptionRequest Request(string url) =>
        new("vbeami", new Core.Manifest.NuGetSourceSpec("vbeami", url), ["VBeami.Core"]);

    [Fact]
    public void Approve_ListedUrl_IsApproved() =>
        Assert.True(new PreApprovedSourceApproval([FeedUrl]).Approve(Request(FeedUrl)));

    [Fact]
    public void Approve_UnlistedUrl_IsRefused() =>
        Assert.False(new PreApprovedSourceApproval([FeedUrl]).Approve(Request("https://evil.example.com/index.json")));

    [Fact]
    public void Approve_CaseDiffersOnly_IsStillApproved()
    {
        // Matched the same way SourceAdopter decides a feed is already known. Two notions of "the
        // same feed" is how this stops being a boundary.
        var approval = new PreApprovedSourceApproval([FeedUrl.ToUpperInvariant()]);

        Assert.True(approval.Approve(Request(FeedUrl)));
    }

    [Fact]
    public void For_EmptyList_FallsBackToRefusingEverything()
    {
        Assert.Same(DenyAllSourceApproval.Instance, PreApprovedSourceApproval.For([]));
        Assert.Same(DenyAllSourceApproval.Instance, PreApprovedSourceApproval.For(null));
    }
}
