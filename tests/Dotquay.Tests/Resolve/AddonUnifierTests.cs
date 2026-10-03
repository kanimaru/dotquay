using Dotquay.Core.Abstractions;
using Dotquay.Core.Manifest;
using Dotquay.Core.Resolve;
using Dotquay.Tests.Support;
using Xunit;

namespace Dotquay.Tests.Resolve;

/// <summary>
/// Covers <see cref="AddonUnifier"/> — the v0.5 extraction of SPEC.md §8's unification rules.
/// </summary>
/// <remarks>
/// The rules themselves already had coverage through <see cref="DependencyGraphResolver"/>; what is
/// new, and what these tests are really about, is that a failure now carries enough structure for a
/// UI to offer a way out (SPEC.md §18). A conflict that reports no usable candidate is a conflict the
/// editor's picker can only shrug at, so the candidate assertions matter as much as the kind ones.
/// </remarks>
public class AddonUnifierTests
{
    private const string CoreUrl = "https://example.com/core.git";
    private const string ForkUrl = "https://example.com/core-fork.git";

    private static (AddonUnifier Unifier, FakeGitClient GitClient) Build()
    {
        var gitClient = new FakeGitClient();
        return (new AddonUnifier(gitClient), gitClient);
    }

    private static void SetTags(FakeGitClient gitClient, string repoUrl, params string[] tags) =>
        gitClient.RemoteRefsByUrl[repoUrl] = tags
            .Select((tag, i) => new GitRef(tag, new string('a', 39) + i, GitRefKind.Tag))
            .ToList();

    private static AddonRequirement Wants(string requirer, string version, string git = CoreUrl) =>
        new(requirer, new AddonSpec { Name = "core", Git = git, Version = version });

    private static AddonRequirement WantsRev(string requirer, string rev, string git = CoreUrl) =>
        new(requirer, new AddonSpec { Name = "core", Git = git, Rev = rev });

    private static AddonRequirement WantsBranch(string requirer, string branch, string git = CoreUrl) =>
        new(requirer, new AddonSpec { Name = "core", Git = git, Branch = branch });

    [Fact]
    public async Task UnifyAsync_SingleVersionRequirement_ResolvesToHighestMatchingTag()
    {
        var (unifier, gitClient) = Build();
        SetTags(gitClient, CoreUrl, "v1.2.0", "v1.4.0", "v1.4.2", "v2.0.0");

        // "~1.4" is a Cargo-style tilde range: >= 1.4.0 and < 1.5.0. v2.0.0 is outside it, v1.2.0 is
        // below it, so v1.4.2 is the highest tag that qualifies.
        var outcome = await unifier.UnifyAsync("core", [Wants("<project>", "~1.4")]);

        Assert.True(outcome.IsResolved);
        Assert.Equal("v1.4.2", outcome.Resolved.DisplayVersion);
    }

    [Fact]
    public async Task UnifyAsync_RangeNamingAPrerelease_CanSelectOne()
    {
        var (unifier, gitClient) = Build();
        SetTags(gitClient, CoreUrl, "v1.0.1", "v1.0.2-rc1", "v1.0.2-rc2", "v1.1.0-rc1");

        // DESIGN-v0.21 §3.4: the range names a 1.0.2 prerelease, so later 1.0.2 prereleases qualify,
        // and 1.1.0-rc1 does not — it is a prerelease of a different version.
        var outcome = await unifier.UnifyAsync("core", [Wants("<project>", "^1.0.2-rc1")]);

        Assert.True(outcome.IsResolved);
        Assert.Equal("v1.0.2-rc2", outcome.Resolved.DisplayVersion);
    }

    [Fact]
    public async Task UnifyAsync_OverlappingVersionRanges_PicksTheHighestSatisfyingAll()
    {
        var (unifier, gitClient) = Build();
        SetTags(gitClient, CoreUrl, "v1.2.0", "v1.2.5", "v1.4.0", "v2.0.0");

        // The SPEC.md §8 diamond. Tilde bounds differ by component count: "~1" means < 2.0.0, while
        // "~1.2" bumps the minor and means < 1.3.0. Their intersection is [1.2.0, 1.3.0), so v1.2.5
        // wins — v1.4.0 satisfies 'a' but not 'b', and v2.0.0 satisfies neither.
        var outcome = await unifier.UnifyAsync("core", [Wants("a", "~1"), Wants("b", "~1.2")]);

        Assert.True(outcome.IsResolved);
        Assert.Equal("v1.2.5", outcome.Resolved.DisplayVersion);
    }

