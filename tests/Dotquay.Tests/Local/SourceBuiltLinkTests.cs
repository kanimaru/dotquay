using Dotquay.Core.Local;
using Dotquay.Tests.Support;
using Xunit;

namespace Dotquay.Tests.Local;

/// <summary>
/// DESIGN-v0.26 §E, decision 3: a dotnet link is refused for a package the .csproj already builds from
/// source, conditional or not, and nothing is written when it is.
/// </summary>
public class SourceBuiltLinkTests
{
    private const string ProjectDirectory = "project";
    private const string Package = "Acme.Core";
    private const string Checkout = "elsewhere/acme-core";
    private const string Include = @"..\src\Acme.Core\Acme.Core.csproj";

    private static InMemoryFileSystem Build(string referenceAttributes = "", bool withProjectReference = true)
    {
        var fileSystem = new InMemoryFileSystem();
        var reference = withProjectReference
            ? $"""<ItemGroup><ProjectReference Include="{Include}"{referenceAttributes} /></ItemGroup>"""
            : "";
        fileSystem.WriteAllText("project/Game.csproj", $"""
            <Project Sdk="Godot.NET.Sdk/4.5.0">
              {reference}
              <ItemGroup><PackageReference Include="Other.Package" Version="1.0.0" /></ItemGroup>
            </Project>
            """);
        fileSystem.WriteAllText("src/Acme.Core/Acme.Core.csproj", """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><PackageId>Acme.Core</PackageId></PropertyGroup></Project>""");
        fileSystem.WriteAllText($"{Checkout}/Acme.Core.csproj", """<Project Sdk="Microsoft.NET.Sdk" />""");
        return fileSystem;
    }

    [Fact]
    public void Link_PackageBuiltFromSource_IsRefused_NamingTheReference()
    {
        var fileSystem = Build();

        var ex = Assert.Throws<LocalLinkException>(() => new LocalLinks(fileSystem).SetDotnet(ProjectDirectory, Package, Checkout, analyzer: false));

        Assert.StartsWith($"'{Package}' is already built from source by {Include}", ex.Message);
        Assert.Empty(new LocalLinks(fileSystem).ReadDotnet(ProjectDirectory));
        Assert.False(fileSystem.FileExists("project/dotquay.local.toml"));
    }

    [Fact]
    public void Link_PackageBuiltFromSourceConditionally_IsRefusedToo_AndSaysWhen()
    {
        var fileSystem = Build(referenceAttributes: " Condition=\"'$(Configuration)' == 'Debug'\"");

        var ex = Assert.Throws<LocalLinkException>(() => new LocalLinks(fileSystem).SetDotnet(ProjectDirectory, Package, Checkout, analyzer: false));

        Assert.Contains("when '$(Configuration)' == 'Debug'", ex.Message);
    }

    [Fact]
    public void Link_RefusalComesBeforeThePathIsChecked()
    {
        // No path makes this link safe, so a missing checkout must not be the error the user sees first.
        var fileSystem = Build();

        var ex = Assert.Throws<LocalLinkException>(() => new LocalLinks(fileSystem).SetDotnet(ProjectDirectory, Package, "no/such/place", analyzer: false));

        Assert.Contains("already built from source", ex.Message);
    }

    [Fact]
    public void Link_OtherPackage_IsUnaffected()
    {
        var fileSystem = Build();
        var links = new LocalLinks(fileSystem);

        links.SetDotnet(ProjectDirectory, "Other.Package", Checkout, analyzer: false);

        Assert.Contains("Other.Package", links.ReadDotnet(ProjectDirectory).Keys);
    }

    [Fact]
    public void Link_WithoutAProjectReference_WorksAsBefore()
    {
        var fileSystem = Build(withProjectReference: false);
        var links = new LocalLinks(fileSystem);

        links.SetDotnet(ProjectDirectory, Package, Checkout, analyzer: false);

        Assert.Contains(Package, links.ReadDotnet(ProjectDirectory).Keys);
    }
}
