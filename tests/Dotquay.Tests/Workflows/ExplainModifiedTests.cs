using Dotquay.Core.Abstractions;
using Dotquay.Core.Json;
using Dotquay.Core.Lockfile;
using Dotquay.Core.Sync;
using Dotquay.Core.Workflows;
using Dotquay.Tests.Support;
using Xunit;

namespace Dotquay.Tests.Workflows;

/// <summary>
/// Covers <c>dotquay status --explain</c> end to end
/// (docs/DESIGN-v0.12-MODIFIED-REASON.md): an addon reads as modified, and the user needs to know
/// whether <c>--force</c> would cost them anything.
/// </summary>
/// <remarks>
/// The invariant running through every test here is that explaining never changes a verdict. The
/// <see cref="AddonStatusKind"/> values these assertions read come from the same
/// <see cref="AddonStatusEvaluator"/> path plain <c>status</c> uses; only the attached
/// <see cref="AddonExplanation"/> is new.
/// </remarks>
public class ExplainModifiedTests {
    private const string RepoUrl = "https://example.com/dialogue_manager.git";
    private const string ProjectDir = "project";
    private const string AddonDir = $"{ProjectDir}/addons/dialogue_manager";

    private const string SceneWithUid =
        """[ext_resource type="Script" uid="uid://bxwpwbdv4fm02" path="res://addons/dialogue_manager/x.gd"]""";

    private const string ScriptLf = "extends Node\nfunc _ready():\n\tpass\n";

    private static (DependencyWorkflow Workflow, InMemoryFileSystem FileSystem, FakeGitClient GitClient) Build() {
        var fileSystem = new InMemoryFileSystem();
        fileSystem.WriteAllText($"{ProjectDir}/dotquay.toml", "[project]\ngodot = \"4.3\"\n");

        var gitClient = new FakeGitClient { FileSystemForFetchedContent = fileSystem };
        gitClient.FetchedFilesByUrl[RepoUrl] = new Dictionary<string, string> {
            ["addons/dialogue_manager/plugin.cfg"] = "cfg",
            ["addons/dialogue_manager/main.tscn"] = SceneWithUid,
            ["addons/dialogue_manager/x.gd"] = ScriptLf,
        };
        gitClient.RemoteRefsByUrl[RepoUrl] = [new GitRef("v1.0.0", new string('a', 40), GitRefKind.Tag)];

        return (new DependencyWorkflow(fileSystem, gitClient, "cache"), fileSystem, gitClient);
    }

    private static async Task<(DependencyWorkflow Workflow, InMemoryFileSystem FileSystem, FakeGitClient GitClient)> InstalledAsync() {
        var built = Build();
        await built.Workflow.AddAsync(ProjectDir, RepoUrl);
        return built;
    }

    private static AddonExplanation ExplanationOf(StatusResult result) {
        var addon = Assert.Single(result.Addons);
        Assert.Equal(AddonStatusKind.Modified, addon.Kind);
        return Assert.IsType<AddonExplanation>(addon.Explanation);
    }

    /// <summary>The plain path stays exactly what it was: local, offline, and unexplained.</summary>
    [Fact]
    public async Task StatusAsync_WithoutExplain_AttachesNothing() {
        var (workflow, fileSystem, _) = await InstalledAsync();
        fileSystem.WriteAllText($"{AddonDir}/hand-edit.gd", "surprise");

        var result = await workflow.StatusAsync(ProjectDir, explain: false);

        var addon = Assert.Single(result.Addons);
        Assert.Equal(AddonStatusKind.Modified, addon.Kind);
        Assert.Null(addon.Explanation);
    }

    [Fact]
    public async Task StatusAsync_CleanAddon_IsNotExplained() {
        var (workflow, _, _) = await InstalledAsync();

        var result = await workflow.StatusAsync(ProjectDir, explain: true);

        var addon = Assert.Single(result.Addons);
        Assert.Equal(AddonStatusKind.Clean, addon.Kind);
        Assert.Null(addon.Explanation);
    }

