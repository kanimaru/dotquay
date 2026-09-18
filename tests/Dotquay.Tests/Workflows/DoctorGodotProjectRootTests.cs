using Dotquay.Core.Workflows;
using Dotquay.Tests.Support;
using Xunit;

namespace Dotquay.Tests.Workflows;

/// <summary>
/// Covers <c>dotquay doctor</c>'s check for an installed addon folder that is a Godot project root
/// rather than an addon (<c>addon_folder_is_godot_project</c>).
/// </summary>
/// <remarks>
/// The offline half of <see cref="Resolve.GraphWarningTests"/>' project-root warning. It matters on
/// its own because the person hitting this has usually already run <c>install</c> a second time and
/// watched it change nothing — the lockfile replays the same <c>path = "."</c> — and the editor's
/// "plugin missing" gives no hint that the path lives in another addon's package manifest. The check
/// needs no network: the installed folder <em>is</em> the extracted subtree.
/// </remarks>
public class DoctorGodotProjectRootTests
{
    private const string ProjectDirectory = "project";
    private const string VbeamiUrl = "https://example.com/vbeami.git";
    private const string VrmUrl = "git@example.com:vrm-addon.git";

    private static string VrmSha => new('c', 40);
    private static string VbeamiSha => new('b', 40);

    /// <summary>The BotV2 shape: vrm is transitive via vbeami, and its installed folder holds the
    /// whole vrm-addon repository.</summary>
    private static InMemoryFileSystem BuildTransitiveProject(bool vrmIsProjectRoot = true, string? overrideBlock = null)
    {
        var fileSystem = new InMemoryFileSystem();

        fileSystem.WriteAllText(fileSystem.CombinePath(ProjectDirectory, "dotquay.toml"), $"""
            [project]

            [addons.vbeami]
            git = "{VbeamiUrl}"
            version = "~0.7"
            path = "godot/addons/vbeami"
            {overrideBlock ?? ""}
            """);

        fileSystem.WriteAllText(fileSystem.CombinePath(ProjectDirectory, "dotquay.lock"), $"""
            [[addon]]
            name = "vbeami"
            git = "{VbeamiUrl}"
            resolved = "{VbeamiSha}"
            version = "v0.7.22"
            path = "godot/addons/vbeami"
            dependencies = ["vrm"]

            [[addon]]
            name = "vrm"
            git = "{VrmUrl}"
            resolved = "{VrmSha}"
            version = "0.0.0-20260917-{VrmSha[..7]}"
            path = "."
            """);

        fileSystem.WriteAllText(fileSystem.CombinePath(ProjectDirectory, "addons", "vbeami", "plugin.cfg"), "[plugin]");

        if (vrmIsProjectRoot)
        {
            WriteVrmAsProjectRoot(fileSystem, fileSystem.CombinePath(ProjectDirectory, "addons", "vrm"));
        }
        else
        {
            fileSystem.WriteAllText(fileSystem.CombinePath(ProjectDirectory, "addons", "vrm", "plugin.cfg"), "[plugin]");
        }

        return fileSystem;
    }

    /// <summary>What vrm-addon's repository root actually looks like after its restructure: a
    /// playable project with its own [project] manifest, and the plugin two levels down.</summary>
    private static void WriteVrmAsProjectRoot(InMemoryFileSystem fileSystem, string folder)
    {
        fileSystem.WriteAllText(fileSystem.CombinePath(folder, "project.godot"), "config_version=5");
        fileSystem.WriteAllText(fileSystem.CombinePath(folder, "dotquay.toml"), """
            [project]
            godot = "4.3"
            """);
        fileSystem.WriteAllText(fileSystem.CombinePath(folder, "addons", "vrm", "plugin.cfg"), "[plugin]");
        fileSystem.WriteAllText(fileSystem.CombinePath(folder, "addons", "gut", "plugin.cfg"), "[plugin]");
    }

    private static async Task<DoctorResult> RunAsync(InMemoryFileSystem fileSystem)
    {
        var workflow = new DependencyWorkflow(fileSystem, new FakeGitClient(), cacheRoot: "cache");
        return await workflow.DoctorAsync(ProjectDirectory);
    }

    private static IReadOnlyList<DoctorIssue> Of(DoctorResult result, DoctorIssueKind kind) =>
        result.Issues.Where(i => i.Kind == kind).ToList();

    [Fact]
    public async Task DoctorAsync_TransitiveAddonInstalledAsAProjectRoot_IsReported()
    {
        var result = await RunAsync(BuildTransitiveProject());

        var issue = Assert.Single(Of(result, DoctorIssueKind.AddonFolderIsGodotProject));
        Assert.Contains("'vrm'", issue.Message);
        Assert.Contains("addons/vrm/", issue.Message);
        Assert.Contains("project.godot", issue.Message);
    }

    [Fact]
    public async Task DoctorAsync_TransitiveAddon_NamesTheParentsPackageManifestAsThePlaceToFix()
    {
        var result = await RunAsync(BuildTransitiveProject());

        // The whole reason for the check: the path that is wrong is in vbeami's manifest, and
        // nothing about "plugin missing" in the editor says so.
        var issue = Assert.Single(Of(result, DoctorIssueKind.AddonFolderIsGodotProject));
        Assert.Contains("'vbeami''s package manifest", issue.Message);
        Assert.Contains("addons/vbeami/dotquay.toml", issue.Message);
        Assert.Contains("path = \"addons/vrm\"", issue.Message);
    }