    [Fact]
    public async Task UnifyAsync_DisjointVersionRanges_ReportsNoCommonVersion()
    {
        var (unifier, gitClient) = Build();
        SetTags(gitClient, CoreUrl, "v1.4.0", "v2.0.0");

        var outcome = await unifier.UnifyAsync("core", [Wants("a", "~1"), Wants("b", "~2")]);

        Assert.False(outcome.IsResolved);
        Assert.Equal(GraphConflictKind.NoCommonVersion, outcome.Conflict.Kind);
        Assert.Contains("'a'", outcome.Conflict.Message);
        Assert.Contains("'b'", outcome.Conflict.Message);
    }

    [Fact]
    public async Task UnifyAsync_DisjointVersionRanges_OffersEveryStableTagAsACandidate()
    {
        var (unifier, gitClient) = Build();
        SetTags(gitClient, CoreUrl, "v1.4.0", "v2.0.0");

        var outcome = await unifier.UnifyAsync("core", [Wants("a", "~1"), Wants("b", "~2")]);

        Assert.Equal(2, outcome.Conflict!.Candidates.Count);
        Assert.All(outcome.Conflict.Candidates, c => Assert.Equal(AddonSourceKind.Version, c.Kind));

        // Newest first: a picker that leads with an old tag makes the safe choice look like the
        // regressive one.
        Assert.Equal("v2.0.0", outcome.Conflict.Candidates[0].Value);
        Assert.Equal("v1.4.0", outcome.Conflict.Candidates[1].Value);
    }

    [Fact]
    public async Task UnifyAsync_DisjointVersionRanges_RecordsWhichRequirerEachCandidateSatisfies()
    {
        var (unifier, gitClient) = Build();
        SetTags(gitClient, CoreUrl, "v1.4.0", "v2.0.0");

        var outcome = await unifier.UnifyAsync("core", [Wants("a", "~1"), Wants("b", "~2")]);

        var v2 = outcome.Conflict!.Candidates.Single(c => c.Value == "v2.0.0");
        var v1 = outcome.Conflict.Candidates.Single(c => c.Value == "v1.4.0");

        Assert.Equal(["b"], v2.SatisfiedRequirers);
        Assert.Equal(["a"], v1.SatisfiedRequirers);
        Assert.All(outcome.Conflict.Candidates, c => Assert.False(c.SatisfiesAll));
    }

    [Fact]
    public async Task UnifyAsync_CandidatesCarryTheirCommitSha()
    {
        var (unifier, gitClient) = Build();
        SetTags(gitClient, CoreUrl, "v1.4.0", "v2.0.0");

        var outcome = await unifier.UnifyAsync("core", [Wants("a", "~1"), Wants("b", "~2")]);

        Assert.All(outcome.Conflict!.Candidates, c => Assert.False(string.IsNullOrEmpty(c.CommitSha)));
    }

    [Fact]
    public async Task UnifyAsync_PrereleaseTags_AreNeverOfferedAsCandidates()
    {
        var (unifier, gitClient) = Build();
        SetTags(gitClient, CoreUrl, "v1.4.0", "v2.0.0-rc1", "v2.0.0");

        var outcome = await unifier.UnifyAsync("core", [Wants("a", "~1"), Wants("b", "~3")]);

        // SPEC.md §15: version resolution never silently picks a "-rc"/"-dev" build, and a picker
        // that offers one would reintroduce exactly that through the back door.
        Assert.DoesNotContain(outcome.Conflict!.Candidates, c => c.Value.Contains("rc"));
    }

    [Fact]
    public async Task UnifyAsync_DifferentRepositories_ReportsDifferentGitUrls()
    {
        var (unifier, gitClient) = Build();
        SetTags(gitClient, CoreUrl, "v1.0.0");
        SetTags(gitClient, ForkUrl, "v1.0.0");

        var outcome = await unifier.UnifyAsync("core", [Wants("a", "~1"), Wants("b", "~1", ForkUrl)]);

        Assert.False(outcome.IsResolved);
        Assert.Equal(GraphConflictKind.DifferentGitUrls, outcome.Conflict.Kind);
        Assert.Contains(outcome.Conflict.Candidates, c => c.Git == CoreUrl);
        Assert.Contains(outcome.Conflict.Candidates, c => c.Git == ForkUrl);
    }

    [Fact]
    public async Task UnifyAsync_TwoDifferentRevPins_ReportsDisjointPins()
    {
        var (unifier, _) = Build();

        var outcome = await unifier.UnifyAsync("core", [WantsRev("a", "aaa1111"), WantsRev("b", "bbb2222")]);

        Assert.False(outcome.IsResolved);
        Assert.Equal(GraphConflictKind.DisjointPins, outcome.Conflict.Kind);
        Assert.Equal(2, outcome.Conflict.Candidates.Count);
        Assert.All(outcome.Conflict.Candidates, c => Assert.Equal(AddonSourceKind.Rev, c.Kind));
    }

