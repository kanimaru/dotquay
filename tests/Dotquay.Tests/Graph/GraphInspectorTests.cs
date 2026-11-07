using Dotquay.Core.Graph;
using Dotquay.Core.Json;
using Dotquay.Core.Manifest;
using Dotquay.Core.Resolve;
using Dotquay.Core.Sync;
using Dotquay.Core.Workflows;
using Dotquay.Tests.Support;
using Xunit;

namespace Dotquay.Tests.Graph;

/// <summary>
/// Covers <see cref="GraphInspector"/>'s offline mode — the payload the Godot panel opens with
/// (SPEC.md §18).
/// </summary>
/// <remarks>
/// These tests are all about what the panel would otherwise get wrong. A manifest entry that was
/// never locked has to appear or the panel disagrees with the file the user is editing; an addon
/// nothing requires any more has to be distinguishable from a legitimately transitive one, or
/// "Remove" gets offered for something that isn't there to remove. The resolved mode isn't covered
/// here because it delegates entirely to <see cref="DependencyGraphResolver"/>, which has its own
/// suite.
/// </remarks>
public class GraphInspectorTests
{
    private const string ProjectDirectory = "project";
    private const string AUrl = "https://example.com/a.git";
    private const string BUrl = "https://example.com/b.git";
    private const string CoreUrl = "https://example.com/core.git";

    private static string Sha(char c) => new(c, 40);

    private static (GraphInspector Inspector, InMemoryFileSystem FileSystem) Build(string manifest, string lockfile)
    {
        var fileSystem = new InMemoryFileSystem();
        fileSystem.WriteAllText(fileSystem.CombinePath(ProjectDirectory, "dotquay.toml"), manifest);
        fileSystem.WriteAllText(fileSystem.CombinePath(ProjectDirectory, "dotquay.lock"), lockfile);

        return (new GraphInspector(fileSystem, new FakeGitClient(), "cache"), fileSystem);
    }

    private static readonly string DiamondManifest = $"""
        [project]
        godot = "4.7"

        [addons.a]
        git = "{AUrl}"
        version = "~1.0"

        [addons.b]
        git = "{BUrl}"
        version = "~2.0"
        """;

    private static readonly string DiamondLockfile = $"""
        [[addon]]
        name = "a"
        git = "{AUrl}"
        resolved = "{Sha('a')}"
        version = "v1.0.0"
        path = "addons/a"
        dependencies = ["core"]

        [[addon]]
        name = "b"
        git = "{BUrl}"
        resolved = "{Sha('b')}"
        version = "v2.0.0"
        path = "addons/b"
        dependencies = ["core"]

        [[addon]]
        name = "core"
        git = "{CoreUrl}"
        resolved = "{Sha('c')}"
        version = "v1.4.2"
        path = "addons/core"
        """;

    [Fact]
    public void Inspect_ReportsTheLockfileAsItsSource()
    {
        var (inspector, _) = Build(DiamondManifest, DiamondLockfile);

        var report = inspector.Inspect(ProjectDirectory);

        // The panel says "press Re-resolve to check for conflicts" on this mode, because a lockfile
        // by definition only exists once conflicts were already settled.
        Assert.Equal(GraphSource.Lockfile, report.Source);
        Assert.Empty(report.Conflicts);
    }

    [Fact]
    public void Inspect_ADiamondIsOneSharedNode()
    {
        var (inspector, _) = Build(DiamondManifest, DiamondLockfile);

        var report = inspector.Inspect(ProjectDirectory);

        Assert.Equal(3, report.Addons.Count);
        Assert.Single(report.Addons, a => a.Name == "core");
    }

    [Fact]
    public void Inspect_DistinguishesRootsFromTransitiveAddons()
    {
        var (inspector, _) = Build(DiamondManifest, DiamondLockfile);

        var report = inspector.Inspect(ProjectDirectory);

        Assert.Equal(GraphNodeOrigin.Root, report.Addons.Single(a => a.Name == "a").Origin);
        Assert.Equal(GraphNodeOrigin.Transitive, report.Addons.Single(a => a.Name == "core").Origin);
    }

