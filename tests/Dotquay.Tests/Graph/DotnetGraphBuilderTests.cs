using Dotquay.Core.Graph;
using Dotquay.Core.Lockfile;
using Dotquay.Core.Manifest;
using Dotquay.Core.Resolve;
using Xunit;

namespace Dotquay.Tests.Graph;

/// <summary>
/// Covers <see cref="DotnetGraphBuilder"/>, the one place both graph modes turn NuGet declarations
/// into package nodes (DESIGN-v0.19 §3).
/// </summary>
/// <remarks>
/// Every case here is an edge the editor would draw wrong. A missing requirer means a package with
/// no arrow from the addon that pulled it in, which is the exact "needed by an addon, but which?"
/// question v0.19 exists to answer. A wrong origin offers Remove on something that has no manifest
/// entry to remove.
/// </remarks>
public class DotnetGraphBuilderTests {
    private static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> NoAddons =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);

    private static DotnetSpec Spec(string name, string version = "1.*") =>
        new() { Name = name, Version = version };

    private static ProjectManifest Manifest(params DotnetSpec[] dotnet) => new() {
        Dotnet = dotnet.ToDictionary(d => d.Name, StringComparer.Ordinal),
    };

    private static DotquayLockfile Lockfile(params LockedDotnet[] dotnet) =>
        new() { Dotnet = dotnet };

    private static LockedDotnet Locked(string name, string resolved = "1.0.0",
        string? source = null) =>
        new() { Name = name, Resolved = resolved, Source = source };

    [Fact]
    public void Build_ProjectDeclaredPackage_IsARootRequiredByTheProject() {
        var nodes = DotnetGraphBuilder.Build(
            Manifest(Spec("Newtonsoft.Json", "13.0.*")),
            Lockfile(Locked("Newtonsoft.Json", "13.0.3")), [], NoAddons);

        var node = Assert.Single(nodes);
        Assert.Equal(GraphNodeOrigin.Root, node.Origin);
        Assert.True(node.DeclaredByProject);
        Assert.Equal([GraphConflict.ProjectRequirerName], node.RequiredBy);
        Assert.Equal("13.0.*", node.Constraint);
        Assert.Equal("13.0.3", node.Resolved);
    }

    [Fact]
    public void Build_AddonDeclaredPackage_IsTransitiveAndNamesTheAddon() {
        var nodes = DotnetGraphBuilder.Build(
            Manifest(),
            Lockfile(Locked("VBeami.Core")),
            [Spec("VBeami.Core", "0.4.*")],
            new Dictionary<string, IReadOnlyList<string>> { ["vbeami"] = ["VBeami.Core"] });

        var node = Assert.Single(nodes);
        Assert.Equal(GraphNodeOrigin.Transitive, node.Origin);
        Assert.False(node.DeclaredByProject);
        Assert.Equal(["vbeami"], node.RequiredBy);
        Assert.Equal("0.4.*", node.Constraint);
    }

    [Fact]
    public void Build_PackageDeclaredByProjectAndAddons_StaysRootAndListsEveryone() {
        // The project's entry wins (requirement P1-5), so it's a root with the project's
        // constraint. The addons still get an edge each: "who else needs this" is exactly what
        // someone about to remove or re-pin it wants to see.
        var nodes = DotnetGraphBuilder.Build(
            Manifest(Spec("Serilog", "3.*")),
            Lockfile(Locked("Serilog", "3.1.1")),
            [Spec("Serilog", "2.*")],
            new Dictionary<string, IReadOnlyList<string>> {
                ["zeta"] = ["Serilog"],
                ["alpha"] = ["Serilog"]
            });

        var node = Assert.Single(nodes);
        Assert.Equal(GraphNodeOrigin.Root, node.Origin);
        Assert.Equal("3.*", node.Constraint);
        Assert.Equal([GraphConflict.ProjectRequirerName, "alpha", "zeta"], node.RequiredBy);
    }

    [Fact]
    public void Build_LockfileOnlyPackage_IsAnOrphanWithNoRequirers() {
        var nodes = DotnetGraphBuilder.Build(Manifest(), Lockfile(Locked("Ghost.Package", "2.0.0")),
            [], NoAddons);

        var node = Assert.Single(nodes);
        Assert.Equal(GraphNodeOrigin.Orphan, node.Origin);
        Assert.Empty(node.RequiredBy);
        Assert.Null(node.Constraint);
        Assert.Equal("2.0.0", node.Resolved);
    }

    [Fact]
    public void Build_CarriesTheLockedFeedName() {
        var nodes = DotnetGraphBuilder.Build(
            Manifest(Spec("A"), Spec("B")), Lockfile(Locked("A", source: "kemomi"), Locked("B")),
            [], NoAddons);

        Assert.Equal("kemomi", nodes.Single(n => n.Name == "A").Source);
        // Null means nuget.org, the same convention the lockfile itself uses.
        Assert.Null(nodes.Single(n => n.Name == "B").Source);
    }

    [Fact]
    public void Build_AddonDeclaredButNotYetLocked_IsStillShown() {
        // An addon updated its manifest and nothing has re-synced yet. Hiding the package would
        // make the graph disagree with the addon's own file; a node with no resolved version is
        // honest.
        var nodes = DotnetGraphBuilder.Build(
            Manifest(), Lockfile(), [Spec("New.Thing")],
            new Dictionary<string, IReadOnlyList<string>> { ["a"] = ["New.Thing"] });

        var node = Assert.Single(nodes);
        Assert.Equal(GraphNodeOrigin.Transitive, node.Origin);
        Assert.Null(node.Resolved);
    }

    [Fact]
    public void Build_EachPackageAppearsOnce() {
        var nodes = DotnetGraphBuilder.Build(
            Manifest(Spec("A")),
            Lockfile(Locked("A"), Locked("B")),
            [Spec("A"), Spec("B"), Spec("B", "9.*")],
            new Dictionary<string, IReadOnlyList<string>> { ["x"] = ["A", "B"] });

        Assert.Equal(["A", "B"], nodes.Select(n => n.Name));
        Assert.Equal("1.*", nodes.Single(n => n.Name == "B").Constraint);
    }

    [Fact]
    public void Flatten_PicksTheWinnerInOrdinalAddonOrder() {
        // Lockfile mode can't replay the walk's discovery order, so it picks a stable one instead.
        // The same project must show the same constraint on every refresh.
        var declarations = new Dictionary<string,
            IReadOnlyList<DotnetSpec>>(StringComparer.Ordinal) {
            ["zeta"] = [Spec("Shared", "2.*")],
            ["alpha"] = [Spec("Shared", "1.*"), Spec("OnlyAlpha")],
        };

        var (winners, packagesByAddon) = DotnetGraphBuilder.Flatten(declarations);

        Assert.Equal("1.*", winners.Single(w => w.Name == "Shared").Version);
        Assert.Equal(["Shared", "OnlyAlpha"], packagesByAddon["alpha"]);
        Assert.Equal(["Shared"], packagesByAddon["zeta"]);
    }
}
