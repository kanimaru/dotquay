using Dotquay.Core.Manifest;
using Dotquay.Core.Workflows;
using Dotquay.Tests.Support;
using Xunit;

namespace Dotquay.Tests.Workflows;

/// <summary>
/// Covers <see cref="OverrideWorkflow"/> — the manifest half of <c>dotquay override</c> (v0.5).
/// </summary>
/// <remarks>
/// The interesting behaviour isn't writing TOML; it's what the workflow refuses to make the caller
/// restate. A conflict picker in the editor knows an addon's name and the version the user clicked,
/// and nothing else — so inheriting the git URL from the manifest or lockfile is what makes the
/// one-click fix possible at all, and getting it wrong would silently repoint an addon at another
/// repository.
/// </remarks>
public class OverrideWorkflowTests {
    private const string ProjectDirectory = "project";
    private const string CoreUrl = "https://example.com/core.git";
    private const string ForkUrl = "https://example.com/core-fork.git";

    private static (OverrideWorkflow Workflow, InMemoryFileSystem FileSystem) Build(string manifest, string? lockfile = null) {
        var fileSystem = new InMemoryFileSystem();
        fileSystem.WriteAllText(fileSystem.CombinePath(ProjectDirectory, "dotquay.toml"), manifest);

        if (lockfile is not null) {
            fileSystem.WriteAllText(fileSystem.CombinePath(ProjectDirectory, "dotquay.lock"), lockfile);
        }

        return (new OverrideWorkflow(fileSystem), fileSystem);
    }

    private static string ReadManifest(InMemoryFileSystem fileSystem) =>
        fileSystem.ReadAllText(fileSystem.CombinePath(ProjectDirectory, "dotquay.toml"));

    private const string ManifestWithCore = $"""
        [project]
        godot = "4.7"

        [addons.core]
        git = "{CoreUrl}"
        version = "~1.2"
        """;

    [Fact]
    public void Set_WritesAnOverridesEntry() {
        var (workflow, fileSystem) = Build(ManifestWithCore);

        var result = workflow.Set(ProjectDirectory, "core", AddonSourceKind.Version, "~1.4");

        Assert.Equal(OverrideChange.Added, result.Change);
        Assert.Contains("[overrides.core]", ReadManifest(fileSystem));
        Assert.Contains("version = \"~1.4\"", ReadManifest(fileSystem));
    }

    [Fact]
    public void Set_LeavesTheOriginalAddonsEntryAlone() {
        var (workflow, fileSystem) = Build(ManifestWithCore);

        workflow.Set(ProjectDirectory, "core", AddonSourceKind.Version, "~1.4");

        // An override records "force this one", not "I changed my mind about what I asked for" —
        // clearing it later must restore the original constraint, which requires it to still be there.
        var manifest = ReadManifest(fileSystem);
        Assert.Contains("[addons.core]", manifest);
        Assert.Contains("version = \"~1.2\"", manifest);
    }

    [Fact]
    public void Set_InheritsTheGitUrlFromTheManifest() {
        var (workflow, _) = Build(ManifestWithCore);

        var result = workflow.Set(ProjectDirectory, "core", AddonSourceKind.Version, "~1.4");

        Assert.Equal(CoreUrl, result.Spec!.Git);
    }

    [Fact]
    public void Set_InheritsTheGitUrlFromTheLockfileForATransitiveAddon() {
        // A transitive addon has no [addons.*] entry of its own, so the lockfile is the only local
        // record of which repository the name means — and it's exactly the case a conflict picker
        // hits, since conflicts usually involve a shared dependency nobody declared directly.
        var lockfile = $"""
            [[addon]]
            name = "shared"
            git = "{CoreUrl}"
            resolved = "{new string('a', 40)}"
            path = "addons/shared"
            """;

        var (workflow, _) = Build(ManifestWithCore, lockfile);

        var result = workflow.Set(ProjectDirectory, "shared", AddonSourceKind.Version, "~2.0");

        Assert.Equal(CoreUrl, result.Spec!.Git);
    }

    [Fact]
    public void Set_ExplicitGitWins() {
        var (workflow, _) = Build(ManifestWithCore);

        // The one conflict kind where the repository itself is the disagreement (SPEC.md §8) needs
        // the caller to be able to say which side won.
        var result = workflow.Set(ProjectDirectory, "core", AddonSourceKind.Version, "~1.4", git: ForkUrl);

        Assert.Equal(ForkUrl, result.Spec!.Git);
    }

    [Fact]
    public void Set_UnknownNameWithNoGitUrl_Throws() {
        var (workflow, _) = Build(ManifestWithCore);

        var exception = Assert.Throws<WorkflowException>(
            () => workflow.Set(ProjectDirectory, "never-heard-of-it", AddonSourceKind.Version, "~1.0"));

        Assert.Contains("can't be inferred", exception.Message);
    }

