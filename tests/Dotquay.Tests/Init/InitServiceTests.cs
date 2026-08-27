using Dotquay.Core.Init;
using Dotquay.Core.Manifest;
using Dotquay.Tests.Support;
using Xunit;

namespace Dotquay.Tests.Init;

public class InitServiceTests
{
    [Fact]
    public void InitProject_WritesScaffoldWhenNoManifestExists()
    {
        var fileSystem = new InMemoryFileSystem();
        var service = new InitService(fileSystem);

        var result = service.InitProject("project");

        Assert.True(result.WasCreated);
        Assert.True(fileSystem.FileExists("project/dotquay.toml"));

        // Parses as a valid, empty manifest. The scaffold is comments plus commented-out examples —
        // there is nothing dotquay needs a fresh project to state.
        var manifest = ManifestReader.ReadProject(fileSystem.ReadAllText("project/dotquay.toml"));
        Assert.Empty(manifest.Addons);
        Assert.Empty(manifest.Dotnet);
        Assert.Null(manifest.NuGetSource);
    }

    [Fact]
    public void InitProject_DoesNotWriteAGodotVersion()
    {
        var fileSystem = new InMemoryFileSystem();
        var service = new InitService(fileSystem);

        service.InitProject("project");

        // The scaffold used to hardcode godot = "4.3" into every project regardless of the Godot
        // version actually in use, and nothing ever read it back. Stating the wrong version
        // confidently is worse than stating none.
        // Matches the key, not the word — the commented example URL legitimately contains "godot".
        Assert.DoesNotContain("godot =", fileSystem.ReadAllText("project/dotquay.toml"));
        Assert.Null(ManifestReader.ReadProject(fileSystem.ReadAllText("project/dotquay.toml")).NuGetSource);
    }

    [Fact]
    public void InitProject_WithNuGetSource_WritesTheProjectSection()
    {
        var fileSystem = new InMemoryFileSystem();
        var service = new InitService(fileSystem);

        service.InitProject("project", nuGetSource: "https://gitlab.example.com/api/v4/projects/1/packages/nuget/index.json");

        var manifest = ManifestReader.ReadProject(fileSystem.ReadAllText("project/dotquay.toml"));
        Assert.Equal("https://gitlab.example.com/api/v4/projects/1/packages/nuget/index.json", manifest.NuGetSource);
    }

    [Fact]
    public void InitProject_RefusesToOverwriteExistingManifest()
    {
        var fileSystem = new InMemoryFileSystem();
        var service = new InitService(fileSystem);

        service.InitProject("project");
        fileSystem.WriteAllText("project/dotquay.toml", "# hand-edited\n");

        var result = service.InitProject("project");

        Assert.False(result.WasCreated);
        Assert.Equal("# hand-edited\n", fileSystem.ReadAllText("project/dotquay.toml"));
    }

    [Fact]
    public void InitPackage_WritesPackageHeader()
    {
        var fileSystem = new InMemoryFileSystem();
        var service = new InitService(fileSystem);

        var result = service.InitPackage("addons/kani_core", "kani_core");

        Assert.True(result.WasCreated);
        var package = ManifestReader.ReadPackage(fileSystem.ReadAllText("addons/kani_core/dotquay.toml"));
        Assert.Equal("kani_core", package.Name);
    }

    [Fact]
    public void InitPackage_DoesNotWriteAVersion()
    {
        var fileSystem = new InMemoryFileSystem();
        var service = new InitService(fileSystem);

        service.InitPackage("addons/kani_core", "kani_core");

        // plugin.cfg and the git tag already carry the version, and doctor already compares those two
        // against each other. A third copy only ever drifts.
        var package = ManifestReader.ReadPackage(fileSystem.ReadAllText("addons/kani_core/dotquay.toml"));
        Assert.Null(package.Version);
    }

    [Fact]
    public void InitPackage_RefusesToOverwriteExistingManifest()
    {
        var fileSystem = new InMemoryFileSystem();
        var service = new InitService(fileSystem);

        service.InitPackage("addons/kani_core", "kani_core");
        var result = service.InitPackage("addons/kani_core", "renamed");

        Assert.False(result.WasCreated);
        var package = ManifestReader.ReadPackage(fileSystem.ReadAllText("addons/kani_core/dotquay.toml"));
        Assert.Equal("kani_core", package.Name);
    }
}
