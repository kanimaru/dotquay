using Dotquay.Core.Versioning;
using Xunit;

namespace Dotquay.Tests.Versioning;

public class VersionConstraintTests {
    private static AddonVersion Version(string tag) {
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
    public void TildeThreeComponent_MatchesOnlyWithinMinorRange(string candidate, bool expected) {
        VersionConstraint.TryParse("~2.44", out var constraint);

        Assert.Equal(expected, constraint!.IsSatisfiedBy(Version(candidate)));
    }

    [Theory]
    [InlineData("1.2.3", true)]
    [InlineData("1.2.99", true)]
    [InlineData("1.3.0", false)] // Cargo semantics: ~1.2.3 bumps minor, not patch
    [InlineData("1.2.2", false)]
    public void TildeExplicitPatch_BumpsMinorNotPatch(string candidate, bool expected) {
        VersionConstraint.TryParse("~1.2.3", out var constraint);

        Assert.Equal(expected, constraint!.IsSatisfiedBy(Version(candidate)));
    }

    [Theory]
    [InlineData("1.0.0", true)]
    [InlineData("1.99.99", true)]
    [InlineData("2.0.0", false)]
    [InlineData("0.9.0", false)]
    public void TildeMajorOnly_MatchesWholeMajorRange(string candidate, bool expected) {
        VersionConstraint.TryParse("~1", out var constraint);

        Assert.Equal(expected, constraint!.IsSatisfiedBy(Version(candidate)));
    }

    [Fact]
    public void Tilde_ExcludesPrereleaseEvenIfComponentsMatch() {
        VersionConstraint.TryParse("~2.44", out var constraint);

        Assert.False(constraint!.IsSatisfiedBy(Version("2.44.0-rc1")));
    }

    [Theory]
    [InlineData("2.44.1", true)]
    [InlineData("2.44.0", false)]
    [InlineData("2.44.2", false)]
    [InlineData("2.44.1-rc1", false)]
    public void ExactConstraint_MatchesOnlyThatVersion(string candidate, bool expected) {
        VersionConstraint.TryParse("2.44.1", out var constraint);

        Assert.Equal(expected, constraint!.IsSatisfiedBy(Version(candidate)));
    }

    [Theory]
    [InlineData("not-a-version")]
    [InlineData("1.x.3")]
    [InlineData("1.2-rc1")]
    [InlineData(">*")]
    [InlineData("<x")]
    [InlineData("1.2.3.4.5")]
    [InlineData("[1.0,2.0)")] // NuGet interval notation is not part of the language (DESIGN-v0.21 §3)
    public void TryParse_RejectsUnparseableConstraint(string text) {
        var ok = VersionConstraint.TryParse(text, out var constraint);

        Assert.False(ok);
        Assert.Null(constraint);
    }

    // Every row of DESIGN-v0.21 §3.1's table, as (constraint, lowest match, first miss above, first
    // miss below or null when the range is unbounded below).
    [Theory]
    [InlineData("1.2.3", "1.2.3", "1.2.4", "1.2.2")]
    [InlineData("=1.2.3", "1.2.3", "1.2.4", "1.2.2")]
    [InlineData("1.2", "1.2.0", "1.3.0", "1.1.9")]
    [InlineData("1.2.x", "1.2.0", "1.3.0", "1.1.9")]
    [InlineData("1.2.*", "1.2.0", "1.3.0", "1.1.9")]
    [InlineData("1", "1.0.0", "2.0.0", "0.9.9")]
    [InlineData("1.x", "1.0.0", "2.0.0", "0.9.9")]
    [InlineData("~1.2.3", "1.2.3", "1.3.0", "1.2.2")]
    [InlineData("~1.2", "1.2.0", "1.3.0", "1.1.9")]
    [InlineData("~1", "1.0.0", "2.0.0", "0.9.9")]
    [InlineData("^1.2.3", "1.2.3", "2.0.0", "1.2.2")]
    [InlineData("^0.2.3", "0.2.3", "0.3.0", "0.2.2")]
    [InlineData("^0.0.3", "0.0.3", "0.0.4", "0.0.2")]
    [InlineData("^1.2", "1.2.0", "2.0.0", "1.1.9")]
    [InlineData("^1", "1.0.0", "2.0.0", "0.9.9")]
    [InlineData("^0", "0.0.0", "1.0.0", null)]
    [InlineData("^0.0", "0.0.0", "0.1.0", null)]
    [InlineData("1.2 - 1.4.5", "1.2.0", "1.4.6", "1.1.9")]
    [InlineData("1.2 - 1.4", "1.2.0", "1.5.0", "1.1.9")]
    [InlineData(">=1.2 <2", "1.2.0", "2.0.0", "1.1.9")]
    [InlineData(">=1.2, <2", "1.2.0", "2.0.0", "1.1.9")]
    [InlineData(">= 1.2 < 2", "1.2.0", "2.0.0", "1.1.9")]
    [InlineData(">1.2.3 <=1.2.5", "1.2.4", "1.2.6", "1.2.3")]
    [InlineData(">1.2", "1.3.0", null, "1.2.9")]
    [InlineData("<=1.2", "0.0.0", "1.3.0", null)]
    [InlineData("v1.2.3", "1.2.3", "1.2.4", "1.2.2")]
    public void Grammar_MatchesTheDesignTable(string text, string lowest, string? aboveMiss, string? belowMiss) {
        Assert.True(VersionConstraint.TryParse(text, out var constraint), $"'{text}' should parse");

        Assert.True(constraint!.IsSatisfiedBy(Version(lowest)), $"'{text}' should accept {lowest}");
        if (aboveMiss is not null) {
            Assert.False(constraint.IsSatisfiedBy(Version(aboveMiss)), $"'{text}' should refuse {aboveMiss}");
        }

        if (belowMiss is not null) {
            Assert.False(constraint.IsSatisfiedBy(Version(belowMiss)), $"'{text}' should refuse {belowMiss}");
        }
    }

    [Theory]
    [InlineData("*")]
    [InlineData("x")]
    [InlineData("")]
    public void AnyVersion_AcceptsEveryReleaseButNoPrerelease(string text) {
        Assert.True(VersionConstraint.TryParse(text, out var constraint));

        Assert.True(constraint!.IsSatisfiedBy(Version("0.0.1")));
        Assert.True(constraint.IsSatisfiedBy(Version("99.1.0")));
        Assert.False(constraint.IsSatisfiedBy(Version("1.0.0-rc1")));
    }

    [Theory]
    [InlineData("1.4.0", true)]
    [InlineData("2.3.1", true)]
    [InlineData("3.0.0", false)]
    [InlineData("1.3.9", false)]
    public void Or_AcceptsEitherAlternative(string candidate, bool expected) {
        VersionConstraint.TryParse("~1.4 || ^2.0", out var constraint);

        Assert.Equal(expected, constraint!.IsSatisfiedBy(Version(candidate)));
    }

    [Theory]
    [InlineData("0.11.0.3", true)]
    [InlineData("0.11.0", false)]
    [InlineData("0.11.0.4", false)]
    public void FourComponentExact_MatchesOnlyThatVersion(string candidate, bool expected) {
        VersionConstraint.TryParse("0.11.0.3", out var constraint);

        Assert.Equal(expected, constraint!.IsSatisfiedBy(Version(candidate)));
    }

    [Theory]
    [InlineData("0.11.0.3", true)]
    [InlineData("0.11.9.1", true)]
    [InlineData("0.12.0.0", false)]
    public void TildeOnFourComponents_NeverBumpsTheFourth(string candidate, bool expected) {
        VersionConstraint.TryParse("~0.11.0.3", out var constraint);

        Assert.Equal(expected, constraint!.IsSatisfiedBy(Version(candidate)));
    }

    [Fact]
    public void ExactThreeComponents_MatchesTheSameVersionWithAZeroFourth() {
        VersionConstraint.TryParse("1.2.3", out var constraint);

        Assert.True(constraint!.IsSatisfiedBy(Version("1.2.3.0")));
        Assert.False(constraint.IsSatisfiedBy(Version("1.2.3.1")));
    }

    [Theory]
    [InlineData("^5.0", "5.1.0-rc1", false)] // no prerelease named, so none selected
    [InlineData("^5.1.0-rc1", "5.1.0-rc1", true)]
    [InlineData("^5.1.0-rc1", "5.1.0-rc5", true)]
    [InlineData("^5.1.0-rc1", "5.1.0", true)]
    [InlineData("^5.1.0-rc1", "5.9.0", true)]
    [InlineData("^5.1.0-rc1", "5.2.0-rc1", false)] // a prerelease of a different version
    [InlineData("^5.1.0-rc1", "5.1.0-beta", false)] // below the lower bound
    [InlineData("^5.1.0-rc1", "6.0.0-rc1", false)]
    [InlineData("5.1.0-rc1", "5.1.0-rc1", true)]
    [InlineData("5.1.0-rc1", "5.1.0-rc5", false)]
    [InlineData("5.1.0-rc1", "5.1.0", false)]
    [InlineData(">=5.1.0-rc.2", "5.1.0-rc.10", true)] // numeric identifiers compare as numbers
    public void Prerelease_OnlyWhenTheRangeNamesOneOfTheSameVersion(string text, string candidate, bool expected) {
        Assert.True(VersionConstraint.TryParse(text, out var constraint));

        Assert.Equal(expected, constraint!.IsSatisfiedBy(Version(candidate)));
    }

    [Theory]
    [InlineData("3.0.0", true)]
    [InlineData("3.0.9", true)]
    [InlineData("3.1.0", false)]
    [InlineData("2.9.0", false)]
    public void Intersect_AcceptsOnlyWhatBothAccept(string candidate, bool expected) {
        VersionConstraint.TryParse("^3.0", out var left);
        VersionConstraint.TryParse("~3.0 || ~2.9", out var right);

        Assert.Equal(expected, left!.Intersect(right!).IsSatisfiedBy(Version(candidate)));
    }
}