    [Fact]
    public void Set_BranchAndRevKinds_WriteTheMatchingField() {
        var (workflow, fileSystem) = Build(ManifestWithCore);

        workflow.Set(ProjectDirectory, "core", AddonSourceKind.Branch, "godot4");
        Assert.Contains("branch = \"godot4\"", ReadManifest(fileSystem));

        workflow.Set(ProjectDirectory, "core", AddonSourceKind.Rev, "9f2c1ab");
        var manifest = ReadManifest(fileSystem);
        Assert.Contains("rev = \"9f2c1ab\"", manifest);
        // Exactly one of version/branch/rev may be set (SPEC.md §7.1) — replacing has to clear the
        // previous kind, not accumulate all three.
        Assert.DoesNotContain("branch = \"godot4\"", manifest);
    }

    [Fact]
    public void Set_TheSameOverrideTwice_ReportsUnchanged() {
        var (workflow, _) = Build(ManifestWithCore);
        workflow.Set(ProjectDirectory, "core", AddonSourceKind.Version, "~1.4");

        var result = workflow.Set(ProjectDirectory, "core", AddonSourceKind.Version, "~1.4");

        // The CLI skips the resync (and therefore a fetch and reinstall) on Unchanged — in an editor
        // that's the difference between a click doing nothing visible and a click freezing the panel.
        Assert.Equal(OverrideChange.Unchanged, result.Change);
    }

    [Fact]
    public void Set_ADifferentValue_ReportsReplaced() {
        var (workflow, _) = Build(ManifestWithCore);
        workflow.Set(ProjectDirectory, "core", AddonSourceKind.Version, "~1.4");

        var result = workflow.Set(ProjectDirectory, "core", AddonSourceKind.Version, "~1.6");

        Assert.Equal(OverrideChange.Replaced, result.Change);
    }

    [Fact]
    public void Set_EmptyValue_Throws() {
        var (workflow, _) = Build(ManifestWithCore);

        Assert.Throws<WorkflowException>(() => workflow.Set(ProjectDirectory, "core", AddonSourceKind.Version, "  "));
    }

    [Fact]
    public void Set_DoesNotWriteARedundantDefaultPath() {
        var (workflow, fileSystem) = Build(ManifestWithCore);

        workflow.Set(ProjectDirectory, "core", AddonSourceKind.Version, "~1.4");

        // AddonSpec.EffectivePath already derives "addons/<name>"; writing it out would add a line
        // the user then has to maintain by hand if the addon ever moves inside its repo.
        Assert.DoesNotContain("path =", ReadManifest(fileSystem));
    }

    [Fact]
    public void Set_InheritsAnExplicitPath() {
        var manifest = $"""
            [addons.core]
            git = "{CoreUrl}"
            version = "~1.2"
            path = "plugins/core"
            """;

        var (workflow, fileSystem) = Build(manifest);

        workflow.Set(ProjectDirectory, "core", AddonSourceKind.Version, "~1.4");

        // An override that silently reverted to the default subtree would install the wrong folder,
        // which looks like the addon vanished rather than like a path problem.
        Assert.Contains("path = \"plugins/core\"", ReadManifest(fileSystem));
    }

    [Fact]
    public void Clear_RemovesTheEntry() {
        var (workflow, fileSystem) = Build(ManifestWithCore);
        workflow.Set(ProjectDirectory, "core", AddonSourceKind.Version, "~1.4");

        var result = workflow.Clear(ProjectDirectory, "core");

        Assert.Equal(OverrideChange.Removed, result.Change);
        Assert.DoesNotContain("[overrides.core]", ReadManifest(fileSystem));
        Assert.Contains("[addons.core]", ReadManifest(fileSystem));
    }

    [Fact]
    public void Clear_WhenThereIsNoOverride_ReportsUnchanged() {
        var (workflow, _) = Build(ManifestWithCore);

        var result = workflow.Clear(ProjectDirectory, "core");

        Assert.Equal(OverrideChange.Unchanged, result.Change);
    }

    [Fact]
    public void List_ReturnsWhatWasWritten() {
        var (workflow, _) = Build(ManifestWithCore);
        workflow.Set(ProjectDirectory, "core", AddonSourceKind.Version, "~1.4");

        var overrides = workflow.List(ProjectDirectory);

        var entry = Assert.Single(overrides);
        Assert.Equal("core", entry.Key);
        Assert.Equal("~1.4", entry.Value.Version);
    }

    [Fact]
    public void Set_WithNoManifest_Throws() {
        var fileSystem = new InMemoryFileSystem();
        var workflow = new OverrideWorkflow(fileSystem);

        var exception = Assert.Throws<WorkflowException>(
            () => workflow.Set(ProjectDirectory, "core", AddonSourceKind.Version, "~1.4"));

        Assert.Contains("Run 'dotquay init' first", exception.Message);
    }
}