    /// <summary>The case the whole feature exists for: a file the user would lose, named.</summary>
    [Fact]
    public async Task StatusAsync_AddedFile_NamesItAndRefusesToCallForcingSafe() {
        var (workflow, fileSystem, _) = await InstalledAsync();
        fileSystem.WriteAllText($"{AddonDir}/my-notes.gd", "hours of work");

        var explanation = ExplanationOf(await workflow.StatusAsync(ProjectDir, explain: true));

        Assert.Equal(ExplainVerdict.ReviewNeeded, explanation.Verdict);
        Assert.Equal(1, explanation.Counts.Content);

        var change = Assert.Single(explanation.Changes);
        Assert.Equal("my-notes.gd", change.Path);
        Assert.Equal(ChangeKind.Added, change.Kind);
        Assert.Equal(ChangeClass.Content, change.Class);
    }

    [Fact]
    public async Task StatusAsync_EditedFile_IsReportedAsChangedContent() {
        var (workflow, fileSystem, _) = await InstalledAsync();
        fileSystem.WriteAllText($"{AddonDir}/plugin.cfg", "cfg, but edited");

        var explanation = ExplanationOf(await workflow.StatusAsync(ProjectDir, explain: true));

        Assert.Equal(ExplainVerdict.ReviewNeeded, explanation.Verdict);
        var change = Assert.Single(explanation.Changes);
        Assert.Equal("plugin.cfg", change.Path);
        Assert.Equal(ChangeKind.Changed, change.Kind);
    }

    /// <summary>
    /// Godot rewriting a stale <c>uid=</c> from the <c>path=</c> beside it — the "modified without
    /// doing anything" this command was built to answer. Nothing here survives a reinstall, so
    /// forcing is safe and the command should say so outright.
    /// </summary>
    [Fact]
    public async Task StatusAsync_GodotRewroteAUid_SaysForcingIsSafe() {
        var (workflow, fileSystem, _) = await InstalledAsync();
        fileSystem.WriteAllText($"{AddonDir}/main.tscn", SceneWithUid.Replace("bxwpwbdv4fm02", "c7k2p9qq1a0zz"));

        var explanation = ExplanationOf(await workflow.StatusAsync(ProjectDir, explain: true));

        Assert.Equal(ExplainVerdict.SafeToForce, explanation.Verdict);
        Assert.Equal(0, explanation.Counts.Content);
        Assert.Equal(1, explanation.Counts.UidOnly);
        Assert.Equal(ChangeClass.UidOnly, Assert.Single(explanation.Changes).Class);
    }

    /// <summary>
    /// Since v0.23 a line-endings-only difference is not a modification at all: the recorded hash ignores line
    /// endings, so another machine's git configuration (CRLF on Windows, LF elsewhere) can no longer make an
    /// untouched addon read as hand-edited. This used to be the explained case "forcing is safe"; there is now
    /// nothing to explain. See docs/DESIGN-v0.23-LINE-ENDINGS.md.
    /// </summary>
    [Fact]
    public async Task StatusAsync_LineEndingsOnly_IsCleanNotModified() {
        var (workflow, fileSystem, _) = await InstalledAsync();
        fileSystem.WriteAllText($"{AddonDir}/x.gd", ScriptLf.Replace("\n", "\r\n"));

        var addon = Assert.Single((await workflow.StatusAsync(ProjectDir, explain: true)).Addons);

        Assert.Equal(AddonStatusKind.Clean, addon.Kind);
        Assert.Null(addon.Explanation);
    }

