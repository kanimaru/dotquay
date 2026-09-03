using Dotquay.Core.Local;
using Dotquay.Core.Workflows;
using Dotquay.Tests.Support;
using Xunit;

namespace Dotquay.Tests.Local;

/// <summary>
/// Covers local .NET links (docs/DESIGN-v0.11-LOCAL-DOTNET-LINK.md) — developing a shared library or
/// source generator in place while a project consumes it as a NuGet package.
/// </summary>
/// <remarks>
/// The test that matters most here is <see cref="Sync_TheImportHook_ComesAfterThePackageReferences"/>.
/// MSBuild evaluates a project in order and <c>&lt;PackageReference Remove&gt;</c> silently no-ops
/// against an item that has not been declared yet, so an overlay imported too early leaves the build
/// quietly using the published package — no error, no warning, wrong answer. That ordering is this
/// feature's equivalent of the addon link's "never recurse through a directory link" rule.
/// </remarks>
public class LocalTargetsTests
{
    private const string ProjectDirectory = "project";
    private const string GeneratorDirectory = "elsewhere/generator";
    private const string PackageId = "dev.kani.godot.generator";

    private const string Csproj = """
        <Project Sdk="Godot.NET.Sdk/4.3.0">
          <PropertyGroup>
            <TargetFramework>net8.0</TargetFramework>
          </PropertyGroup>
          <ItemGroup>
            <PackageReference Include="dev.kani.godot.generator" Version="1.0.0" />
          </ItemGroup>
        </Project>
        """;

