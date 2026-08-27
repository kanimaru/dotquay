using Dotquay.Core.Versioning;
using Xunit;

namespace Dotquay.Tests.Versioning;

public class VersionConstraintTests
{
    private static AddonVersion Version(string tag)
    {
        var ok = AddonVersion.TryParse(tag, out var version);
        Assert.True(ok);
        return version!;
    }

    [Theory]
    [InlineData("2.44.0", true)]
    [InlineData("2.44.5", true)]
    [InlineData("2.44.99", true)]
    [InlineData("2.45.0", false)]
    [InlineData("2.43.9", false)]
    public void TildeThreeComponent_MatchesOnlyWithinMinorRange(string candidate, bool expected)
    {
        VersionConstraint.TryParse("~2.44", out var constraint);

        Assert.Equal(expected, constraint!.IsSatisfiedBy(Version(candidate)));
    }

    [Theory]
    [InlineData("1.2.3", true)]
    [InlineData("1.2.99", true)]
    [InlineData("1.3.0", false)] // Cargo semantics: ~1.2.3 bumps minor, not patch
    [InlineData("1.2.2", false)]
    public void TildeExplicitPatch_BumpsMinorNotPatch(string candidate, bool expected)
    {
        VersionConstraint.TryParse("~1.2.3", out var constraint);

        Assert.Equal(expected, constraint!.IsSatisfiedBy(Version(candidate)));
    }

    [Theory]
    [InlineData("1.0.0", true)]
    [InlineData("1.99.99", true)]
    [InlineData("2.0.0", false)]
    [InlineData("0.9.0", false)]
    public void TildeMajorOnly_MatchesWholeMajorRange(string candidate, bool expected)
    {
        VersionConstraint.TryParse("~1", out var constraint);

        Assert.Equal(expected, constraint!.IsSatisfiedBy(Version(candidate)));
    }

    [Fact]
    public void Tilde_ExcludesPrereleaseEvenIfComponentsMatch()
    {
        VersionConstraint.TryParse("~2.44", out var constraint);

        Assert.False(constraint!.IsSatisfiedBy(Version("2.44.0-rc1")));
    }

    [Theory]
    [InlineData("2.44.1", true)]
    [InlineData("2.44.0", false)]
    [InlineData("2.44.2", false)]
    public void ExactConstraint_MatchesOnlyThatVersion(string candidate, bool expected)
    {
        VersionConstraint.TryParse("2.44.1", out var constraint);

        Assert.Equal(expected, constraint!.IsSatisfiedBy(Version(candidate)));
    }

    [Fact]
    public void TryParse_RejectsUnparseableConstraint()
    {
        var ok = VersionConstraint.TryParse("not-a-version", out var constraint);

        Assert.False(ok);
        Assert.Null(constraint);
    }
}
