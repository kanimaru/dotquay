using Dependi.Core.Init;
using Dependi.Core.Manifest;
using Dependi.Tests.Support;
using Xunit;

namespace Dependi.Tests.Init;

public class InitServiceTests
{
    [Fact]
    public void InitProject_WritesScaffoldWhenNoManifestExists()
    {
        var fileSystem = new InMemoryFileSystem();
        var service = new InitService(fileSystem);

        var result = service.InitProject("project", godotVersion: "4.4");

        Assert.True(result.WasCreated);
        Assert.True(fileSystem.FileExists("project/dependi.toml"));

        var manifest = ManifestReader.ReadProject(fileSystem.ReadAllText("project/dependi.toml"));
        Assert.Equal("4.4", manifest.Godot);
    }

    [Fact]
    public void InitProject_DefaultsGodotVersionWhenNotSpecified()
    {
        var fileSystem = new InMemoryFileSystem();
        var service = new InitService(fileSystem);

        service.InitProject("project");

        var manifest = ManifestReader.ReadProject(fileSystem.ReadAllText("project/dependi.toml"));
        Assert.Equal("4.3", manifest.Godot);
    }

    [Fact]
    public void InitProject_RefusesToOverwriteExistingManifest()
    {
        var fileSystem = new InMemoryFileSystem();
        var service = new InitService(fileSystem);

        service.InitProject("project");
        fileSystem.WriteAllText("project/dependi.toml", "# hand-edited\n");

        var result = service.InitProject("project");

        Assert.False(result.WasCreated);
        Assert.Equal("# hand-edited\n", fileSystem.ReadAllText("project/dependi.toml"));
    }

    [Fact]
    public void InitPackage_WritesPackageHeader()
    {
        var fileSystem = new InMemoryFileSystem();
        var service = new InitService(fileSystem);

        var result = service.InitPackage("addons/kani_core", "kani_core", "1.0.0");

        Assert.True(result.WasCreated);
        var package = ManifestReader.ReadPackage(fileSystem.ReadAllText("addons/kani_core/dependi.toml"));
        Assert.Equal("kani_core", package.Name);
        Assert.Equal("1.0.0", package.Version);
    }

    [Fact]
    public void InitPackage_RefusesToOverwriteExistingManifest()
    {
        var fileSystem = new InMemoryFileSystem();
        var service = new InitService(fileSystem);

        service.InitPackage("addons/kani_core", "kani_core", "1.0.0");
        var result = service.InitPackage("addons/kani_core", "kani_core", "2.0.0");

        Assert.False(result.WasCreated);
        var package = ManifestReader.ReadPackage(fileSystem.ReadAllText("addons/kani_core/dependi.toml"));
        Assert.Equal("1.0.0", package.Version);
    }
}
