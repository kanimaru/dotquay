using Dotquay.Core.Doctor;
using Dotquay.Core.Lockfile;
using Dotquay.Core.Workflows;
using Dotquay.Tests.Support;
using Xunit;

namespace Dotquay.Tests.Workflows;

/// <summary>
/// Covers <c>dotquay doctor</c>'s check for a lockfile entry whose install folder disagrees with
/// the <c>install_as</c> the declaring manifest asks for.
/// </summary>
/// <remarks>
/// The drift is invisible by construction: <c>install</c> replays the lockfile and never re-reads
/// the manifest (SPEC.md §5), so editing <c>install_as</c> moves nothing until an <c>update</c>
/// re-resolves. Until then the manifest and disk describe two different projects, and every symptom
/// shows up somewhere else — a <c>preload("res://addons/…")</c> that no longer resolves, an addon
/// that looks installed twice, an orphan report naming the folder that is actually live.
/// </remarks>
public class DoctorInstallFolderDriftTests {
    private const string ProjectDirectory = "project";
    private const string AddonUrl = "https://example.com/mtoon-shader.git";
    private const string ParentUrl = "https://example.com/vrm.git";

    private static string Sha => new('a', 40);

    /// <param name="manifestInstallAs">The <c>install_as</c> line for <c>[addons.mtoon-shader]</c>,
    /// or null for none.</param>
    /// <param name="lockedFolder">The <c>folder</c> the lockfile records, or null for the
    /// default.</param>
    private static InMemoryFileSystem BuildProject(string? manifestInstallAs,
        string? lockedFolder) {
        var fileSystem = new InMemoryFileSystem();

        fileSystem.WriteAllText(fileSystem.CombinePath(ProjectDirectory, "dotquay.toml"), $"""
            [project]

            [addons.mtoon-shader]
            git = "{AddonUrl}"
            version = "~1.0"
            {(manifestInstallAs is null ? "" : $"install_as = \"{manifestInstallAs}\"")}
            """);

        fileSystem.WriteAllText(fileSystem.CombinePath(ProjectDirectory, "dotquay.lock"), $"""
            [[addon]]
            name = "mtoon-shader"
            git = "{AddonUrl}"
            resolved = "{Sha}"
            path = "."
            {(lockedFolder is null ? "" : $"folder = \"{lockedFolder}\"")}
            """);

        var installedFolder = lockedFolder ?? "mtoon-shader";
        fileSystem.WriteAllText(
            fileSystem.CombinePath(ProjectDirectory, "addons", installedFolder, "plugin.cfg"),
            "[plugin]");

        return fileSystem;
    }

    private static async Task<IReadOnlyList<DoctorIssue>> RunAsync(InMemoryFileSystem fileSystem) {
        var workflow = new DependencyWorkflow(fileSystem, new FakeGitClient(), cacheRoot: "cache");
        var result = await workflow.DoctorAsync(ProjectDirectory);

        return result.Issues.Where(i => i.Kind == DoctorIssueKind.InstallFolderDrift).ToList();
    }

    // -----------------------------------------------------------------------------------------
    // The project's own manifest
    // -----------------------------------------------------------------------------------------

    [Fact]
    public async Task DoctorAsync_InstallAsAddedSinceTheLockfile_IsReported() {
        var issues = await RunAsync(BuildProject(manifestInstallAs: "Godot-MToon-Shader",
            lockedFolder: null));

        // The exact case that motivated this check: the fix is written down, committed, and doing
        // nothing, because install replays the lockfile.
        var issue = Assert.Single(issues);
        Assert.Contains("addons/mtoon-shader", issue.Message);
        Assert.Contains("addons/Godot-MToon-Shader", issue.Message);
        Assert.Contains("dotquay update mtoon-shader", issue.Message);
    }

    [Fact]
    public async Task DoctorAsync_InstallAsRemovedSinceTheLockfile_IsReported() {
        var issues = await RunAsync(BuildProject(manifestInstallAs: null,
            lockedFolder: "Godot-MToon-Shader"));

        // The mirror image, and the one that reads as an orphaned folder if nothing explains it.
        var issue = Assert.Single(issues);
        Assert.Contains("addons/Godot-MToon-Shader", issue.Message);
    }

    [Fact]
    public async Task DoctorAsync_InstallAsChangedSinceTheLockfile_IsReported() {
        var issues = await RunAsync(BuildProject(manifestInstallAs: "NewFolder",
            lockedFolder: "OldFolder"));

        Assert.Single(issues);
    }

    [Fact]
    public async Task DoctorAsync_ManifestAndLockfileAgreeing_IsNotReported() {
        var issues = await RunAsync(BuildProject(manifestInstallAs: "Godot-MToon-Shader",
            lockedFolder: "Godot-MToon-Shader"));

        Assert.Empty(issues);
    }