    [Fact]
    public void Inspect_FlagsAnUnreachableLockedAddonAsAnOrphan()
    {
        // Nothing declares or depends on "ghost" any more — the drift `doctor` reports as a dangling
        // entry. Calling it transitive would tell the user something still needs it.
        var lockfile = DiamondLockfile + $"""

            [[addon]]
            name = "ghost"
            git = "{CoreUrl}"
            resolved = "{Sha('d')}"
            path = "addons/ghost"
            """;

        var (inspector, _) = Build(DiamondManifest, lockfile);

        var report = inspector.Inspect(ProjectDirectory);

        Assert.Equal(GraphNodeOrigin.Orphan, report.Addons.Single(a => a.Name == "ghost").Origin);
    }

    [Fact]
    public void Inspect_BuildsProjectEdgesForEveryRoot()
    {
        var (inspector, _) = Build(DiamondManifest, DiamondLockfile);

        var report = inspector.Inspect(ProjectDirectory);

        Assert.Contains(report.Edges, e => e.From == GraphConflict.ProjectRequirerName && e.To == "a");
        Assert.Contains(report.Edges, e => e.From == GraphConflict.ProjectRequirerName && e.To == "b");
    }

    [Fact]
    public void Inspect_BuildsBothSidesOfADiamond()
    {
        var (inspector, _) = Build(DiamondManifest, DiamondLockfile);

        var report = inspector.Inspect(ProjectDirectory);

        Assert.Contains(report.Edges, e => e.From == "a" && e.To == "core");
        Assert.Contains(report.Edges, e => e.From == "b" && e.To == "core");
    }

    [Fact]
    public void Inspect_DropsEdgesToNamesWithNoNode()
    {
        // A lockfile can name a dependency that isn't locked (a hand-edit, or an interrupted install).
        // Drawing an arrow into nothing is worse than admitting the child isn't in this picture.
        var lockfile = $"""
            [[addon]]
            name = "a"
            git = "{AUrl}"
            resolved = "{Sha('a')}"
            path = "addons/a"
            dependencies = ["missing_child"]
            """;

        var manifest = $"""
            [addons.a]
            git = "{AUrl}"
            version = "~1.0"
            """;

        var (inspector, _) = Build(manifest, lockfile);

        var report = inspector.Inspect(ProjectDirectory);

        Assert.DoesNotContain(report.Edges, e => e.To == "missing_child");
    }

    [Fact]
    public void Inspect_IncludesAManifestEntryThatWasNeverLocked()
    {
        var manifest = DiamondManifest + $"""

            [addons.newcomer]
            git = "{CoreUrl}"
            version = "~3.0"
            """;

        var (inspector, _) = Build(manifest, DiamondLockfile);

        var report = inspector.Inspect(ProjectDirectory);

        // Omitting it would make the panel silently disagree with the dotquay.toml on screen.
        var node = report.Addons.Single(a => a.Name == "newcomer");
        Assert.Null(node.ResolvedSha);
        Assert.Null(node.Status);
        Assert.Equal("~3.0", node.Constraint);
    }

    [Fact]
    public void Inspect_ReportsAnAddonWithNoFolderAsMissing()
    {
        var (inspector, _) = Build(DiamondManifest, DiamondLockfile);

        var report = inspector.Inspect(ProjectDirectory);

        Assert.Equal(AddonStatusKind.Missing, report.Addons.Single(a => a.Name == "b").Status);
    }

    [Fact]
    public void Inspect_ReportsAnUnrecordedFolderAsModified()
    {
        var (inspector, fileSystem) = Build(DiamondManifest, DiamondLockfile);
        // On disk, but the lockfile has no content_hash for it — a folder dotquay never installed
        // (SPEC.md §9 step 4 treats that as dirty rather than silently adopting it), so the panel
        // must not badge it as clean.
        fileSystem.WriteAllText(fileSystem.CombinePath(ProjectDirectory, "addons", "a", "plugin.cfg"), "cfg");

        var report = inspector.Inspect(ProjectDirectory);

        Assert.Equal(AddonStatusKind.Modified, report.Addons.Single(a => a.Name == "a").Status);
    }

