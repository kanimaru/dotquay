using Dotquay.Core.Abstractions;
using Dotquay.Core.Git;
using Xunit;

namespace Dotquay.Tests.Git;

/// <summary>
/// Exercises <see cref="ProcessGitClient.ParseRefs"/> directly against fabricated
/// <c>git ls-remote</c> output — no real git process or network call needed, since this is pure
/// text parsing (SPEC.md §12: testable without network/disk).
/// </summary>
public class ProcessGitClientTests {
    [Fact]
    public void ParseRefs_ParsesBranchesAndLightweightTags() {
        const string output = """
            7fd1a60b01f91b314f59955a4e4d4e80d8edf11d	refs/heads/master
            b1b3f9723831141a31a1a7252a213e216ea76e56	refs/tags/v1.0.0
            """;

        var refs = ProcessGitClient.ParseRefs(output);

        Assert.Equal(2, refs.Count);
        Assert.Contains(refs, r =>
            r is {
                Name: "master", Kind: GitRefKind.Branch,
                CommitSha: "7fd1a60b01f91b314f59955a4e4d4e80d8edf11d"
            });
        Assert.Contains(refs, r =>
            r is {
                Name: "v1.0.0", Kind: GitRefKind.Tag,
                CommitSha: "b1b3f9723831141a31a1a7252a213e216ea76e56"
            });
    }

    [Fact]
    public void ParseRefs_PrefersPeeledCommitOverAnnotatedTagObjectSha() {
        // An annotated tag: the first line is the tag OBJECT's own sha; the "^{}" line is the
        // commit it points at. We want the commit, since that's what gets fetched/pinned.
        const string output = """
            aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa	refs/tags/v2.0.0
            bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb	refs/tags/v2.0.0^{}
            """;

        var refs = ProcessGitClient.ParseRefs(output);

        var tag = Assert.Single(refs);
        Assert.Equal("v2.0.0", tag.Name);
        Assert.Equal(GitRefKind.Tag, tag.Kind);
        Assert.Equal("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", tag.CommitSha);
    }

    [Fact]
    public void ParseRefs_IgnoresRefsOutsideTagsAndHeads() {
        const string output = """
            cccccccccccccccccccccccccccccccccccccccc	refs/pull/1/head
            dddddddddddddddddddddddddddddddddddddddd	refs/heads/main
            """;

        var refs = ProcessGitClient.ParseRefs(output);

        var only = Assert.Single(refs);
        Assert.Equal("main", only.Name);
    }

    [Fact]
    public void ParseRefs_IgnoresBlankLinesAndMalformedInput() {
        const string output = "\n\nnot-a-valid-line-at-all\n\n";

        var refs = ProcessGitClient.ParseRefs(output);

        Assert.Empty(refs);
    }

    [Fact]
    public void ParseRefs_EmptyInputProducesNoRefs() {
        var refs = ProcessGitClient.ParseRefs("");

        Assert.Empty(refs);
    }
}
