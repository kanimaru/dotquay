using Dotquay.Core.Abstractions;
using Dotquay.Core.Dotnet;
using Dotquay.Core.Sync;
using Dotquay.Core.Workflows;
using Dotquay.Tests.Support;
using Xunit;

namespace Dotquay.Tests.Dotnet;

/// <summary>
/// Covers the source-generator question on <c>add</c> (v0.14) and the two gates in front of it.
/// </summary>
/// <remarks>
/// The behaviour worth protecting is mostly <em>silence</em>. The question exists because a generator
/// referenced with a plain <c>PackageReference</c> really does flow on to downstream consumers
/// (measured 2026-09-10, three levels deep), so a published library may want to contain it. But for a
/// Godot game nothing depends on the project, the answer has no consequence, and a prompt on every
/// generator added to every game is how people learn to dismiss prompts. So: asked only when the
/// author declared the package a development dependency <em>and</em> the project looks published, and
/// the only thing ever written is the containment.
/// </remarks>
public class GeneratorAssetTests {
    private const string ProjectDir = "project";
    private const string GeneratorId = "Dev.Kani.Godot.Generator";

    /// <summary>A Godot game: the end of the line, nothing consumes it as a package.</summary>
    private const string GameCsproj = """
        <Project Sdk="Godot.NET.Sdk/4.7.2">
          <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
          </PropertyGroup>
        </Project>
        """;