    /// <summary>
    /// The classification still earns its place when line endings changed BESIDE a real edit: the tree is
    /// modified for a real reason, and the explanation separates the file that only churned from the one that
    /// matters.
    /// </summary>
    [Fact]
    public async Task StatusAsync_LineEndingChurnBesideARealEdit_IsStillClassifiedPerFile() {
        var (workflow, fileSystem, _) = await InstalledAsync();
        fileSystem.WriteAllText($"{AddonDir}/x.gd", ScriptLf.Replace("\n", "\r\n"));
        fileSystem.WriteAllText($"{AddonDir}/plugin.cfg", "cfg, but edited");

        var explanation = ExplanationOf(await workflow.StatusAsync(ProjectDir, explain: true));

        Assert.Equal(ExplainVerdict.ReviewNeeded, explanation.Verdict);
        Assert.Equal(1, explanation.Counts.Content);
        Assert.Equal(1, explanation.Counts.LineEndingsOnly);
    }

    /// <summary>
    /// One real edit alongside benign churn still needs review — the verdict is about the worst
    /// change present, never the majority.
    /// </summary>
    [Fact]
    public async Task StatusAsync_OneRealEditAmongBenignChurn_StillNeedsReview() {
        var (workflow, fileSystem, _) = await InstalledAsync();
        fileSystem.WriteAllText($"{AddonDir}/main.tscn", SceneWithUid.Replace("bxwpwbdv4fm02", "c7k2p9qq1a0zz"));
        fileSystem.WriteAllText($"{AddonDir}/plugin.cfg", "cfg, but edited");

        var explanation = ExplanationOf(await workflow.StatusAsync(ProjectDir, explain: true));

        Assert.Equal(ExplainVerdict.ReviewNeeded, explanation.Verdict);
        Assert.Equal(1, explanation.Counts.Content);
        Assert.Equal(1, explanation.Counts.UidOnly);

        // Content first, so a truncated report never loses the change that matters.
        Assert.Equal(ChangeClass.Content, explanation.Changes[0].Class);
    }

    /// <summary>
    /// No baseline, no explanation — and emphatically not an exception. The status verdicts were
    /// already computed before any of this ran, and the other addons are still explainable.
    /// </summary>
    [Fact]
    public async Task StatusAsync_BaselineCannotBeReconstructed_ReportsUnavailable() {
        var (workflow, fileSystem, gitClient) = await InstalledAsync();
        fileSystem.WriteAllText($"{AddonDir}/my-notes.gd", "hours of work");

        // Cold cache plus an upstream that no longer answers: the shape of being offline, or of a
        // commit that was force-pushed away.
        fileSystem.DeleteDirectory("cache");
        gitClient.FetchedFilesByUrl.Remove(RepoUrl);

        var explanation = ExplanationOf(await workflow.StatusAsync(ProjectDir, explain: true));

        Assert.Equal(ExplainVerdict.Unavailable, explanation.Verdict);
        Assert.False(string.IsNullOrWhiteSpace(explanation.UnavailableReason));
        Assert.Empty(explanation.Changes);
    }

    /// <summary>
    /// The ordinary unpatched addon says nothing about patches. Asserted because the first
    /// implementation reported "no patch applied" on every addon, which is the kind of caveat that
    /// trains a reader to skip the line that matters.
    /// </summary>
    [Fact]
    public async Task StatusAsync_UnpatchedAddon_DoesNotClaimAPatchIsMissing() {
        var (workflow, fileSystem, _) = await InstalledAsync();
        fileSystem.WriteAllText($"{AddonDir}/my-notes.gd", "hours of work");

        var explanation = ExplanationOf(await workflow.StatusAsync(ProjectDir, explain: true));

        Assert.False(explanation.BaselineIncludesPatch);
        Assert.False(explanation.PatchCouldNotBeApplied);
    }

