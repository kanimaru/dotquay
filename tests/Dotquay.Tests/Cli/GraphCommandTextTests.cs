using Dotquay.Cli.Commands;
using Dotquay.Core.Json;
using Xunit;

namespace Dotquay.Tests.Cli;

/// <summary>
/// Covers the NuGet line of <c>dotquay graph</c>'s human-readable tree. It names the addons that
/// declared a package instead of saying "an addon" (DESIGN-v0.19).
/// </summary>
public class GraphCommandTextTests {
    private static DotnetJson Package(string origin, bool declaredByProject,
        params string[] requiredBy) => new() {
            Name = "Serilog",
            Origin = origin,
            DeclaredByProject = declaredByProject,
            RequiredBy = requiredBy,
        };

    [Fact]
    public void DescribeDotnetOrigin_ProjectOnly_SaysNothingExtra() {
        Assert.Equal("", GraphCommand.DescribeDotnetOrigin(Package("root", true, "<project>")));
    }

    [Fact]
    public void DescribeDotnetOrigin_Transitive_NamesTheAddons() {
        Assert.Equal("  (required by a, b)",
            GraphCommand.DescribeDotnetOrigin(Package("transitive", false, "a", "b")));
    }

    [Fact]
    public void DescribeDotnetOrigin_RootAlsoDeclaredByAnAddon_SaysAlso() {
        Assert.Equal("  (also required by a)",
            GraphCommand.DescribeDotnetOrigin(Package("root", true, "<project>", "a")));
    }

    [Fact]
    public void DescribeDotnetOrigin_Orphan_PointsAtDoctor() {
        Assert.Contains("dotquay doctor",
            GraphCommand.DescribeDotnetOrigin(Package("orphan", false)));
    }

    [Fact]
    public void DescribeDotnetOrigin_PayloadFromAnOlderShape_FallsBackToTheOldWording() {
        // No origin and no requirers: exactly what a GraphJson built before v0.19 looks like.
        Assert.Equal("  (required by an addon)",
            GraphCommand.DescribeDotnetOrigin(new DotnetJson { Name = "X" }));
    }
}