    /// <summary>A library other projects depend on, so containment is a real choice.</summary>
    private const string LibraryCsproj = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
            <PackageId>VBeami.Core</PackageId>
          </PropertyGroup>
        </Project>
        """;

    /// <summary>Records what it was asked, so a test can assert the question was never put at all —
    /// which is the actual contract for the common case.</summary>
    private sealed class RecordingDecision(bool keepPrivate) : IGeneratorAssetDecision {
        public List<GeneratorAssetRequest> Requests { get; } = [];

        public bool KeepPrivate(GeneratorAssetRequest request) {
            Requests.Add(request);
            return keepPrivate;
        }
    }

    private static (DependencyWorkflow Workflow, InMemoryFileSystem FileSystem, FakeNuGetClient NuGetClient)
        Build(string csproj, IGeneratorAssetDecision decision) {
        var fileSystem = new InMemoryFileSystem();
        fileSystem.WriteAllText($"{ProjectDir}/dotquay.toml", "# Dotquay project manifest.\n");
        fileSystem.WriteAllText($"{ProjectDir}/Game.csproj", csproj);

        var gitClient = new FakeGitClient { FileSystemForFetchedContent = fileSystem };
        var nuGetClient = new FakeNuGetClient();
        var runner = new FakeDotnetCliRunner();

        var addonInstaller = new AddonInstaller(
            fileSystem, new RepoCache(fileSystem, gitClient, "cache"), new SyncEngine(fileSystem), new FakePatchTool(fileSystem));

        var workflow = new DependencyWorkflow(
            fileSystem,
            gitClient,
            new FakeNuGetClientFactory(nuGetClient),
            addonInstaller,
            new DotnetInstaller(fileSystem, runner, TestCredentials.None())) {
            GeneratorAssetDecision = decision,
        };

        return (workflow, fileSystem, nuGetClient);
    }

    private static string ReadManifest(InMemoryFileSystem fileSystem) =>
        fileSystem.ReadAllText($"{ProjectDir}/dotquay.toml");

    private static string ReadCsproj(InMemoryFileSystem fileSystem) =>
        fileSystem.ReadAllText($"{ProjectDir}/Game.csproj");

    /// <summary>The common case, and the one a regression would be most annoying in.</summary>
    [Fact]
    public async Task AGameIsNeverAsked() {
        var decision = new RecordingDecision(keepPrivate: true);
        var (workflow, fileSystem, nuGetClient) = Build(GameCsproj, decision);
        nuGetClient.VersionsByPackageId[GeneratorId] = ["0.0.5"];
        nuGetClient.DevelopmentDependencyPackageIds.Add(GeneratorId);

        await workflow.AddDotnetAsync(ProjectDir, GeneratorId);

        Assert.Empty(decision.Requests);
        Assert.DoesNotContain("private_assets", ReadManifest(fileSystem));
        Assert.DoesNotContain("PrivateAssets", ReadCsproj(fileSystem));
    }

    [Fact]
    public async Task AnOrdinaryLibraryIsNeverAsked() {
        var decision = new RecordingDecision(keepPrivate: true);
        var (workflow, fileSystem, nuGetClient) = Build(LibraryCsproj, decision);
        nuGetClient.VersionsByPackageId["Newtonsoft.Json"] = ["13.0.3"];

        await workflow.AddDotnetAsync(ProjectDir, "Newtonsoft.Json");

        Assert.Empty(decision.Requests);
        Assert.DoesNotContain("private_assets", ReadManifest(fileSystem));
    }

    /// <summary>A feed that will not serve a nuspec has said nothing, and "no signal" must not be read
    /// as "generator" — otherwise the question fires on every package from that feed.</summary>
    [Fact]
    public async Task AnUnreadableNuspecIsNotTreatedAsAGenerator() {
        var decision = new RecordingDecision(keepPrivate: true);
        var (workflow, _, nuGetClient) = Build(LibraryCsproj, decision);
        nuGetClient.VersionsByPackageId[GeneratorId] = ["0.0.5"];
        nuGetClient.UnreadableNuspecPackageIds.Add(GeneratorId);

        await workflow.AddDotnetAsync(ProjectDir, GeneratorId);

        Assert.Empty(decision.Requests);
    }

    [Fact]
    public async Task APublishedLibraryIsAskedAndToldWhy() {
        var decision = new RecordingDecision(keepPrivate: false);
        var (workflow, _, nuGetClient) = Build(LibraryCsproj, decision);
        nuGetClient.VersionsByPackageId[GeneratorId] = ["0.0.5"];
        nuGetClient.DevelopmentDependencyPackageIds.Add(GeneratorId);

        await workflow.AddDotnetAsync(ProjectDir, GeneratorId);

        var request = Assert.Single(decision.Requests);
        Assert.Equal(GeneratorId, request.PackageId);
        Assert.Equal("0.0.5", request.Version);

        // Named so the prompt can show what the heuristic read. A user asked an unexpected question
        // about their own project is owed the evidence behind it.
        Assert.Equal("PackageId", request.PackableEvidence);
    }

    [Fact]
    public async Task KeepingItPrivateWritesPrivateAssetsToBothFiles() {
        var (workflow, fileSystem, nuGetClient) = Build(LibraryCsproj, KeepGeneratorPrivateDecision.Instance);
        nuGetClient.VersionsByPackageId[GeneratorId] = ["0.0.5"];
        nuGetClient.DevelopmentDependencyPackageIds.Add(GeneratorId);

        var locked = await workflow.AddDotnetAsync(ProjectDir, GeneratorId);

        Assert.Equal(["all"], locked.Assets.Private);
        Assert.Contains("private_assets = [\"all\"]", ReadManifest(fileSystem));
        Assert.Contains("<PrivateAssets>all</PrivateAssets>", ReadCsproj(fileSystem));

        // Only containment is ever written. "Share it onward" is NuGet's own default, so there is
        // nothing to say, and include_assets/exclude_assets need a signal a nuspec cannot give.
        Assert.DoesNotContain("include_assets", ReadManifest(fileSystem));
        Assert.DoesNotContain("exclude_assets", ReadManifest(fileSystem));
    }

    [Fact]
    public async Task SharingItWritesNothing() {
        var (workflow, fileSystem, nuGetClient) = Build(LibraryCsproj, ShareGeneratorDecision.Instance);
        nuGetClient.VersionsByPackageId[GeneratorId] = ["0.0.5"];
        nuGetClient.DevelopmentDependencyPackageIds.Add(GeneratorId);

        var locked = await workflow.AddDotnetAsync(ProjectDir, GeneratorId);

        Assert.Null(locked.Assets.Private);
        Assert.DoesNotContain("private_assets", ReadManifest(fileSystem));
        Assert.DoesNotContain("PrivateAssets", ReadCsproj(fileSystem));
    }

    /// <summary>The default for every caller that never thought about this — CI, <c>--json</c>, the
    /// editor plugin — keeps an add byte-identical to every release before v0.14.</summary>
    [Fact]
    public async Task TheDefaultDecisionSharesAndWritesNothing() {
        var fileSystem = new InMemoryFileSystem();
        fileSystem.WriteAllText($"{ProjectDir}/dotquay.toml", "# Dotquay project manifest.\n");
        fileSystem.WriteAllText($"{ProjectDir}/Game.csproj", LibraryCsproj);

        var gitClient = new FakeGitClient { FileSystemForFetchedContent = fileSystem };
        var nuGetClient = new FakeNuGetClient();
        nuGetClient.VersionsByPackageId[GeneratorId] = ["0.0.5"];
        nuGetClient.DevelopmentDependencyPackageIds.Add(GeneratorId);

        var workflow = new DependencyWorkflow(
            fileSystem,
            gitClient,
            new FakeNuGetClientFactory(nuGetClient),
            new AddonInstaller(
                fileSystem, new RepoCache(fileSystem, gitClient, "cache"), new SyncEngine(fileSystem), new FakePatchTool(fileSystem)),
            new DotnetInstaller(fileSystem, new FakeDotnetCliRunner(), TestCredentials.None()));

        var locked = await workflow.AddDotnetAsync(ProjectDir, GeneratorId);

        Assert.Null(locked.Assets.Private);
    }
}

/// <summary>
/// Covers <see cref="CsprojPackable"/> — the probe that decides whether the generator question is
/// worth asking at all.
/// </summary>
public class CsprojPackableTests {
    private const string CsprojPath = "project/Game.csproj";

    private static string? Evidence(string csproj) {
        var fileSystem = new InMemoryFileSystem();
        fileSystem.WriteAllText(CsprojPath, csproj);

        return CsprojPackable.Evidence(fileSystem, CsprojPath);
    }

    [Fact]
    public void APlainGodotProjectIsNotPackable() =>
        Assert.Null(Evidence("""
            <Project Sdk="Godot.NET.Sdk/4.7.2">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """));

    [Theory]
    [InlineData("<PackageId>VBeami.Core</PackageId>", "PackageId")]
    [InlineData("<GeneratePackageOnBuild>true</GeneratePackageOnBuild>", "GeneratePackageOnBuild")]
    [InlineData("<IsPackable>true</IsPackable>", "IsPackable")]
    public void AnExplicitPackagingPropertyIsNamed(string property, string expected) =>
        Assert.Equal(expected, Evidence($"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                {property}
              </PropertyGroup>
            </Project>
            """));

    /// <summary>Only an explicit true counts: IsPackable already defaults to true for a library, so
    /// treating absence as packable would make every project match and the prompt universal.</summary>
    [Fact]
    public void AnAbsentIsPackableIsNotEvidence() =>
        Assert.Null(Evidence("""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """));

    /// <summary>An explicit false settles it whatever else the file says — and must read the same
    /// whichever line the author happened to type second.</summary>
    [Theory]
    [InlineData("<PackageId>X</PackageId>\n    <IsPackable>false</IsPackable>")]
    [InlineData("<IsPackable>false</IsPackable>\n    <PackageId>X</PackageId>")]
    public void AnExplicitIsPackableFalseWinsRegardlessOfOrder(string properties) =>
        Assert.Null(Evidence($"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                {properties}
              </PropertyGroup>
            </Project>
            """));

    /// <summary>A malformed .csproj is the build's problem to report, not this probe's. Staying quiet
    /// means a broken project file never becomes a confusing question about source generators.</summary>
    [Fact]
    public void MalformedXmlIsQuiet() => Assert.Null(Evidence("<Project><PropertyGroup>"));

    [Fact]
    public void AMissingCsprojIsQuiet() =>
        Assert.Null(CsprojPackable.Evidence(new InMemoryFileSystem(), CsprojPath));

    /// <summary>The legacy MSBuild namespace still builds, and a namespace-sensitive match would read
    /// every property as absent.</summary>
    [Fact]
    public void TheLegacyMsbuildNamespaceIsStillRead() =>
        Assert.Equal("PackageId", Evidence("""
            <Project xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
              <PropertyGroup>
                <PackageId>VBeami.Core</PackageId>
              </PropertyGroup>
            </Project>
            """));
}