    [Fact]
    public void Inspect_ReportsAMatchingTreeAsClean()
    {
        var fileSystem = new InMemoryFileSystem();
        var installedPath = fileSystem.CombinePath(ProjectDirectory, "addons", "a");
        fileSystem.WriteAllText(fileSystem.CombinePath(installedPath, "plugin.cfg"), "cfg");

        // The same hash the installer would have recorded — computed rather than hard-coded, so this
        // stays true if the hashing scheme ever changes.
        var contentHash = ContentHasher.HashDirectory(fileSystem, installedPath);

        var lockfile = $"""
            [[addon]]
            name = "a"
            git = "{AUrl}"
            resolved = "{Sha('a')}"
            version = "v1.0.0"
            path = "addons/a"
            content_hash = "{contentHash}"
            """;

        var manifest = $"""
            [addons.a]
            git = "{AUrl}"
            version = "~1.0"
            """;

        fileSystem.WriteAllText(fileSystem.CombinePath(ProjectDirectory, "dotquay.toml"), manifest);
        fileSystem.WriteAllText(fileSystem.CombinePath(ProjectDirectory, "dotquay.lock"), lockfile);

        var report = new GraphInspector(fileSystem, new FakeGitClient(), "cache").Inspect(ProjectDirectory);

        Assert.Equal(AddonStatusKind.Clean, report.Addons.Single(a => a.Name == "a").Status);
    }

    [Fact]
    public void Inspect_MarksOverriddenAddons()
    {
        var manifest = DiamondManifest + $"""

            [overrides.core]
            git = "{CoreUrl}"
            rev = "{Sha('c')}"
            """;

        var (inspector, _) = Build(manifest, DiamondLockfile);

        var report = inspector.Inspect(ProjectDirectory);

        Assert.True(report.Addons.Single(a => a.Name == "core").IsOverridden);
        Assert.Contains("core", report.Overrides);
    }

    [Fact]
    public void Inspect_AnOverrideSuppliesTheConstraintForATransitiveAddon()
    {
        var manifest = DiamondManifest + $"""

            [overrides.core]
            git = "{CoreUrl}"
            version = "~1.4"
            """;

        var (inspector, _) = Build(manifest, DiamondLockfile);

        var report = inspector.Inspect(ProjectDirectory);

        // Without the override, "core" has no manifest entry and therefore no constraint to show.
        var core = report.Addons.Single(a => a.Name == "core");
        Assert.Equal("~1.4", core.Constraint);
        Assert.Equal(AddonSourceKind.Version, core.SourceKind);
    }

    [Fact]
    public void Inspect_ReportsProjectDeclaredAndAddonDeclaredNuGetSeparately()
    {
        var manifest = DiamondManifest + """

            [dotnet.Newtonsoft.Json]
            version = "13.0.*"
            """;

        var lockfile = DiamondLockfile + """

            [[dotnet]]
            name = "Newtonsoft.Json"
            resolved = "13.0.3"

            [[dotnet]]
            name = "Serilog"
            resolved = "3.1.1"
            """;

        var (inspector, _) = Build(manifest, lockfile);

        var report = inspector.Inspect(ProjectDirectory);

        var declared = report.Dotnet.Single(d => d.Name == "Newtonsoft.Json");
        Assert.True(declared.DeclaredByProject);
        Assert.Equal("13.0.3", declared.Resolved);

        // Locked but not in the manifest: unioned in by an addon (requirement P1-5), so the panel
        // must not offer to remove it from a manifest it isn't in.
        Assert.False(report.Dotnet.Single(d => d.Name == "Serilog").DeclaredByProject);
    }