    [Fact]
    public async Task DoctorAsync_NeitherDeclaringAFolder_IsNotReported() {
        var issues = await RunAsync(BuildProject(manifestInstallAs: null, lockedFolder: null));

        // The default. This must stay silent or doctor reports every addon in every project.
        Assert.Empty(issues);
    }

    [Fact]
    public async Task DoctorAsync_CaseCorrectedFolderWithNoInstallAs_IsNotReported() {
        var issues = await RunAsync(BuildProject(manifestInstallAs: null,
            lockedFolder: "MTOON-shader"));

        // AddonInstaller.CorrectFolderCase doing its job, not drift: the addon's own repository is
        // the authority on its folder's casing, and the manifest key is only a guess at it.
        // Reporting this would fire on every addon that correction has ever repaired, and the
        // advice would be to undo the fix.
        Assert.Empty(issues);
    }

    [Fact]
    public async Task DoctorAsync_CaseDifferingFromAnExplicitInstallAs_IsReported() {
        var issues = await RunAsync(BuildProject(manifestInstallAs: "Godot-MToon-Shader",
            lockedFolder: "godot-mtoon-shader"));

        // Where an install_as is stated, the casing is stated too — the correction never overrides
        // a stated choice, and to Godot these are two different res:// paths.
        Assert.Single(issues);
    }

    [Fact]
    public async Task DoctorAsync_AnOverride_WinsOverTheAddonEntry() {
        var fileSystem = BuildProject(manifestInstallAs: "FromAddonsTable",
            lockedFolder: "FromOverride");
        var manifestPath = fileSystem.CombinePath(ProjectDirectory, "dotquay.toml");
        fileSystem.WriteAllText(manifestPath, fileSystem.ReadAllText(manifestPath) + $"""

            [overrides.mtoon-shader]
            git = "{AddonUrl}"
            rev = "{Sha}"
            install_as = "FromOverride"
            """);

        // Precedence mirrors resolution rather than inventing its own (SPEC.md §8) — an override
        // wins, so the lockfile agreeing with it is health, not drift.
        Assert.Empty(await RunAsync(fileSystem));
    }

    // -----------------------------------------------------------------------------------------
    // A parent addon's package manifest
    // -----------------------------------------------------------------------------------------

    /// <summary>A project with one declared parent that pulls in <c>mtoon-shader</c>
    /// transitively.</summary>
    private static InMemoryFileSystem BuildTransitiveProject(string childInstallAs,
        string lockedChildFolder) {
        var fileSystem = new InMemoryFileSystem();

        fileSystem.WriteAllText(fileSystem.CombinePath(ProjectDirectory, "dotquay.toml"), $"""
            [project]

            [addons.vrm]
            git = "{ParentUrl}"
            version = "~1.0"
            """);

        fileSystem.WriteAllText(fileSystem.CombinePath(ProjectDirectory, "dotquay.lock"), $"""
            [[addon]]
            name = "vrm"
            git = "{ParentUrl}"
            resolved = "{Sha}"
            path = "addons/vrm"
            dependencies = ["mtoon-shader"]

            [[addon]]
            name = "mtoon-shader"
            git = "{AddonUrl}"
            resolved = "{Sha}"
            path = "."
            folder = "{lockedChildFolder}"
            """);

        fileSystem.WriteAllText(
            fileSystem.CombinePath(ProjectDirectory, "addons", "vrm", "dotquay.toml"), $"""
            [package]
            name = "vrm"

            [addons.mtoon-shader]
            git = "{AddonUrl}"
            version = "~1.0"
            install_as = "{childInstallAs}"
            """);

        fileSystem.WriteAllText(
            fileSystem.CombinePath(ProjectDirectory, "addons", lockedChildFolder, "plugin.cfg"),
            "[plugin]");

        return fileSystem;
    }

    [Fact]
    public async Task DoctorAsync_ParentDeclaringADifferentFolderThanTheLockfile_IsReported() {
        var issues = await RunAsync(BuildTransitiveProject(childInstallAs: "Godot-MToon-Shader",
            lockedChildFolder: "mtoon-shader"));

        // The quietest version of this bug: a parent that starts declaring install_as for a shared
        // dependency changes nothing about that dependency's commit, so a same-commit check never
        // notices it needs to move.
        var issue = Assert.Single(issues);
        Assert.Contains("addons/vrm/dotquay.toml", issue.Message);
    }

    [Fact]
    public async Task DoctorAsync_ParentDeclarationTheLockfileHonours_IsNotReported() {
        var issues = await RunAsync(BuildTransitiveProject(childInstallAs: "Godot-MToon-Shader",
            lockedChildFolder: "Godot-MToon-Shader"));

        Assert.Empty(issues);
    }