    [Fact]
    public async Task UnifyAsync_IdenticalRevPins_Unify()
    {
        var (unifier, _) = Build();

        // The same pin twice isn't a disagreement — refusing it would make a shared dependency
        // impossible to declare consistently.
        var outcome = await unifier.UnifyAsync("core", [WantsRev("a", "aaa1111"), WantsRev("b", "aaa1111")]);

        Assert.True(outcome.IsResolved);
        Assert.Equal("aaa1111", outcome.Resolved.ResolvedSha);
    }

    [Fact]
    public async Task UnifyAsync_PinAgainstVersionRange_ReportsDisjointPins()
    {
        var (unifier, gitClient) = Build();
        SetTags(gitClient, CoreUrl, "v1.0.0");

        var outcome = await unifier.UnifyAsync("core", [WantsRev("a", "aaa1111"), Wants("b", "~1")]);

        Assert.False(outcome.IsResolved);
        Assert.Equal(GraphConflictKind.DisjointPins, outcome.Conflict.Kind);
    }

    [Fact]
    public async Task UnifyAsync_BranchPinCandidate_ResolvesToTheBranchTip()
    {
        var (unifier, gitClient) = Build();
        gitClient.BranchTips[(CoreUrl, "godot4")] = new string('c', 40);

        var outcome = await unifier.UnifyAsync("core", [WantsBranch("a", "godot4"), WantsRev("b", "bbb2222")]);

        var branchCandidate = outcome.Conflict!.Candidates.Single(c => c.Kind == AddonSourceKind.Branch);
        Assert.Equal(new string('c', 40), branchCandidate.CommitSha);
    }

    [Fact]
    public async Task UnifyAsync_CandidateThatCannotResolve_IsStillOfferedWithoutASha()
    {
        var (unifier, gitClient) = Build();
        // The fork has no tags at all, so 'b's constraint resolves to nothing — what a repo whose
        // release tags were deleted upstream looks like. Hiding that option would leave the user
        // staring at a two-sided conflict with only one side visible.
        SetTags(gitClient, CoreUrl, "v1.0.0");
        SetTags(gitClient, ForkUrl);

        var outcome = await unifier.UnifyAsync("core", [Wants("a", "~1"), Wants("b", "~1", ForkUrl)]);

        Assert.False(outcome.IsResolved);
        Assert.Equal(GraphConflictKind.DifferentGitUrls, outcome.Conflict.Kind);
        Assert.Equal(2, outcome.Conflict.Candidates.Count);
        Assert.Contains(outcome.Conflict.Candidates, c => c.Git == ForkUrl && c.CommitSha is null);
        Assert.Contains(outcome.Conflict.Candidates, c => c.Git == CoreUrl && c.CommitSha is not null);
    }

    [Fact]
    public async Task UnifyAsync_MalformedConstraint_ReportsInvalidConstraint()
    {
        var (unifier, gitClient) = Build();
        SetTags(gitClient, CoreUrl, "v1.0.0");

        var outcome = await unifier.UnifyAsync("core", [Wants("a", "~1"), Wants("b", "not-a-version")]);

        Assert.False(outcome.IsResolved);
        Assert.Equal(GraphConflictKind.InvalidConstraint, outcome.Conflict.Kind);
        Assert.Empty(outcome.Conflict.Candidates);
    }

    [Fact]
    public async Task UnifyAsync_AlwaysReportsEveryRequirer()
    {
        var (unifier, gitClient) = Build();
        SetTags(gitClient, CoreUrl, "v1.0.0", "v2.0.0", "v3.0.0");

        var outcome = await unifier.UnifyAsync("core", [Wants("a", "~1"), Wants("b", "~2"), Wants("c", "~3")]);

        Assert.Equal(3, outcome.Conflict!.Requirements.Count);
        Assert.Equal(["a", "b", "c"], outcome.Conflict.Requirements.Select(r => r.Requirer));
    }

    [Fact]
    public async Task ResolvedOrThrow_OnAConflict_ThrowsWithTheConflictMessage()
    {
        var (unifier, gitClient) = Build();
        SetTags(gitClient, CoreUrl, "v1.0.0", "v2.0.0");

        var outcome = await unifier.UnifyAsync("core", [Wants("a", "~1"), Wants("b", "~2")]);

        // This is what keeps install/update's behaviour identical to v0.4: the same rules, the same
        // message, still a hard error (SPEC.md §8).
        var exception = Assert.Throws<AddonResolutionException>(() => outcome.ResolvedOrThrow());
        Assert.Equal(outcome.Conflict!.Message, exception.Message);
    }

    [Fact]
    public async Task UnifyAsync_NoRequirements_IsAProgrammingError()
    {
        var (unifier, _) = Build();

        // Reaching unification with nothing to unify means the graph walk lost an edge — a bug in
        // dotquay, not a user-facing conflict, so it must not be dressed up as one.
        await Assert.ThrowsAsync<ArgumentException>(() => unifier.UnifyAsync("core", []));
    }
}