    /// <summary>The diamond, plus one NuGet package of each origin: <c>Newtonsoft.Json</c> declared
    /// by the project, <c>Serilog</c> declared by addon <c>a</c>'s installed package manifest, and
    /// <c>Ghost.Package</c> that only the lockfile still mentions.</summary>
    private static GraphReport InspectWithNuGet(
        string? aPackageManifest, string extraLockfile = "", string manifestExtra = "")
    {
        var manifest = DiamondManifest + manifestExtra + """

            [dotnet.Newtonsoft.Json]
            version = "13.0.*"
            """;

        var lockfile = DiamondLockfile + extraLockfile + """

            [[dotnet]]
            name = "Newtonsoft.Json"
            resolved = "13.0.3"

            [[dotnet]]
            name = "Serilog"
            resolved = "3.1.1"
            source = "kemomi"

            [[dotnet]]
            name = "Ghost.Package"
            resolved = "2.0.0"
            """;

        var (inspector, fileSystem) = Build(manifest, lockfile);
        if (aPackageManifest is not null)
        {
            fileSystem.WriteAllText(fileSystem.CombinePath(ProjectDirectory, "addons", "a", "dotquay.toml"), aPackageManifest);
        }

        return inspector.Inspect(ProjectDirectory);
    }

    private const string ADeclaresSerilog = """
        [package]
        name = "a"
        version = "1.0.0"

        [dotnet.Serilog]
        version = "3.*"
        """;

    [Fact]
    public void Inspect_AddonDeclaredNuGet_NamesTheAddonThatDeclaredIt()
    {
        // The whole point of v0.19: the default, offline view used to know only "not the project's",
        // which the panel could render as nothing better than "needed by an addon".
        var report = InspectWithNuGet(ADeclaresSerilog);

        var serilog = report.Dotnet.Single(d => d.Name == "Serilog");
        Assert.Equal(GraphNodeOrigin.Transitive, serilog.Origin);
        Assert.Equal(["a"], serilog.RequiredBy);
        Assert.Equal("3.*", serilog.Constraint);
        Assert.Equal("kemomi", serilog.Source);
    }

    [Fact]
    public void Inspect_ProjectDeclaredNuGet_IsARootRequiredByTheProject()
    {
        var report = InspectWithNuGet(ADeclaresSerilog);

        var json = report.Dotnet.Single(d => d.Name == "Newtonsoft.Json");
        Assert.Equal(GraphNodeOrigin.Root, json.Origin);
        Assert.Equal([GraphConflict.ProjectRequirerName], json.RequiredBy);
    }

    [Fact]
    public void Inspect_NuGetNothingDeclares_IsAnOrphan()
    {
        var report = InspectWithNuGet(ADeclaresSerilog);

        var ghost = report.Dotnet.Single(d => d.Name == "Ghost.Package");
        Assert.Equal(GraphNodeOrigin.Orphan, ghost.Origin);
        Assert.Empty(ghost.RequiredBy);
    }

    [Fact]
    public void Inspect_AddonWithNoPackageManifest_VouchesForNothing()
    {
        // Without a manifest on disk, nothing claims Serilog is needed. Showing it as an orphan is the
        // same honest answer doctor gives, and the two must not disagree.
        var report = InspectWithNuGet(aPackageManifest: null);

        Assert.Equal(GraphNodeOrigin.Orphan, report.Dotnet.Single(d => d.Name == "Serilog").Origin);
    }

    [Fact]
    public void Inspect_MalformedPackageManifest_ReadsAsDeclaringNothing()
    {
        // A read-only view must not fail over a broken addon file. doctor's
        // package_manifest_unreadable check reports it, once.
        var report = InspectWithNuGet("this is [not toml");

        Assert.Equal(GraphNodeOrigin.Orphan, report.Dotnet.Single(d => d.Name == "Serilog").Origin);
    }