    [Fact]
    public async Task DoctorAsync_ParentsDisagreeingWithEachOther_IsNotReported() {
        var fileSystem = BuildTransitiveProject(childInstallAs: "Godot-MToon-Shader",
            lockedChildFolder: "mtoon-shader");

        var lockPath = fileSystem.CombinePath(ProjectDirectory, "dotquay.lock");
        fileSystem.WriteAllText(lockPath, fileSystem.ReadAllText(lockPath) + $"""

            [[addon]]
            name = "other"
            git = "https://example.com/other.git"
            resolved = "{Sha}"
            path = "addons/other"
            dependencies = ["mtoon-shader"]
            """);

        fileSystem.WriteAllText(
            fileSystem.CombinePath(ProjectDirectory, "addons", "other", "dotquay.toml"), $"""
            [package]
            name = "other"

            [addons.mtoon-shader]
            git = "{AddonUrl}"
            version = "~1.0"
            install_as = "SomethingElse"
            """);

        // Two parents asking for different folders is a resolution conflict with its own reporting
        // (GraphConflictKind.DisagreeingInstallFolders). Doctor picking a winner here would
        // contradict whatever resolution decides, so it stays quiet.
        Assert.Empty(await RunAsync(fileSystem));
    }

    [Fact]
    public async Task DoctorAsync_ProjectManifest_WinsOverAParentDeclaration() {
        var fileSystem = BuildTransitiveProject(childInstallAs: "Godot-MToon-Shader",
            lockedChildFolder: "ProjectChoice");

        var manifestPath = fileSystem.CombinePath(ProjectDirectory, "dotquay.toml");
        fileSystem.WriteAllText(manifestPath, fileSystem.ReadAllText(manifestPath) + $"""

            [addons.mtoon-shader]
            git = "{AddonUrl}"
            version = "~1.0"
            install_as = "ProjectChoice"
            """);

        // The consuming project's own declaration is never second-guessed by an addon's.
        Assert.Empty(await RunAsync(fileSystem));
    }

    [Fact]
    public async Task DoctorAsync_ATransitiveAddonNobodyDeclaresAFolderFor_IsNotReported() {
        var fileSystem = new InMemoryFileSystem();

        fileSystem.WriteAllText(fileSystem.CombinePath(ProjectDirectory, "dotquay.toml"), $"""
            [project]

            [addons.vrm]
            git = "{ParentUrl}"
            version = "~1.0"
            """);

        fileSystem.WriteAllText(fileSystem.CombinePath(ProjectDirectory, "dotquay.lock"), $"""
            [[addon]]
            name = "vrm"
            git = "{ParentUrl}"
            resolved = "{Sha}"
            path = "addons/vrm"
            dependencies = ["mtoon-shader"]

            [[addon]]
            name = "mtoon-shader"
            git = "{AddonUrl}"
            resolved = "{Sha}"
            path = "."
            """);

        // A transitive addon appears in no manifest the project owns (SPEC.md §8), and one shipping
        // no package manifest at all is the common case. With nothing declaring a folder there is
        // nothing to disagree with.
        Assert.Empty(await RunAsync(fileSystem));
    }

    // -----------------------------------------------------------------------------------------
    // The rule itself
    // -----------------------------------------------------------------------------------------

    private static LockedAddon Locked(string? folder) => new() {
        Name = "mtoon-shader",
        Git = AddonUrl,
        Resolved = Sha,
        Path = ".",
        Folder = folder,
    };

    [Fact]
    public void Check_ReportsBothFoldersAndTheSource() {
        var drift = InstallFolderDriftChecker.Check(
            Locked("here"),
            new InstallFolderDriftChecker.FolderDeclaration("there", IsExplicit: true,
                "somewhere"));

        Assert.NotNull(drift);
        Assert.Equal("mtoon-shader", drift.Name);
        Assert.Equal("here", drift.Actual);
        Assert.Equal("there", drift.Expected);
        Assert.Equal("somewhere", drift.Source);
    }

    [Fact]
    public void Check_ALockfileWithNoFolder_ComparesAgainstTheName() {
        // Every lockfile written before install_as existed omits the field, and must still be
        // understood as "installed under its own name" rather than as unknown.
        Assert.Null(InstallFolderDriftChecker.Check(
            Locked(folder: null),
            new InstallFolderDriftChecker.FolderDeclaration("mtoon-shader", IsExplicit: false,
                "somewhere")));

        Assert.NotNull(InstallFolderDriftChecker.Check(
            Locked(folder: null),
            new InstallFolderDriftChecker.FolderDeclaration("elsewhere", IsExplicit: true,
                "somewhere")));
    }
}
