using Dependi.Core.Dotnet;
using Dependi.Tests.Support;
using Xunit;

namespace Dependi.Tests.Dotnet;

public class CsprojPackageReferenceWriterTests
{
    private const string SdkStyleCsproj = """
        <Project Sdk="Godot.NET.Sdk/4.3.0">
          <PropertyGroup>
            <TargetFramework>net8.0</TargetFramework>
          </PropertyGroup>
        </Project>
        """;

    [Fact]
    public void AddOrUpdate_NoExistingReference_AddsOne()
    {
        var fileSystem = new InMemoryFileSystem();
        fileSystem.WriteAllText("project/Game.csproj", SdkStyleCsproj);

        CsprojPackageReferenceWriter.AddOrUpdate(fileSystem, "project/Game.csproj", "Newtonsoft.Json", "13.0.3");

        var text = fileSystem.ReadAllText("project/Game.csproj");
        Assert.Contains("Include=\"Newtonsoft.Json\"", text);
        Assert.Contains("Version=\"13.0.3\"", text);
    }

    [Fact]
    public void AddOrUpdate_ExistingReference_UpdatesVersionInPlace()
    {
        var fileSystem = new InMemoryFileSystem();
        fileSystem.WriteAllText("project/Game.csproj", SdkStyleCsproj);
        CsprojPackageReferenceWriter.AddOrUpdate(fileSystem, "project/Game.csproj", "Newtonsoft.Json", "13.0.3");

        CsprojPackageReferenceWriter.AddOrUpdate(fileSystem, "project/Game.csproj", "Newtonsoft.Json", "13.0.4");

        var text = fileSystem.ReadAllText("project/Game.csproj");
        Assert.Contains("Version=\"13.0.4\"", text);
        Assert.DoesNotContain("Version=\"13.0.3\"", text);
        Assert.Equal(1, CountOccurrences(text, "PackageReference"));
    }

    [Fact]
    public void AddOrUpdate_MatchesPackageIdCaseInsensitively()
    {
        var fileSystem = new InMemoryFileSystem();
        fileSystem.WriteAllText("project/Game.csproj", SdkStyleCsproj);
        CsprojPackageReferenceWriter.AddOrUpdate(fileSystem, "project/Game.csproj", "Newtonsoft.Json", "13.0.3");

        CsprojPackageReferenceWriter.AddOrUpdate(fileSystem, "project/Game.csproj", "newtonsoft.json", "13.0.4");

        var text = fileSystem.ReadAllText("project/Game.csproj");
        Assert.Equal(1, CountOccurrences(text, "PackageReference"));
    }

    [Fact]
    public void AddOrUpdate_PreservesExistingPropertyGroup()
    {
        var fileSystem = new InMemoryFileSystem();
        fileSystem.WriteAllText("project/Game.csproj", SdkStyleCsproj);

        CsprojPackageReferenceWriter.AddOrUpdate(fileSystem, "project/Game.csproj", "Newtonsoft.Json", "13.0.3");

        var text = fileSystem.ReadAllText("project/Game.csproj");
        Assert.Contains("<TargetFramework>net8.0</TargetFramework>", text);
        Assert.Contains("Godot.NET.Sdk/4.3.0", text);
    }

    [Fact]
    public void Remove_ExistingReference_RemovesItAndReturnsTrue()
    {
        var fileSystem = new InMemoryFileSystem();
        fileSystem.WriteAllText("project/Game.csproj", SdkStyleCsproj);
        CsprojPackageReferenceWriter.AddOrUpdate(fileSystem, "project/Game.csproj", "Newtonsoft.Json", "13.0.3");

        var removed = CsprojPackageReferenceWriter.Remove(fileSystem, "project/Game.csproj", "Newtonsoft.Json");

        Assert.True(removed);
        Assert.DoesNotContain("Newtonsoft.Json", fileSystem.ReadAllText("project/Game.csproj"));
    }

    [Fact]
    public void Remove_NoExistingReference_ReturnsFalseAndLeavesFileUnchanged()
    {
        var fileSystem = new InMemoryFileSystem();
        fileSystem.WriteAllText("project/Game.csproj", SdkStyleCsproj);

        var removed = CsprojPackageReferenceWriter.Remove(fileSystem, "project/Game.csproj", "Newtonsoft.Json");

        Assert.False(removed);
    }

    [Fact]
    public void AddOrUpdate_NotAProjectRoot_Throws()
    {
        var fileSystem = new InMemoryFileSystem();
        fileSystem.WriteAllText("project/Game.csproj", "<NotAProject></NotAProject>");

        Assert.Throws<DotnetSyncException>(
            () => CsprojPackageReferenceWriter.AddOrUpdate(fileSystem, "project/Game.csproj", "Newtonsoft.Json", "13.0.3"));
    }

    [Fact]
    public void AddOrUpdate_InvalidXml_Throws()
    {
        var fileSystem = new InMemoryFileSystem();
        fileSystem.WriteAllText("project/Game.csproj", "<Project><Unclosed></Project>");

        Assert.Throws<DotnetSyncException>(
            () => CsprojPackageReferenceWriter.AddOrUpdate(fileSystem, "project/Game.csproj", "Newtonsoft.Json", "13.0.3"));
    }

    private static int CountOccurrences(string text, string substring)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(substring, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += substring.Length;
        }

        return count;
    }
}