    [Fact]
    public void Inspect_UnreachableAddon_DoesNotVouchForItsPackages()
    {
        // 'ghost' is still on disk and still declares Serilog, but nothing requires 'ghost' any more.
        // Letting an orphaned addon keep its package alive would hide exactly the drift an orphan
        // badge exists to show.
        var lockfile = $"""

            [[addon]]
            name = "ghost"
            git = "{CoreUrl}"
            resolved = "{Sha('d')}"
            path = "addons/ghost"
            """;

        var manifest = DiamondManifest + """

            [dotnet.Newtonsoft.Json]
            version = "13.0.*"
            """;

        var (inspector, fileSystem) = Build(manifest, DiamondLockfile + lockfile + """

            [[dotnet]]
            name = "Serilog"
            resolved = "3.1.1"
            """);
        fileSystem.WriteAllText(
            fileSystem.CombinePath(ProjectDirectory, "addons", "ghost", "dotquay.toml"),
            ADeclaresSerilog.Replace("name = \"a\"", "name = \"ghost\""));

        var report = inspector.Inspect(ProjectDirectory);

        Assert.Equal(GraphNodeOrigin.Orphan, report.Dotnet.Single(d => d.Name == "Serilog").Origin);
    }

    [Fact]
    public void Inspect_ReadsThePackageManifestFromTheInstallAsFolder()
    {
        // 'a' installs into addons/renamed_a. Its package manifest lives there, not in addons/a, and a
        // name-derived path would quietly find nothing.
        var lockfile = DiamondLockfile.Replace(
            "path = \"addons/a\"", "path = \"addons/a\"\nfolder = \"renamed_a\"");

        var (inspector, fileSystem) = Build(DiamondManifest, lockfile + """

            [[dotnet]]
            name = "Serilog"
            resolved = "3.1.1"
            """);
        fileSystem.WriteAllText(
            fileSystem.CombinePath(ProjectDirectory, "addons", "renamed_a", "dotquay.toml"), ADeclaresSerilog);

        var report = inspector.Inspect(ProjectDirectory);

        var serilog = report.Dotnet.Single(d => d.Name == "Serilog");
        Assert.Equal(GraphNodeOrigin.Transitive, serilog.Origin);
        Assert.Equal(["a"], serilog.RequiredBy);
    }

    [Fact]
    public void ToJson_CarriesNuGetOriginRequirersAndSource()
    {
        var payload = JsonMapper.ToJson(InspectWithNuGet(ADeclaresSerilog));

        var serilog = payload.Dotnet.Single(d => d.Name == "Serilog");
        Assert.Equal("transitive", serilog.Origin);
        Assert.Equal(["a"], serilog.RequiredBy);
        Assert.Equal("kemomi", serilog.Source);

        Assert.Equal("orphan", payload.Dotnet.Single(d => d.Name == "Ghost.Package").Origin);
    }

    [Fact]
    public void Inspect_EmptyProject_IsNotAnError()
    {
        var (inspector, _) = Build("[project]\ngodot = \"4.7\"\n", "");

        var report = inspector.Inspect(ProjectDirectory);

        Assert.Empty(report.Addons);
        Assert.Empty(report.Edges);
        Assert.Empty(report.RootNames);
    }

    [Fact]
    public void Inspect_WithNoManifest_Throws()
    {
        var fileSystem = new InMemoryFileSystem();
        var inspector = new GraphInspector(fileSystem, new FakeGitClient(), "cache");

        var exception = Assert.Throws<WorkflowException>(() => inspector.Inspect(ProjectDirectory));

        // The panel keys its "Create dotquay.toml" button off this exact error kind.
        Assert.Contains("Run 'dotquay init' first", exception.Message);
    }

    [Fact]
    public void ToJson_ProducesTheContractTheGodotPluginParses()
    {
        var (inspector, _) = Build(DiamondManifest, DiamondLockfile);

        var payload = JsonMapper.ToJson(inspector.Inspect(ProjectDirectory));

        Assert.Equal("lockfile", payload.Source);
        Assert.Equal(["a", "b"], payload.Roots);
        Assert.False(payload.HasConflicts);

        var core = payload.Addons.Single(a => a.Name == "core");
        Assert.Equal("transitive", core.Origin);
        // Precomputed so every consumer abbreviates a SHA the same way the CLI prints it.
        Assert.Equal(Sha('c')[..7], core.ShortSha);
    }
}