    private static InMemoryFileSystem Build()
    {
        var fileSystem = new InMemoryFileSystem();
        fileSystem.WriteAllText(fileSystem.CombinePath(ProjectDirectory, "Game.csproj"), Csproj);
        fileSystem.WriteAllText(
            fileSystem.CombinePath(GeneratorDirectory, "generator.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        return fileSystem;
    }

    private static string OverlayPath(InMemoryFileSystem fileSystem) =>
        fileSystem.CombinePath(ProjectDirectory, LocalTargetsWriter.FileName);

    private static IReadOnlyDictionary<string, DotnetLink> Linked(InMemoryFileSystem fileSystem, bool analyzer)
    {
        var links = new LocalLinks(fileSystem);
        links.SetDotnet(ProjectDirectory, PackageId, GeneratorDirectory, analyzer);
        return links.ReadDotnet(ProjectDirectory);
    }

    // ---- schema -----------------------------------------------------------

    [Fact]
    public void SetDotnet_AFolder_StoresTheProjectFileInside()
    {
        var fileSystem = Build();

        var link = Linked(fileSystem, analyzer: true)[PackageId];

        // Resolved once, at link time — so nothing downstream has to re-derive which project a folder
        // meant, and a folder that later grows a second .csproj can't change what the link points at.
        Assert.EndsWith("generator.csproj", link.ProjectPath);
        Assert.True(link.Analyzer);
    }

    [Fact]
    public void SetDotnet_ACsprojDirectly_IsAlsoAccepted()
    {
        var fileSystem = Build();
        var links = new LocalLinks(fileSystem);

        links.SetDotnet(ProjectDirectory, PackageId, fileSystem.CombinePath(GeneratorDirectory, "generator.csproj"), analyzer: false);

        Assert.EndsWith("generator.csproj", links.ReadDotnet(ProjectDirectory)[PackageId].ProjectPath);
    }

    [Fact]
    public void SetDotnet_SomethingThatIsNeither_IsRefused()
    {
        var fileSystem = Build();
        var links = new LocalLinks(fileSystem);

        Assert.Throws<LocalLinkException>(
            () => links.SetDotnet(ProjectDirectory, PackageId, "elsewhere/nothing-here", analyzer: false));
    }

    [Fact]
    public void ReadAll_AddonAndDotnetLinks_AreKeptApart()
    {
        var fileSystem = Build();
        fileSystem.WriteAllText(fileSystem.CombinePath("elsewhere/my-addon", "plugin.cfg"), "[plugin]");

        var links = new LocalLinks(fileSystem);
        links.Set(ProjectDirectory, "my-addon", "elsewhere/my-addon");
        links.SetDotnet(ProjectDirectory, PackageId, GeneratorDirectory, analyzer: true);

        var all = links.ReadAll(ProjectDirectory);

        // The namespacing exists so an addon and a package that happen to share a name cannot collide.
        Assert.Single(all.Addons);
        Assert.Single(all.Dotnet);
    }

    [Fact]
    public void ReadAll_TheV06FlatShape_StillReadsAsAnAddonLink()
    {
        var fileSystem = Build();
        fileSystem.WriteAllText(fileSystem.CombinePath(ProjectDirectory, LocalLinks.FileName), """
            [link.my-addon]
            path = "F:/work/my-addon"
            """);

        var all = new LocalLinks(fileSystem).ReadAll(ProjectDirectory);

        // The file never leaves one machine, so there is no migration to coordinate — but silently
        // forgetting an existing link would leave someone staring at an addon that stopped being
        // linked with nothing said about why.
        Assert.Equal("F:/work/my-addon", all.Addons["my-addon"].Path);
    }

    [Fact]
    public void SetDotnet_ADottedPackageId_SurvivesARoundTrip()
    {
        var fileSystem = Build();
        var links = new LocalLinks(fileSystem);

        links.SetDotnet(ProjectDirectory, "Kani.Core", GeneratorDirectory, analyzer: false);

        // Unquoted, [link.dotnet.Kani.Core] would be two nested tables and read back as a package
        // called "Core" — the same trap [dotnet.<PackageId>] has in the manifest.
        Assert.True(links.ReadDotnet(ProjectDirectory).ContainsKey("Kani.Core"));
    }

    // ---- the overlay ------------------------------------------------------

    [Fact]
    public void Sync_WritesTheSwap()
    {
        var fileSystem = Build();

        LocalTargetsWriter.Sync(fileSystem, ProjectDirectory, Linked(fileSystem, analyzer: true));

        var overlay = fileSystem.ReadAllText(OverlayPath(fileSystem));
        Assert.Contains($"<PackageReference Remove=\"{PackageId}\"", overlay);
        Assert.Contains("<ProjectReference Include=", overlay);
    }

    [Fact]
    public void Sync_AnAnalyzerLink_CarriesBothAttributes()
    {
        var fileSystem = Build();

        LocalTargetsWriter.Sync(fileSystem, ProjectDirectory, Linked(fileSystem, analyzer: true));

        // Without OutputItemType the generator is referenced as an ordinary library and never runs,
        // producing no error and no output — the failure this flag exists to prevent.
        var overlay = fileSystem.ReadAllText(OverlayPath(fileSystem));
        Assert.Contains("OutputItemType=\"Analyzer\"", overlay);
        Assert.Contains("ReferenceOutputAssembly=\"false\"", overlay);
    }

    [Fact]
    public void Sync_APlainLibraryLink_CarriesNeither()
    {
        var fileSystem = Build();

        LocalTargetsWriter.Sync(fileSystem, ProjectDirectory, Linked(fileSystem, analyzer: false));

        // A shared library is meant to be compiled against; the analyzer attributes would break it.
        var overlay = fileSystem.ReadAllText(OverlayPath(fileSystem));
        Assert.DoesNotContain("OutputItemType", overlay);
        Assert.DoesNotContain("ReferenceOutputAssembly", overlay);
    }

    [Fact]
    public void Sync_LeavesTheCommittedCsprojAlmostAlone()
    {
        var fileSystem = Build();

        LocalTargetsWriter.Sync(fileSystem, ProjectDirectory, Linked(fileSystem, analyzer: true));

        // The whole point of the overlay: the tracked file keeps its PackageReference, so no absolute
        // machine-local path can ever reach git.
        var csproj = fileSystem.ReadAllText(fileSystem.CombinePath(ProjectDirectory, "Game.csproj"));
        Assert.Contains("<PackageReference Include=\"dev.kani.godot.generator\"", csproj);
        Assert.DoesNotContain("ProjectReference", csproj);
    }

    [Fact]
    public void Sync_TheImportHook_ComesAfterThePackageReferences()
    {
        var fileSystem = Build();

        LocalTargetsWriter.Sync(fileSystem, ProjectDirectory, Linked(fileSystem, analyzer: true));

        var csproj = fileSystem.ReadAllText(fileSystem.CombinePath(ProjectDirectory, "Game.csproj"));

        // THE load-bearing assertion. MSBuild evaluates in order, and `PackageReference Remove` is a
        // silent no-op against an item declared later — so an import placed above the ItemGroup would
        // leave the build using the published package with nothing said. If this ever fails, the
        // feature is broken in the one way that produces no error message.
        Assert.True(
            csproj.IndexOf("<Import", StringComparison.Ordinal)
            > csproj.LastIndexOf("<PackageReference", StringComparison.Ordinal),
            "the overlay import must come after the PackageReference items, or the Remove silently does nothing");
    }

    [Fact]
    public void Sync_RunTwice_DoesNotDuplicateTheHook()
    {
        var fileSystem = Build();
        var links = Linked(fileSystem, analyzer: true);

        LocalTargetsWriter.Sync(fileSystem, ProjectDirectory, links);
        LocalTargetsWriter.Sync(fileSystem, ProjectDirectory, links);

        var csproj = fileSystem.ReadAllText(fileSystem.CombinePath(ProjectDirectory, "Game.csproj"));
        Assert.Equal(1, csproj.Split("<Import").Length - 1);
    }

    [Fact]
    public void Sync_NoLinks_DeletesTheOverlayButKeepsTheHook()
    {
        var fileSystem = Build();
        LocalTargetsWriter.Sync(fileSystem, ProjectDirectory, Linked(fileSystem, analyzer: true));

        LocalTargetsWriter.Sync(fileSystem, ProjectDirectory, new Dictionary<string, DotnetLink>());

        Assert.False(fileSystem.FileExists(OverlayPath(fileSystem)));

        // The hook is inert without the overlay (Condition="Exists"), and removing it would churn a
        // committed file on every link/unlink cycle — for teammates too.
        var csproj = fileSystem.ReadAllText(fileSystem.CombinePath(ProjectDirectory, "Game.csproj"));
        Assert.Contains("<Import", csproj);
    }

    [Fact]
    public void Sync_NoLinksAndNoCsproj_IsANoOp()
    {
        var fileSystem = new InMemoryFileSystem();
        fileSystem.WriteAllText(fileSystem.CombinePath(ProjectDirectory, "dotquay.toml"), "");

        // A GDScript-only project must not be asked for a .csproj it has no reason to have.
        LocalTargetsWriter.Sync(fileSystem, ProjectDirectory, new Dictionary<string, DotnetLink>());

        Assert.False(fileSystem.FileExists(OverlayPath(fileSystem)));
    }

    // ---- doctor -----------------------------------------------------------

    private static async Task<IReadOnlyList<DoctorIssue>> DoctorAsync(InMemoryFileSystem fileSystem)
    {
        fileSystem.WriteAllText(fileSystem.CombinePath(ProjectDirectory, "dotquay.toml"), $"""
            [dotnet."{PackageId}"]
            version = "1.0.0"
            """);
        fileSystem.WriteAllText(fileSystem.CombinePath(ProjectDirectory, "dotquay.lock"), $"""
            [[dotnet]]
            name = "{PackageId}"
            resolved = "1.0.0"
            """);

        var result = await new DependencyWorkflow(fileSystem, new FakeGitClient(), cacheRoot: "cache")
            .DoctorAsync(ProjectDirectory);

        return result.Issues;
    }

    [Fact]
    public async Task Doctor_ALinkWhoseProjectIsGone_IsReported()
    {
        var fileSystem = Build();
        var link = Linked(fileSystem, analyzer: true)[PackageId];
        LocalTargetsWriter.Sync(fileSystem, ProjectDirectory, new Dictionary<string, DotnetLink> { [PackageId] = link });
        fileSystem.DeleteFile(link.ProjectPath);

        var issues = await DoctorAsync(fileSystem);

        // Otherwise this surfaces as an MSBuild error about a path, with nothing pointing at the link
        // that named it.
        Assert.Contains(issues, i => i.Kind == DoctorIssueKind.LinkedProjectMissing);
    }

    [Fact]
    public async Task Doctor_AnOverlayNobodyImports_IsReported()
    {
        var fileSystem = Build();
        LocalTargetsWriter.Sync(fileSystem, ProjectDirectory, Linked(fileSystem, analyzer: true));

        // Someone dropped the import — a hand-edit, or a merge that took the other side.
        fileSystem.WriteAllText(fileSystem.CombinePath(ProjectDirectory, "Game.csproj"), Csproj);

        var issues = await DoctorAsync(fileSystem);

        // The link now does nothing at all and the build silently uses the published package. This is
        // the one failure mode the overlay design has that the .csproj-editing design didn't.
        Assert.Contains(issues, i => i.Kind == DoctorIssueKind.LocalOverlayNotImported);
    }

    [Fact]
    public async Task Doctor_AHealthyLink_IsClean()
    {
        var fileSystem = Build();
        LocalTargetsWriter.Sync(fileSystem, ProjectDirectory, Linked(fileSystem, analyzer: true));

        var issues = await DoctorAsync(fileSystem);

        Assert.DoesNotContain(
            issues,
            i => i.Kind is DoctorIssueKind.LinkedProjectMissing or DoctorIssueKind.LocalOverlayNotImported);
    }
}
