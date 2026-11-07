using Dotquay.Core.Abstractions;
using Dotquay.Core.Manifest;
using Dotquay.Core.Resolve;
using Dotquay.Tests.Support;
using Xunit;

namespace Dotquay.Tests.Resolve;

/// <summary>
/// Covers <see cref="RefClassifier"/> — what <c>dotquay add &lt;url&gt;#ref</c> decides a ref is.
/// </summary>
/// <remarks>
/// Asks the remote rather than pattern-matching the string, because guessing would be wrong often
/// enough to matter: <c>1.0</c> is a plausible branch name and <c>release</c> is a plausible tag.
/// The cost of guessing wrong is a manifest entry that tracks a moving branch when the user meant a
/// fixed tag, or the reverse — both silent.
/// </remarks>
public class RefClassifierTests {
    private const string RepoUrl = "https://example.com/addon.git";

    private static RefClassifier Build(params GitRef[] refs) {
        var gitClient = new FakeGitClient();
        gitClient.RemoteRefsByUrl[RepoUrl] = refs.ToList();
        return new RefClassifier(gitClient);
    }

    private static GitRef Branch(string name) => new(name, new string('b', 40), GitRefKind.Branch);

    private static GitRef Tag(string name) => new(name, new string('t', 40), GitRefKind.Tag);

    [Fact]
    public async Task ClassifyAsync_ABranchName_IsABranch() {
        var classified = await Build(Branch("main"), Tag("v1.0.0")).ClassifyAsync(RepoUrl, "main");

        Assert.Equal(AddonSourceKind.Branch, classified.Kind);
        Assert.Equal("main", classified.Value);
    }

    [Fact]
    public async Task ClassifyAsync_ATagName_IsAnExactVersion() {
        var classified = await Build(Branch("main"), Tag("v1.0.0"))
            .ClassifyAsync(RepoUrl, "v1.0.0");

        // Exact, not widened to "~1.0": naming one tag is a request for that tag, and broadening it
        // would silently install something the user never typed.
        Assert.Equal(AddonSourceKind.Version, classified.Kind);
        Assert.Equal("v1.0.0", classified.Value);
    }

    [Fact]
    public async Task ClassifyAsync_AFullSha_IsARevWithoutAskingTheRemote() {
        var gitClient = new FakeGitClient(); // no refs configured at all
        var sha = new string('a', 40);

        var classified = await new RefClassifier(gitClient).ClassifyAsync(RepoUrl, sha);

        // A commit need not still be a named ref — pinning to one reachable only from history is
        // legitimate, so this must not depend on ls-remote listing it.
        Assert.Equal(AddonSourceKind.Rev, classified.Kind);
        Assert.Equal(sha, classified.Value);
    }

    [Fact]
    public async Task ClassifyAsync_AnAbbreviatedSha_IsARev() {
        var classified = await Build(Branch("main")).ClassifyAsync(RepoUrl, "9f2c1ab");

        Assert.Equal(AddonSourceKind.Rev, classified.Kind);
    }

    [Fact]
    public async Task ClassifyAsync_BranchWinsOverASameNamedTag() {
        var classified = await Build(Branch("release"), Tag("release"))
            .ClassifyAsync(RepoUrl, "release");

        // Repositories really do carry both. Someone typing "#release" almost always means the
        // branch.
        Assert.Equal(AddonSourceKind.Branch, classified.Kind);
    }

    [Fact]
    public async Task ClassifyAsync_ANumericBranchName_IsStillABranch() {
        // The case a string-shaped guess gets wrong: "1.0" looks like a version but is a real
        // branch naming convention.
        var classified = await Build(Branch("1.0")).ClassifyAsync(RepoUrl, "1.0");

        Assert.Equal(AddonSourceKind.Branch, classified.Kind);
    }

    [Fact]
    public async Task ClassifyAsync_UnknownRef_ListsWhatExists() {
        var classifier = Build(Branch("main"), Branch("develop"), Tag("v1.0.0"));

        var exception = await Assert.ThrowsAsync<AddonResolutionException>(
            () => classifier.ClassifyAsync(RepoUrl, "nonexistent"));

        Assert.Contains("nonexistent", exception.Message);
        Assert.Contains("main", exception.Message);
        Assert.Contains("v1.0.0", exception.Message);
    }

    [Fact]
    public async Task ClassifyAsync_RepoWithNothing_SaysSo() {
        var exception = await Assert.ThrowsAsync<AddonResolutionException>(
            () => Build().ClassifyAsync(RepoUrl, "main"));

        Assert.Contains("no branches or tags", exception.Message);
    }

    [Fact]
    public async Task ClassifyAsync_EmptyRef_Throws() {
        await Assert.ThrowsAsync<AddonResolutionException>(() => Build(Branch("main"))
            .ClassifyAsync(RepoUrl, "  "));
    }
}
