using Dotquay.Core.Abstractions;
using Dotquay.Core.Versioning;
using Xunit;

namespace Dotquay.Tests.Versioning;

public class TagSelectorTests
{
    [Fact]
    public void SelectHighestMatching_PicksHighestSatisfyingTag()
    {
        var refs = new[]
        {
            new GitRef("v1.5", "sha-a", GitRefKind.Tag),
            new GitRef("v1.5.1", "sha-b", GitRefKind.Tag),
            new GitRef("v1.6.0", "sha-c", GitRefKind.Tag), // outside the ~1.5 range
        };
        VersionConstraint.TryParse("~1.5", out var constraint);

        var match = TagSelector.SelectHighestMatching(refs, constraint!);

        Assert.NotNull(match);
        Assert.Equal("v1.5.1", match!.Ref.Name);
        Assert.Equal("sha-b", match.Ref.CommitSha);
    }

    [Fact]
    public void SelectHighestMatching_IgnoresBranchRefs()
    {
        var refs = new[]
        {
            new GitRef("v9.9.9", "sha-branch-tricking-us", GitRefKind.Branch),
        };
        VersionConstraint.TryParse("~1", out var constraint);

        var match = TagSelector.SelectHighestMatching(refs, constraint!);

        Assert.Null(match);
    }

    [Fact]
    public void SelectHighestMatching_SkipsUnparseableTagsWithoutThrowing()
    {
        var refs = new[]
        {
            new GitRef("not-a-version", "sha-a", GitRefKind.Tag),
            new GitRef("v1.0.0", "sha-b", GitRefKind.Tag),
        };
        VersionConstraint.TryParse("~1", out var constraint);

        var match = TagSelector.SelectHighestMatching(refs, constraint!);

        Assert.NotNull(match);
        Assert.Equal("v1.0.0", match!.Ref.Name);
    }

    [Fact]
    public void SelectHighestMatching_ReturnsNullWhenNothingMatches()
    {
        var refs = new[]
        {
            new GitRef("v9.0.0", "sha-a", GitRefKind.Tag),
        };
        VersionConstraint.TryParse("~1", out var constraint);

        var match = TagSelector.SelectHighestMatching(refs, constraint!);

        Assert.Null(match);
    }
}
