using Dotquay.Core.Dotnet;
using Dotquay.Tests.Support;
using Xunit;

namespace Dotquay.Tests.Dotnet;

public class CsprojLocatorTests {
    [Fact]
    public void Locate_ExactlyOneCsproj_ReturnsIt() {
        var fileSystem = new InMemoryFileSystem();
        fileSystem.WriteAllText("project/MyGame.csproj", "<Project Sdk=\"Godot.NET.Sdk/4.3.0\"></Project>");

        var path = CsprojLocator.Locate(fileSystem, "project");

        Assert.Equal("project/MyGame.csproj", path);
    }

    [Fact]
    public void Locate_NoCsproj_Throws() {
        var fileSystem = new InMemoryFileSystem();
        fileSystem.CreateDirectory("project");

        var ex = Assert.Throws<DotnetSyncException>(() => CsprojLocator.Locate(fileSystem, "project"));
        Assert.Contains("No .csproj", ex.Message);
    }

    [Fact]
    public void Locate_MultipleCsproj_Throws() {
        var fileSystem = new InMemoryFileSystem();
        fileSystem.WriteAllText("project/A.csproj", "<Project></Project>");
        fileSystem.WriteAllText("project/B.csproj", "<Project></Project>");

        var ex = Assert.Throws<DotnetSyncException>(() => CsprojLocator.Locate(fileSystem, "project"));
        Assert.Contains("Multiple .csproj", ex.Message);
    }

    [Fact]
    public void Locate_IgnoresCsprojInSubdirectories() {
        var fileSystem = new InMemoryFileSystem();
        fileSystem.WriteAllText("project/MyGame.csproj", "<Project></Project>");
        fileSystem.WriteAllText("project/addons/some_addon/Nested.csproj", "<Project></Project>");

        var path = CsprojLocator.Locate(fileSystem, "project");

        Assert.Equal("project/MyGame.csproj", path);
    }
}