    /// <summary>
    /// A lockfile that names a patch whose file is gone. The comparison is then measurably incomplete
    /// — the patch's own edits are in the listed changes — and the report has to admit that rather
    /// than present them as the user's unexplained hand-edits.
    /// </summary>
    [Fact]
    public async Task StatusAsync_DeclaredPatchFileMissing_SaysTheComparisonIsIncomplete() {
        var (workflow, fileSystem, _) = await InstalledAsync();

        var lockfile = LockfileReader.Read(fileSystem.ReadAllText($"{ProjectDir}/dotquay.lock"));
        fileSystem.WriteAllText(
            $"{ProjectDir}/dotquay.lock",
            LockfileWriter.Serialize(lockfile with {
                Addons = lockfile.Addons
                    .Select(a => a with { PatchHash = "sha256:deadbeef", PatchBaseSha = a.Resolved })
                    .ToList(),
            }));

        fileSystem.WriteAllText($"{AddonDir}/plugin.cfg", "cfg, but edited");

        var explanation = ExplanationOf(await workflow.StatusAsync(ProjectDir, explain: true));

        Assert.True(explanation.PatchCouldNotBeApplied);
        Assert.False(explanation.BaselineIncludesPatch);
    }

    /// <summary>Scratch is scratch: the diff copy must not survive into the project.</summary>
    [Fact]
    public async Task StatusAsync_LeavesNoStagingBehind() {
        var (workflow, fileSystem, _) = await InstalledAsync();
        fileSystem.WriteAllText($"{AddonDir}/my-notes.gd", "hours of work");

        await workflow.StatusAsync(ProjectDir, explain: true);

        Assert.False(fileSystem.DirectoryExists($"{ProjectDir}/.dotquay/staging/dialogue_manager-explain-base"));
    }

    // -- wire contract ----------------------------------------------------------------------

    [Fact]
    public async Task ToJson_UnexplainedStatus_OmitsTheExplanationEntirely() {
        var (workflow, fileSystem, _) = await InstalledAsync();
        fileSystem.WriteAllText($"{AddonDir}/my-notes.gd", "hours of work");

        var json = JsonMapper.ToJson(await workflow.StatusAsync(ProjectDir, explain: false));

        var entry = Assert.Single(json.Addons);
        Assert.Equal("modified", entry.Status);
        Assert.Null(entry.Explanation);
    }

    [Fact]
    public async Task ToJson_ExplainedStatus_UsesTheDocumentedWireStrings() {
        var (workflow, fileSystem, _) = await InstalledAsync();
        fileSystem.WriteAllText($"{AddonDir}/main.tscn", SceneWithUid.Replace("bxwpwbdv4fm02", "c7k2p9qq1a0zz"));

        var json = JsonMapper.ToJson(await workflow.StatusAsync(ProjectDir, explain: true));

        var entry = Assert.Single(json.Addons);
        Assert.Equal("modified", entry.Status);

        var explanation = Assert.IsType<StatusExplanationJson>(entry.Explanation);
        Assert.Equal("safe_to_force", explanation.Verdict);
        Assert.Equal(1, explanation.Counts.Total);
        Assert.Equal(1, explanation.Counts.UidOnly);

        var change = Assert.Single(explanation.Changes);
        Assert.Equal("main.tscn", change.Path);
        Assert.Equal("changed", change.Kind);
        Assert.Equal("uid_only", change.Class);
    }

    /// <summary>
    /// The editor plugin (<c>core/dotquay_wire.gd</c>) switches on these five and nothing else.
    /// Adding an explanation must not have introduced a sixth.
    /// </summary>
    [Fact]
    public void ToWire_StatusKinds_AreUnchangedByThisFeature() {
        Assert.Equal("clean", JsonMapper.ToWire(AddonStatusKind.Clean));
        Assert.Equal("patched", JsonMapper.ToWire(AddonStatusKind.PatchedAsExpected));
        Assert.Equal("modified", JsonMapper.ToWire(AddonStatusKind.Modified));
        Assert.Equal("missing", JsonMapper.ToWire(AddonStatusKind.Missing));
        Assert.Equal("linked", JsonMapper.ToWire(AddonStatusKind.Linked));
    }
}