    [Fact]
    public async Task DoctorAsync_TransitiveAddon_OffersAnOverridePinnedToTheLockedCommit()
    {
        var result = await RunAsync(BuildTransitiveProject());

        // doctor cannot know which branch or range vbeami declared vrm with, so the one override it
        // can quote without guessing pins exactly what is installed.
        var issue = Assert.Single(Of(result, DoctorIssueKind.AddonFolderIsGodotProject));
        Assert.Contains($"dotquay override vrm --rev {VrmSha} --path addons/vrm", issue.Message);
    }

    [Fact]
    public async Task DoctorAsync_ProjectRootFolder_DoesNotAlsoReportItsProjectManifestAsUnreadable()
    {
        var result = await RunAsync(BuildTransitiveProject());

        // The project's own [project] dotquay.toml has no [package] header, and before this check
        // that was the only thing doctor said — sending people off to add a header to a file that
        // should not be under addons/ at all. One explanation, not two contradictory ones.
        Assert.Empty(Of(result, DoctorIssueKind.UnreadablePackageManifest));
    }

    [Fact]
    public async Task DoctorAsync_AddonInstalledCorrectly_IsNotReported()
    {
        var result = await RunAsync(BuildTransitiveProject(vrmIsProjectRoot: false));

        Assert.Empty(Of(result, DoctorIssueKind.AddonFolderIsGodotProject));
    }

    [Fact]
    public async Task DoctorAsync_OverriddenAddon_NamesTheOverrideAsThePlaceToFix()
    {
        var fileSystem = BuildTransitiveProject(overrideBlock: $"""

            [overrides.vrm]
            git = "{VrmUrl}"
            branch = "main"
            path = "."
            """);

        var result = await RunAsync(fileSystem);

        // An override wins resolution unconditionally (SPEC.md §8), so it is also where the wrong
        // path is coming from now — pointing at vbeami's manifest would be pointing at a file that
        // no longer decides anything.
        var issue = Assert.Single(Of(result, DoctorIssueKind.AddonFolderIsGodotProject));
        Assert.Contains("[overrides.vrm]", issue.Message);
        Assert.DoesNotContain("'vbeami''s package manifest", issue.Message);
    }

    [Fact]
    public async Task DoctorAsync_RootAddon_NamesTheProjectsOwnDeclaration()
    {
        var fileSystem = new InMemoryFileSystem();
        fileSystem.WriteAllText(fileSystem.CombinePath(ProjectDirectory, "dotquay.toml"), $"""
            [project]

            [addons.vrm]
            git = "{VrmUrl}"
            branch = "main"
            path = "."
            """);
        fileSystem.WriteAllText(fileSystem.CombinePath(ProjectDirectory, "dotquay.lock"), $"""
            [[addon]]
            name = "vrm"
            git = "{VrmUrl}"
            resolved = "{VrmSha}"
            path = "."
            """);
        WriteVrmAsProjectRoot(fileSystem, fileSystem.CombinePath(ProjectDirectory, "addons", "vrm"));

        var result = await RunAsync(fileSystem);

        var issue = Assert.Single(Of(result, DoctorIssueKind.AddonFolderIsGodotProject));
        Assert.Contains("[addons.vrm]", issue.Message);
    }

    [Fact]
    public async Task DoctorAsync_InstallAsFolder_IsCheckedWhereTheAddonActuallyLives()
    {
        var fileSystem = new InMemoryFileSystem();
        fileSystem.WriteAllText(fileSystem.CombinePath(ProjectDirectory, "dotquay.toml"), $"""
            [project]

            [addons.vrm]
            git = "{VrmUrl}"
            branch = "main"
            path = "."
            install_as = "godot_vrm"
            """);
        fileSystem.WriteAllText(fileSystem.CombinePath(ProjectDirectory, "dotquay.lock"), $"""
            [[addon]]
            name = "vrm"
            git = "{VrmUrl}"
            resolved = "{VrmSha}"
            path = "."
            folder = "godot_vrm"
            """);
        WriteVrmAsProjectRoot(fileSystem, fileSystem.CombinePath(ProjectDirectory, "addons", "godot_vrm"));

        var result = await RunAsync(fileSystem);

        // Through InstallPaths, like every other check — a hand-written addons/<name> would look at
        // an empty folder and report nothing.
        var issue = Assert.Single(Of(result, DoctorIssueKind.AddonFolderIsGodotProject));
        Assert.Contains("addons/godot_vrm/", issue.Message);
        Assert.Contains("path = \"addons/vrm\"", issue.Message);
    }

    [Fact]
    public async Task DoctorAsync_NoManifestAtAll_StillReportsFromTheLockfileAndDisk()
    {
        var fileSystem = BuildTransitiveProject();
        fileSystem.DeleteFile(fileSystem.CombinePath(ProjectDirectory, "dotquay.toml"));

        var result = await RunAsync(fileSystem);

        // doctor keeps working without a manifest (it only loses the checks that need one); this
        // check needs the lockfile and the folder, both of which are there.
        var issue = Assert.Single(Of(result, DoctorIssueKind.AddonFolderIsGodotProject));
        Assert.Contains("'vbeami''s package manifest", issue.Message);
    }
}
