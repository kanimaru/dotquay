using Dotquay.Core.Dotnet;
using Dotquay.Core.Manifest;
using Dotquay.Tests.Support;
using Xunit;

namespace Dotquay.Tests.Dotnet;

public class CsprojPackageReferenceWriterTests {
    private const string SdkStyleCsproj = """
        <Project Sdk="Godot.NET.Sdk/4.3.0">
          <PropertyGroup>
            <TargetFramework>net8.0</TargetFramework>
          </PropertyGroup>
        </Project>
        """;

    [Fact]
    public void AddOrUpdate_NoExistingReference_AddsOne() {
        var fileSystem = new InMemoryFileSystem();
        fileSystem.WriteAllText("project/Game.csproj", SdkStyleCsproj);

        CsprojPackageReferenceWriter.AddOrUpdate(fileSystem, "project/Game.csproj", "Newtonsoft.Json", "13.0.3");

        var text = fileSystem.ReadAllText("project/Game.csproj");
        Assert.Contains("Include=\"Newtonsoft.Json\"", text);
        Assert.Contains("Version=\"13.0.3\"", text);
    }

    [Fact]
    public void AddOrUpdate_NoItemGroup_InsertsAboveTrailingImportsAndTheirComments() {
        var fileSystem = new InMemoryFileSystem();
        fileSystem.WriteAllText("project/Game.csproj", """
            <Project>
              <PropertyGroup>
                <TargetFramework>net8.0</TargetFramework>
              </PropertyGroup>
              <!-- explicit SDK targets -->
              <Import Project="Sdk.targets" Sdk="Microsoft.NET.Sdk" />
            </Project>
            """);

        CsprojPackageReferenceWriter.AddOrUpdate(fileSystem, "project/Game.csproj", "Newtonsoft.Json", "13.0.3");

        var text = fileSystem.ReadAllText("project/Game.csproj");
        var reference = text.IndexOf("<PackageReference", StringComparison.Ordinal);
        Assert.True(reference > text.IndexOf("</PropertyGroup>", StringComparison.Ordinal));
        Assert.True(reference < text.IndexOf("<!-- explicit SDK targets -->", StringComparison.Ordinal));
    }

