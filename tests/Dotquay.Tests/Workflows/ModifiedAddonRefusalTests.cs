using Dotquay.Core.Abstractions;
using Dotquay.Core.Workflows;
using Dotquay.Tests.Support;
using Xunit;

namespace Dotquay.Tests.Workflows;

/// <summary>
/// Covers what a P0-6 refusal tells the user: which files are modified, and — for
/// <c>dotquay link</c> — that the refusal arrives before any link is declared.
/// </summary>
public class ModifiedAddonRefusalTests {
    private const string RepoUrl = "https://example.com/dialogue_manager.git";
    private const string ProjectDir = "project";
    private const string AddonDir = $"{ProjectDir}/addons/dialogue_manager";
    private const string LinkPath = "elsewhere/dialogue_manager";

    private static async Task<(DependencyWorkflow Workflow, InMemoryFileSystem FileSystem)> InstalledAsync() {
        var fileSystem = new InMemoryFileSystem();
        fileSystem.WriteAllText($"{ProjectDir}/dotquay.toml", "[project]\n");

        var gitClient = new FakeGitClient { FileSystemForFetchedContent = fileSystem };
        gitClient.FetchedFilesByUrl[RepoUrl] = new Dictionary<string, string> {
            ["addons/dialogue_manager/plugin.cfg"] = "cfg",
            ["addons/dialogue_manager/x.gd"] = "extends Node\n",
        };
        gitClient.RemoteRefsByUrl[RepoUrl] = [new GitRef("v1.0.0", new string('a', 40), GitRefKind.Tag)];

        var workflow = new DependencyWorkflow(fileSystem, gitClient, "cache");
        await workflow.AddAsync(ProjectDir, RepoUrl);
        return (workflow, fileSystem);
    }

    [Fact]
    public async Task InstallAsync_OverHandEdits_NamesTheChangedFiles() {
        var (workflow, fileSystem) = await InstalledAsync();
        fileSystem.WriteAllText($"{AddonDir}/plugin.cfg", "cfg, but edited");
        fileSystem.WriteAllText($"{AddonDir}/notes.gd", "hours of work");

        var exception = await Assert.ThrowsAsync<WorkflowException>(
            () => workflow.InstallAsync(ProjectDir, force: false));

        Assert.Contains("local modifications", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Changed locally (2 files):", exception.Message, StringComparison.Ordinal);
        Assert.Contains("added    notes.gd", exception.Message, StringComparison.Ordinal);
        Assert.Contains("changed  plugin.cfg", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("more.", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>Past five files the list stops and says where the rest is.</summary>
    [Fact]
    public async Task InstallAsync_OverManyHandEdits_ListsFiveAndPointsAtExplain() {
        var (workflow, fileSystem) = await InstalledAsync();
        const int Edited = 8;
        for (var i = 0; i < Edited; i++) {
            fileSystem.WriteAllText($"{AddonDir}/extra_{i}.gd", "new");
        }

        var exception = await Assert.ThrowsAsync<WorkflowException>(
            () => workflow.InstallAsync(ProjectDir, force: false));

        Assert.Contains($"Changed locally ({Edited} files):", exception.Message, StringComparison.Ordinal);
        Assert.Contains("extra_4.gd", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("extra_5.gd", exception.Message, StringComparison.Ordinal);
        Assert.Contains("... and 3 more", exception.Message, StringComparison.Ordinal);
        Assert.Contains("dotquay status --explain", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The field report: an editor trimmed trailing whitespace and every later update was blocked.
    /// The refusal must say so, and say that forcing is safe.
    /// </summary>
    [Fact]
    public async Task InstallAsync_OverWhitespaceOnlyEdits_SaysNothingRealChangedAndForceIsSafe() {
        var (workflow, fileSystem) = await InstalledAsync();
        fileSystem.WriteAllText($"{AddonDir}/x.gd", "extends Node   \n\n");

        var exception = await Assert.ThrowsAsync<WorkflowException>(
            () => workflow.InstallAsync(ProjectDir, force: false));

        Assert.Contains("changed  x.gd  (whitespace only)", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Only 1 whitespace-only change", exception.Message, StringComparison.Ordinal);
        Assert.Contains("'dotquay update --force' is safe", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InstallAsync_OverRealAndWhitespaceEdits_WarnsAboutTheRealOnes() {
        var (workflow, fileSystem) = await InstalledAsync();
        fileSystem.WriteAllText($"{AddonDir}/x.gd", "extends Node   \n");
        fileSystem.WriteAllText($"{AddonDir}/plugin.cfg", "cfg, but edited");

        var exception = await Assert.ThrowsAsync<WorkflowException>(
            () => workflow.InstallAsync(ProjectDir, force: false));

        Assert.Contains("1 real content change: --force would discard it", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("is safe", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Editor output is not hashed, so it can never be why an install refused — listing it would
    /// point at files that are not the problem.
    /// </summary>
    [Fact]
    public async Task InstallAsync_OverHandEdits_LeavesEditorArtifactsOutOfTheList() {
        var (workflow, fileSystem) = await InstalledAsync();
        fileSystem.WriteAllText($"{AddonDir}/x.gd.uid", "uid://bxwpwbdv4fm02");
        fileSystem.WriteAllText($"{AddonDir}/plugin.cfg", "cfg, but edited");

        var exception = await Assert.ThrowsAsync<WorkflowException>(
            () => workflow.InstallAsync(ProjectDir, force: false));

        Assert.Contains("Changed locally (1 file):", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("x.gd.uid", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The field report: <c>dotquay link</c> over a modified addon refused, but only after it had
    /// written the link declaration — so every later <c>update</c> treated the addon as linked. The
    /// check now runs first, so the caller can refuse before declaring anything.
    /// </summary>
    [Fact]
    public async Task EnsureAddonLinkableAsync_OverHandEdits_RefusesAndNamesTheFiles() {
        var (workflow, fileSystem) = await InstalledAsync();
        fileSystem.WriteAllText($"{AddonDir}/plugin.cfg", "cfg, but edited");

        var exception = await Assert.ThrowsAsync<WorkflowException>(
            () => workflow.EnsureAddonLinkableAsync(ProjectDir, "dialogue_manager", LinkPath, force: false));

        Assert.Contains("is being replaced by a link", exception.Message, StringComparison.Ordinal);
        Assert.Contains("changed  plugin.cfg", exception.Message, StringComparison.Ordinal);
        Assert.False(fileSystem.FileExists($"{ProjectDir}/dotquay.local.toml"));
    }

    [Fact]
    public async Task EnsureAddonLinkableAsync_CleanAddon_Allows() {
        var (workflow, _) = await InstalledAsync();

        await workflow.EnsureAddonLinkableAsync(ProjectDir, "dialogue_manager", LinkPath, force: false);
    }

    [Fact]
    public async Task EnsureAddonLinkableAsync_UnderForce_Allows() {
        var (workflow, fileSystem) = await InstalledAsync();
        fileSystem.WriteAllText($"{AddonDir}/plugin.cfg", "cfg, but edited");

        await workflow.EnsureAddonLinkableAsync(ProjectDir, "dialogue_manager", LinkPath, force: true);
    }

    [Fact]
    public async Task EnsureAddonLinkableAsync_NeverInstalled_Allows() {
        var (workflow, _) = await InstalledAsync();

        await workflow.EnsureAddonLinkableAsync(ProjectDir, "not_installed", LinkPath, force: false);
    }
}
