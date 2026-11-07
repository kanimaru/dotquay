using System.Xml.Linq;
using Dotquay.Core.Dotnet;
using Dotquay.Core.Json;
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
    public void Install_AfterLink_KeepsTheImportHookBelowTheNewPackageReference()
    {
        // The other direction: link first, into a project with no PackageReference ItemGroup yet, then
        // install writes the reference. The writer's new ItemGroup must not land below the hook.
        var fileSystem = new InMemoryFileSystem();
        var csprojPath = fileSystem.CombinePath(ProjectDirectory, "Game.csproj");
        fileSystem.WriteAllText(csprojPath, """
            <Project Sdk="Godot.NET.Sdk/4.3.0">
              <PropertyGroup>
                <TargetFramework>net8.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """);
        fileSystem.WriteAllText(
            fileSystem.CombinePath(GeneratorDirectory, "generator.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");

        LocalTargetsWriter.Sync(fileSystem, ProjectDirectory, Linked(fileSystem, analyzer: true));
        CsprojPackageReferenceWriter.AddOrUpdate(fileSystem, csprojPath, PackageId, "1.0.0");

        var csproj = fileSystem.ReadAllText(csprojPath);

        // Same load-bearing assertion as Sync_TheImportHook_ComesAfterThePackageReferences.
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
    public void Sync_OverAnOverlayFromBeforeTheStagingTarget_ReplacesItWhole()
    {
        // Every command that reaches DependencyWorkflow.SyncLocalTargets (install, add, update, and
        // so link/unlink, which end in an install) rewrites the overlay wholesale. An existing user
        // therefore gets the staging target on their next such command, without relinking.
        var fileSystem = Build();
        var overlayPath = fileSystem.CombinePath(ProjectDirectory, LocalTargetsWriter.FileName);
        fileSystem.WriteAllText(overlayPath, "<Project><!-- written before staging existed --></Project>");

        LocalTargetsWriter.Sync(fileSystem, ProjectDirectory, Linked(fileSystem, analyzer: true));

        var overlay = fileSystem.ReadAllText(overlayPath);
        Assert.Contains(LocalTargetsWriter.AnalyzerStagingTarget, overlay);
        Assert.DoesNotContain("written before staging existed", overlay);
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

    // ---- analyzer staging -------------------------------------------------

    private static XElement Overlay(InMemoryFileSystem fileSystem) =>
        XDocument.Parse(fileSystem.ReadAllText(OverlayPath(fileSystem))).Root!;

    private static XElement? StagingTarget(InMemoryFileSystem fileSystem) =>
        Overlay(fileSystem).Elements("Target").SingleOrDefault(
            t => (string?)t.Attribute("Name") == LocalTargetsWriter.AnalyzerStagingTarget);

    /// <summary>Every attribute value in the staging target, which is where all of its behaviour
    /// lives — there is no element text anywhere in it.</summary>
    private static string StagingAttributes(InMemoryFileSystem fileSystem) =>
        string.Join(
            "\n",
            StagingTarget(fileSystem)!.DescendantsAndSelf()
                .SelectMany(e => e.Attributes().Select(a => a.Value).Append(e.Value)));

    [Fact]
    public void Sync_AnAnalyzerLink_CarriesTheStagingTarget()
    {
        var fileSystem = Build();

        LocalTargetsWriter.Sync(fileSystem, ProjectDirectory, Linked(fileSystem, analyzer: true));

        // A Roslyn host holds a file lock on every analyzer it loads, for as long as it lives. Point
        // one at a generator's own build output and that generator can never be rebuilt again
        // (MSB3061/MSB3026) — which takes the consuming project's build down with it.
        var target = StagingTarget(fileSystem);
        Assert.NotNull(target);
        Assert.Equal("ResolveReferences", (string?)target.Attribute("AfterTargets"));
        Assert.Equal("CoreCompile", (string?)target.Attribute("BeforeTargets"));
    }

    [Fact]
    public void Sync_APlainLibraryLink_HasNoStagingTarget()
    {
        var fileSystem = Build();

        LocalTargetsWriter.Sync(fileSystem, ProjectDirectory, Linked(fileSystem, analyzer: false));

        // Nothing loads an ordinary library reference the way Roslyn loads an analyzer, so there is no
        // lock to dodge and no reason to put a build-time copy in anyone's way.
        Assert.Null(StagingTarget(fileSystem));
    }

    [Fact]
    public void Sync_OneAnalyzerAmongPlainLinks_StillEmitsExactlyOneTarget()
    {
        var fileSystem = Build();
        var links = new LocalLinks(fileSystem);
        links.SetDotnet(ProjectDirectory, PackageId, GeneratorDirectory, analyzer: true);
        links.SetDotnet(ProjectDirectory, "Kani.Core", GeneratorDirectory, analyzer: false);

        LocalTargetsWriter.Sync(fileSystem, ProjectDirectory, links.ReadDotnet(ProjectDirectory));

        // The target operates on @(Analyzer) wholesale, so it is emitted per overlay and not per link.
        // SingleOrDefault in the helper is the assertion that a second copy would trip.
        Assert.NotNull(StagingTarget(fileSystem));
    }

    [Fact]
    public void Sync_TheStagedCopy_KeepsTheAnalyzersOwnFileName()
    {
        var fileSystem = Build();

        LocalTargetsWriter.Sync(fileSystem, ProjectDirectory, Linked(fileSystem, analyzer: true));

        // Roslyn resolves an analyzer by its assembly name. A copy renamed to carry its hash loads as
        // nothing at all — CS8034, a *warning*, so the build goes on quietly emitting less code than
        // it should. The hash has to sit in the directory name, and a fresh directory per content hash
        // is also what makes a rebuilt generator land somewhere nothing has locked yet.
        Assert.Contains(
            "%(Filename)-%(FileHash)\\%(Filename)%(Extension)",
            StagingAttributes(fileSystem));
    }

    [Fact]
    public void Sync_TheStagedCopy_TakesTheAnalyzersNeighboursWithIt()
    {
        var fileSystem = Build();

        LocalTargetsWriter.Sync(fileSystem, ProjectDirectory, Linked(fileSystem, analyzer: true));

        // An analyzer resolves its own dependencies out of the directory it was loaded from, so
        // copying the assembly alone would strand it.
        Assert.Contains(
            "%(_DotquayHashedAnalyzer.RootDir)%(_DotquayHashedAnalyzer.Directory)*",
            StagingAttributes(fileSystem));
    }

    [Fact]
    public void Sync_PackageAndSdkAnalyzers_AreLeftWhereTheyAre()
    {
        var fileSystem = Build();

        LocalTargetsWriter.Sync(fileSystem, ProjectDirectory, Linked(fileSystem, analyzer: true));

        // Nothing rebuilds an analyzer out of the NuGet cache or out of the SDK, so nothing is waiting
        // on its lock — copying those would be pure build cost for no benefit.
        var attributes = StagingAttributes(fileSystem);
        Assert.Contains("StartsWith('$(NuGetPackageRoot)')", attributes);
        Assert.Contains("StartsWith('$(NetCoreRoot)')", attributes);
    }

    [Fact]
    public void Sync_TheStagingTarget_SwapsTheAnalyzerItemForTheCopy()
    {
        var fileSystem = Build();

        LocalTargetsWriter.Sync(fileSystem, ProjectDirectory, Linked(fileSystem, analyzer: true));

        // Staging that does not reach @(Analyzer) is a directory of copied files nobody compiles
        // against — the lock stays on the original and the whole target does nothing.
        var analyzers = StagingTarget(fileSystem)!.Descendants("Analyzer").ToList();
        Assert.Contains(analyzers, a => (string?)a.Attribute("Remove") == "@(_DotquayLinkedAnalyzer)");
        Assert.Contains(analyzers, a => (string?)a.Attribute("Include") == "@(_DotquayStagedAnalyzer)");
    }

    [Fact]
    public void Sync_TheStageRoot_IsResolvedInsideTheTarget()
    {
        var fileSystem = Build();

        LocalTargetsWriter.Sync(fileSystem, ProjectDirectory, Linked(fileSystem, analyzer: true));

        // IntermediateOutputPath is defined by the SDK's .targets, which are imported after this
        // overlay. Read at overlay-evaluation time it is empty, and the staging directory would land
        // beside the .csproj instead of under obj/ — with nothing said about it.
        Assert.Empty(Overlay(fileSystem).Elements("PropertyGroup"));
        Assert.Contains(
            "$([MSBuild]::NormalizePath('$(MSBuildProjectDirectory)', '$(IntermediateOutputPath)', 'linked-analyzers'))",
            StagingAttributes(fileSystem));
    }

    [Fact]
    public void Sync_StaleStagedDirectories_AreSweptButNotTheCurrentOnes()
    {
        var fileSystem = Build();

        LocalTargetsWriter.Sync(fileSystem, ProjectDirectory, Linked(fileSystem, analyzer: true));

        // One directory per generator build, so obj/ grows for as long as the link lives unless they
        // are swept — and sweeping the one this build just staged into would delete what is about to
        // be compiled against.
        var stale = StagingTarget(fileSystem)!.Descendants("_DotquayStaleStage").Single();
        Assert.Contains("System.IO.Directory]::GetDirectories", (string?)stale.Attribute("Include"));
        Assert.Contains("%(Filename)-%(FileHash)", (string?)stale.Attribute("Exclude"));
    }

    [Fact]
    public void Sync_TheSweep_SaysNothingAboutADirectoryAHostStillHolds()
    {
        var fileSystem = Build();

        LocalTargetsWriter.Sync(fileSystem, ProjectDirectory, Linked(fileSystem, analyzer: true));

        var target = StagingTarget(fileSystem)!;

        // A staged directory a Roslyn host still has open cannot be deleted, and that is the normal
        // state while a generator is being developed — not a fault. RemoveDir reports it as warning
        // MSB3231 on every single build, which is why the sweep goes through the shell instead.
        Assert.Empty(target.Descendants("RemoveDir"));
        Assert.Empty(target.Descendants("Delete"));

        var sweeps = target.Descendants("Exec").ToList();
        Assert.Equal(2, sweeps.Count);
        Assert.All(sweeps, e => Assert.Equal("true", (string?)e.Attribute("IgnoreExitCode")));
        Assert.All(sweeps, e => Assert.Equal("low", (string?)e.Attribute("StandardErrorImportance")));

        // One branch per shell, and each guarded so a build with nothing to sweep spawns no process.
        Assert.Contains(sweeps, e => ((string?)e.Attribute("Command"))!.StartsWith("rmdir", StringComparison.Ordinal));
        Assert.Contains(sweeps, e => ((string?)e.Attribute("Command"))!.StartsWith("rm -rf", StringComparison.Ordinal));
        Assert.All(sweeps, e => Assert.Contains("'@(_DotquayStaleStage)' != ''", (string?)e.Attribute("Condition")));
    }

    [Fact]
    public void Sync_TheStagingTarget_StaysOutOfTheCommittedCsproj()
    {
        var fileSystem = Build();

        LocalTargetsWriter.Sync(fileSystem, ProjectDirectory, Linked(fileSystem, analyzer: true));

        // The staging exists because of a link on one machine. In the tracked .csproj it would ask a
        // whole team to carry one person's development setup — and it would outlive the link, since
        // unlinking only deletes the overlay.
        var csproj = fileSystem.ReadAllText(fileSystem.CombinePath(ProjectDirectory, "Game.csproj"));
        Assert.DoesNotContain(LocalTargetsWriter.AnalyzerStagingTarget, csproj);
    }

    [Fact]
    public void Sync_UnlinkingTheAnalyzer_TakesTheStagingWithIt()
    {
        var fileSystem = Build();
        LocalTargetsWriter.Sync(fileSystem, ProjectDirectory, Linked(fileSystem, analyzer: true));

        new LocalLinks(fileSystem).RemoveDotnet(ProjectDirectory, PackageId);
        LocalTargetsWriter.Sync(fileSystem, ProjectDirectory, new LocalLinks(fileSystem).ReadDotnet(ProjectDirectory));

        // Nothing is left behind to shadow-copy analyzers for a link that no longer exists — the
        // overlay is the only place the target was ever written.
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
    public async Task Doctor_AMalformedLocalFile_FailsLikeABrokenLockfile()
    {
        var fileSystem = Build();
        fileSystem.WriteAllText(fileSystem.CombinePath(ProjectDirectory, LocalLinks.FileName), "this is [ not toml\n");

        // Doctor reads dotquay.toml and dotquay.lock for real and fails on either when it doesn't
        // parse (invalid_project_file). The local links file is the third project file; it used to
        // come back as a linked_project_missing issue, which names a link that was never read.
        var exception = await Assert.ThrowsAsync<LocalLinksParseException>(() => DoctorAsync(fileSystem));

        Assert.Contains(LocalLinks.FileName, exception.Message, StringComparison.Ordinal);
        Assert.Equal(JsonErrorKind.InvalidProjectFile, JsonErrorClassifier.Classify(exception).Kind);
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