    [Fact]
    public void AddOrUpdate_ExistingReference_UpdatesVersionInPlace() {
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
    public void AddOrUpdate_MatchesPackageIdCaseInsensitively() {
        var fileSystem = new InMemoryFileSystem();
        fileSystem.WriteAllText("project/Game.csproj", SdkStyleCsproj);
        CsprojPackageReferenceWriter.AddOrUpdate(fileSystem, "project/Game.csproj", "Newtonsoft.Json", "13.0.3");

        CsprojPackageReferenceWriter.AddOrUpdate(fileSystem, "project/Game.csproj", "newtonsoft.json", "13.0.4");

        var text = fileSystem.ReadAllText("project/Game.csproj");
        Assert.Equal(1, CountOccurrences(text, "PackageReference"));
    }

    [Fact]
    public void AddOrUpdate_PreservesExistingPropertyGroup() {
        var fileSystem = new InMemoryFileSystem();
        fileSystem.WriteAllText("project/Game.csproj", SdkStyleCsproj);

        CsprojPackageReferenceWriter.AddOrUpdate(fileSystem, "project/Game.csproj", "Newtonsoft.Json", "13.0.3");

        var text = fileSystem.ReadAllText("project/Game.csproj");
        Assert.Contains("<TargetFramework>net8.0</TargetFramework>", text);
        Assert.Contains("Godot.NET.Sdk/4.3.0", text);
    }

    [Fact]
    public void Remove_ExistingReference_RemovesItAndReturnsTrue() {
        var fileSystem = new InMemoryFileSystem();
        fileSystem.WriteAllText("project/Game.csproj", SdkStyleCsproj);
        CsprojPackageReferenceWriter.AddOrUpdate(fileSystem, "project/Game.csproj", "Newtonsoft.Json", "13.0.3");

        var removed = CsprojPackageReferenceWriter.Remove(fileSystem, "project/Game.csproj", "Newtonsoft.Json");

        Assert.True(removed);
        Assert.DoesNotContain("Newtonsoft.Json", fileSystem.ReadAllText("project/Game.csproj"));
    }

    [Fact]
    public void Remove_NoExistingReference_ReturnsFalseAndLeavesFileUnchanged() {
        var fileSystem = new InMemoryFileSystem();
        fileSystem.WriteAllText("project/Game.csproj", SdkStyleCsproj);

        var removed = CsprojPackageReferenceWriter.Remove(fileSystem, "project/Game.csproj", "Newtonsoft.Json");

        Assert.False(removed);
    }

    [Fact]
    public void AddOrUpdate_NotAProjectRoot_Throws() {
        var fileSystem = new InMemoryFileSystem();
        fileSystem.WriteAllText("project/Game.csproj", "<NotAProject></NotAProject>");

        Assert.Throws<DotnetSyncException>(
            () => CsprojPackageReferenceWriter.AddOrUpdate(fileSystem, "project/Game.csproj", "Newtonsoft.Json", "13.0.3"));
    }

    [Fact]
    public void AddOrUpdate_InvalidXml_Throws() {
        var fileSystem = new InMemoryFileSystem();
        fileSystem.WriteAllText("project/Game.csproj", "<Project><Unclosed></Project>");

        Assert.Throws<DotnetSyncException>(
            () => CsprojPackageReferenceWriter.AddOrUpdate(fileSystem, "project/Game.csproj", "Newtonsoft.Json", "13.0.3"));
    }

    // ---- asset metadata (v0.10) -------------------------------------------

    private static readonly PackageAssets AnalyzerAssets = new() {
        Private = ["none"],
        Include = ["runtime", "analyzers"],
    };

    private static InMemoryFileSystem WithReference(string csproj) {
        var fileSystem = new InMemoryFileSystem();
        fileSystem.WriteAllText("project/Game.csproj", csproj);
        return fileSystem;
    }

    [Fact]
    public void AddOrUpdate_WithDeclaredAssets_WritesChildElements() {
        var fileSystem = WithReference(SdkStyleCsproj);

        CsprojPackageReferenceWriter.AddOrUpdate(
            fileSystem, "project/Game.csproj", "gdUnit4.analyzers", "1.0.0", AnalyzerAssets);

        var text = fileSystem.ReadAllText("project/Game.csproj");
        Assert.Contains("<PrivateAssets>none</PrivateAssets>", text);
        Assert.Contains("<IncludeAssets>runtime;analyzers</IncludeAssets>", text);
        Assert.DoesNotContain("ExcludeAssets", text);
    }

    [Fact]
    public void AddOrUpdate_ChangedAssets_ReplaceRatherThanDuplicate() {
        var fileSystem = WithReference(SdkStyleCsproj);
        CsprojPackageReferenceWriter.AddOrUpdate(
            fileSystem, "project/Game.csproj", "gdUnit4.analyzers", "1.0.0", AnalyzerAssets);

        CsprojPackageReferenceWriter.AddOrUpdate(
            fileSystem, "project/Game.csproj", "gdUnit4.analyzers", "1.0.0",
            new PackageAssets { Private = ["all"] }, AnalyzerAssets);

        var text = fileSystem.ReadAllText("project/Game.csproj");
        Assert.Equal(1, CountOccurrences(text, "<PrivateAssets>"));
        Assert.Contains("<PrivateAssets>all</PrivateAssets>", text);
    }

    [Fact]
    public void AddOrUpdate_UndeclaredAndNeverWrittenByDotquay_LeavesHandEditsAlone() {
        // The pre-v0.10 workflow: someone hand-added the metadata and dotquay knows nothing about it.
        // Stripping it on the next version bump would be dotquay destroying work it never owned.
        var fileSystem = WithReference("""
            <Project Sdk="Godot.NET.Sdk/4.3.0">
              <ItemGroup>
                <PackageReference Include="gdUnit4.analyzers" Version="1.0.0">
                  <PrivateAssets>none</PrivateAssets>
                </PackageReference>
              </ItemGroup>
            </Project>
            """);

        CsprojPackageReferenceWriter.AddOrUpdate(fileSystem, "project/Game.csproj", "gdUnit4.analyzers", "1.0.1");

        var text = fileSystem.ReadAllText("project/Game.csproj");
        Assert.Contains("<PrivateAssets>none</PrivateAssets>", text);
        Assert.Contains("Version=\"1.0.1\"", text);
    }

    [Fact]
    public void AddOrUpdate_UndeclaredButPreviouslyWrittenByDotquay_RemovesIt() {
        // The manifest withdrew the declaration. Only the lockfile record makes this distinguishable
        // from the hand-edit case above.
        var fileSystem = WithReference(SdkStyleCsproj);
        CsprojPackageReferenceWriter.AddOrUpdate(
            fileSystem, "project/Game.csproj", "gdUnit4.analyzers", "1.0.0", AnalyzerAssets);

        CsprojPackageReferenceWriter.AddOrUpdate(
            fileSystem, "project/Game.csproj", "gdUnit4.analyzers", "1.0.0", PackageAssets.None, AnalyzerAssets);

        var text = fileSystem.ReadAllText("project/Game.csproj");
        Assert.DoesNotContain("PrivateAssets", text);
        Assert.DoesNotContain("IncludeAssets", text);
    }

    [Fact]
    public void AddOrUpdate_MetadataWrittenAsAnAttribute_IsUpdatedInPlace() {
        var fileSystem = WithReference("""
            <Project Sdk="Godot.NET.Sdk/4.3.0">
              <ItemGroup>
                <PackageReference Include="gdUnit4.analyzers" Version="1.0.0" PrivateAssets="all" />
              </ItemGroup>
            </Project>
            """);

        CsprojPackageReferenceWriter.AddOrUpdate(
            fileSystem, "project/Game.csproj", "gdUnit4.analyzers", "1.0.0", new PackageAssets { Private = ["none"] });

        var text = fileSystem.ReadAllText("project/Game.csproj");
        Assert.Contains("PrivateAssets=\"none\"", text);

        // The two spellings mean the same thing to MSBuild, so carrying both would leave MSBuild's
        // precedence — not dotquay — deciding which one wins.
        Assert.DoesNotContain("<PrivateAssets>", text);
    }

    [Fact]
    public void ReadAssets_TolerantOfSpacingAndCasing() {
        var fileSystem = WithReference("""
            <Project Sdk="Godot.NET.Sdk/4.3.0">
              <ItemGroup>
                <PackageReference Include="gdUnit4.analyzers" Version="1.0.0">
                  <IncludeAssets>runtime; build; contentfiles</IncludeAssets>
                </PackageReference>
              </ItemGroup>
            </Project>
            """);

        var assets = CsprojPackageReferenceWriter.ReadAssets(fileSystem, "project/Game.csproj", "gdUnit4.analyzers");

        Assert.Equal(["runtime", "build", "contentFiles"], assets!.Include);
    }

    [Fact]
    public void AddOrUpdate_UnrelatedMetadata_SurvivesEveryPath() {
        var fileSystem = WithReference("""
            <Project Sdk="Godot.NET.Sdk/4.3.0">
              <ItemGroup>
                <PackageReference Include="gdUnit4.analyzers" Version="1.0.0" NoWarn="NU1605" Condition="'$(X)'=='y'">
                  <PrivateAssets>none</PrivateAssets>
                </PackageReference>
              </ItemGroup>
            </Project>
            """);

        CsprojPackageReferenceWriter.AddOrUpdate(
            fileSystem, "project/Game.csproj", "gdUnit4.analyzers", "1.0.1",
            new PackageAssets { Include = ["runtime"] }, new PackageAssets { Private = ["none"] });

        var text = fileSystem.ReadAllText("project/Game.csproj");
        Assert.Contains("NoWarn=\"NU1605\"", text);
        Assert.Contains("Condition=\"'$(X)'=='y'\"", text);
    }

    private static int CountOccurrences(string text, string substring) {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(substring, index, StringComparison.Ordinal)) >= 0) {
            count++;
            index += substring.Length;
        }

        return count;
    }
}
