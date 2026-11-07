using Dotquay.Core.Versioning;
using Xunit;

namespace Dotquay.Tests.Versioning;

public class AddonVersionTests {
    [Theory]
    [InlineData("v1.5.1", new long[] { 1, 5, 1 })]
    [InlineData("v1.8.0", new long[] { 1, 8, 0 })]
    [InlineData("v0.11.0.3", new long[] { 0, 11, 0, 3 })] // Phantom Camera: 4-component tags
    [InlineData("v4.9", new long[] { 4, 9 })] // godot-sqlite: 2-component tags
    [InlineData("2.44.0", new long[] { 2, 44, 0 })] // no "v" prefix is also fine
    public void TryParse_AcceptsRealWorldTagShapes(string tag, long[] expectedComponents) {
        var ok = AddonVersion.TryParse(tag, out var version);

        Assert.True(ok);
        Assert.Equal(expectedComponents, version!.Components);
        Assert.False(version.IsPrerelease);
    }

    [Theory]
    [InlineData("v5.0-dev1", "dev1")]
    [InlineData("v1.0.2-rc1", "rc1")]
    public void TryParse_TreatsDashSuffixAsPrerelease(string tag, string expectedLabel) {
        var ok = AddonVersion.TryParse(tag, out var version);

        Assert.True(ok);
        Assert.True(version!.IsPrerelease);
        Assert.Equal(expectedLabel, version.PrereleaseLabel);
    }

    [Fact]
    public void TryParse_TreatsStableSuffixAsRelease() {
        var ok = AddonVersion.TryParse("v1.0.2-stable", out var version);

        Assert.True(ok);
        Assert.False(version!.IsPrerelease);
        Assert.Equal(new long[] { 1, 0, 2 }, version.Components);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-version")]
    [InlineData("1")]           // fewer than 2 components
    [InlineData("1.2.3.4.5")]   // more than 4 components
    [InlineData("1.a.3")]       // non-numeric component
    public void TryParse_RejectsUnparseableTags(string tag) {
        var ok = AddonVersion.TryParse(tag, out var version);

        Assert.False(ok);
        Assert.Null(version);
    }

    [Fact]
    public void CompareTo_OrdersByComponentsThenPrerelease() {
        AddonVersion.TryParse("1.2.0", out var v120);
        AddonVersion.TryParse("1.10.0", out var v1100);
        AddonVersion.TryParse("2.0.0-rc1", out var v200rc1);
        AddonVersion.TryParse("2.0.0", out var v200);

        Assert.True(v120!.CompareTo(v1100!) < 0);   // 1.2.0 < 1.10.0 (numeric, not lexical)
        Assert.True(v200rc1!.CompareTo(v200!) < 0); // prerelease sorts below the release
    }

    // SemVer 2.0 §11 precedence, which NuGet follows too (DESIGN-v0.21 §3.4).
    [Theory]
    [InlineData("1.0.0-rc.2", "1.0.0-rc.10")]   // numeric identifiers compare as numbers
    [InlineData("1.0.0-alpha", "1.0.0-alpha.1")] // a shorter identifier list sorts first
    [InlineData("1.0.0-alpha.1", "1.0.0-alpha.beta")] // numeric sorts below alphanumeric
    [InlineData("1.0.0-beta", "1.0.0-rc1")]
    [InlineData("1.0.0-rc1", "1.0.0")]
    public void CompareTo_FollowsSemVerPrereleasePrecedence(string lower, string higher) {
        AddonVersion.TryParse(lower, out var low);
        AddonVersion.TryParse(higher, out var high);

        Assert.True(low!.CompareTo(high!) < 0, $"{lower} should sort below {higher}");
        Assert.True(high!.CompareTo(low!) > 0, $"{higher} should sort above {lower}");
    }

    [Fact]
    public void TryParse_IgnoresBuildMetadata() {
        Assert.True(AddonVersion.TryParse("1.0.7+r3456", out var version));

        Assert.Equal(new long[] { 1, 0, 7 }, version!.Components);
        Assert.False(version.IsPrerelease);
    }
}
