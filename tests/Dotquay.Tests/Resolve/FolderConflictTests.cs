using Dotquay.Core.Manifest;
using Dotquay.Core.Resolve;
using Dotquay.Tests.Support;
using Xunit;

namespace Dotquay.Tests.Resolve;

/// <summary>
/// Covers <see cref="GraphConflictKind.DisagreeingInstallFolders"/> — two requirers wanting the same
/// addon in different folders under <c>addons/</c>.
/// </summary>
/// <remarks>
/// Reported rather than guessed, unlike <c>path</c> (which SPEC.md §8 takes from the first
/// requirement). The asymmetry is deliberate: a slightly-wrong source subtree usually fails loudly at
/// install time, whereas a wrong install folder breaks a dependent addon's <c>preload</c> at parse
/// time — Godot refuses to compile the script, and the error points at the innocent addon rather than
/// at the folder that moved.
/// </remarks>
public class FolderConflictTests {
    private const string CoreUrl = "https://example.com/core.git";

    private static AddonUnifier Build() => new(new FakeGitClient());

    private static AddonRequirement Wants(string requirer, string? installAs) =>
        new(requirer, new AddonSpec { Name = "core", Git = CoreUrl, Rev = new string('a', 40), InstallAs = installAs });

    [Fact]
    public async Task UnifyAsync_AgreeingFolders_Unify() {
        var outcome = await Build().UnifyAsync("core", [Wants("a", "Shared-Core"), Wants("b", "Shared-Core")]);

        Assert.True(outcome.IsResolved);
        Assert.Equal("Shared-Core", outcome.Resolved.EffectiveFolder);
    }

    [Fact]
    public async Task UnifyAsync_OneSilentOneExplicitButEqual_Unify() {
        // "core" and install_as = "core" are the same request written two ways; treating them as a
        // conflict would fire on a project that is entirely consistent.
        var outcome = await Build().UnifyAsync("core", [Wants("a", installAs: null), Wants("b", "core")]);

        Assert.True(outcome.IsResolved);
        Assert.Equal("core", outcome.Resolved.EffectiveFolder);
    }

    [Fact]
    public async Task UnifyAsync_DifferentFolders_Conflict() {
        var outcome = await Build().UnifyAsync("core", [Wants("a", "Godot-MToon-Shader"), Wants("b", "mtoon")]);

        Assert.False(outcome.IsResolved);
        Assert.Equal(GraphConflictKind.DisagreeingInstallFolders, outcome.Conflict.Kind);
    }

    [Fact]
    public async Task UnifyAsync_DifferentFolders_ConflictsEvenWhenTheCommitAgrees() {
        var outcome = await Build().UnifyAsync("core", [Wants("a", "Godot-MToon-Shader"), Wants("b", "mtoon")]);

        // Both requirements name the identical rev. Agreement on the commit is not agreement on the
        // destination, and Godot's flat addons/ can only hold one copy.
        Assert.False(outcome.IsResolved);
        Assert.All(outcome.Conflict.Requirements, r => Assert.Equal(new string('a', 40), r.Constraint));
    }

    [Fact]
    public async Task UnifyAsync_DifferentFolders_OffersEachFolderAsACandidate() {
        var outcome = await Build().UnifyAsync("core", [Wants("a", "Godot-MToon-Shader"), Wants("b", "mtoon")]);

        var folders = outcome.Conflict!.Candidates.Select(c => c.InstallAs).ToList();
        Assert.Contains("Godot-MToon-Shader", folders);
        Assert.Contains("mtoon", folders);
    }

    [Fact]
    public async Task UnifyAsync_DifferentFolders_SaysWhoWantsWhich() {
        var outcome = await Build().UnifyAsync(
            "core", [Wants("a", "Godot-MToon-Shader"), Wants("b", "mtoon"), Wants("c", "Godot-MToon-Shader")]);

        // The right answer is usually "whichever folder something hardcodes in a preload", so who
        // asked for what is the entire basis for choosing.
        var preferred = outcome.Conflict!.Candidates.Single(c => c.InstallAs == "Godot-MToon-Shader");
        Assert.Equal(["a", "c"], preferred.SatisfiedRequirers);
    }

    [Fact]
    public async Task UnifyAsync_DifferentFolders_MessageNamesBothFolders() {
        var outcome = await Build().UnifyAsync("core", [Wants("a", "Godot-MToon-Shader"), Wants("b", "mtoon")]);

        Assert.Contains("addons/Godot-MToon-Shader", outcome.Conflict!.Message);
        Assert.Contains("addons/mtoon", outcome.Conflict.Message);
        Assert.Contains("install_as", outcome.Conflict.Message);
    }

    [Fact]
    public async Task UnifyAsync_SingleRequirement_NeverConflictsOverItsFolder() {
        var outcome = await Build().UnifyAsync("core", [Wants("<project>", "Whatever-I-Like")]);

        Assert.True(outcome.IsResolved);
        Assert.Equal("Whatever-I-Like", outcome.Resolved.EffectiveFolder);
    }

    [Fact]
    public async Task UnifyAsync_FolderDisagreementIsReportedBeforeVersionDisagreement() {
        var gitClient = new FakeGitClient();
        var unifier = new AddonUnifier(gitClient);

        var requirements = new List<AddonRequirement>
        {
            new("a", new AddonSpec { Name = "core", Git = CoreUrl, Version = "~1", InstallAs = "One" }),
            new("b", new AddonSpec { Name = "core", Git = CoreUrl, Version = "~2", InstallAs = "Two" }),
        };

        var outcome = await unifier.UnifyAsync("core", requirements);

        // Both are wrong, but the folder is the one a user can't discover from a version error — and
        // reporting it first avoids sending them to fix versions only to hit this next.
        Assert.Equal(GraphConflictKind.DisagreeingInstallFolders, outcome.Conflict!.Kind);
    }
}
