using System.Xml.Linq;
using Dotquay.Core.Local;
using Dotquay.Tests.Support;
using Xunit;

namespace Dotquay.Tests.Local;

/// <summary>
/// A linked package's <c>ProjectReference</c> applies exactly where the <c>PackageReference</c> it
/// replaces applies.
/// </summary>
/// <remarks>
/// A package declared only for Debug builds and linked without its condition comes back in every
/// configuration: an export build then carried a linked test server whose apphost collided with the
/// game's (NETSDK1152). The overlay has to read the condition off the committed <c>.csproj</c>,
/// because that file is where the user decided when the package applies.
/// </remarks>
public class LinkedReferenceConditionTests {
    private const string ProjectDirectory = "project";
    private const string LinkedDirectory = "elsewhere/testing";
    private const string PackageId = "CROSS.Testing";
    private const string DebugOnly = "'$(Configuration)' == 'Debug'";
    private const string WindowsOnly = "'$(OS)' == 'Windows_NT'";

    private static XElement LinkedReference(string csprojBody) {
        var fileSystem = new InMemoryFileSystem();
        fileSystem.WriteAllText(fileSystem.CombinePath(ProjectDirectory, "Game.csproj"),
            $"<Project Sdk=\"Godot.NET.Sdk/4.3.0\">{csprojBody}</Project>");
        fileSystem.WriteAllText(fileSystem.CombinePath(LinkedDirectory, "CROSS.Testing.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\" />");

        var links = new LocalLinks(fileSystem);
        links.SetDotnet(ProjectDirectory, PackageId, LinkedDirectory, analyzer: false);
        LocalTargetsWriter.Sync(fileSystem, ProjectDirectory, links.ReadDotnet(ProjectDirectory));

        var overlay = XDocument.Parse(fileSystem.ReadAllText(
            fileSystem.CombinePath(ProjectDirectory, LocalTargetsWriter.FileName)));
        return Assert.Single(overlay.Descendants("ProjectReference"));
    }

    private static string? ConditionOf(XElement element) => (string?)element.Attribute("Condition");

    [Fact]
    public void Sync_APackageInAConditionalItemGroup_LinksUnderTheSameCondition() {
        var reference = LinkedReference($"""
            <ItemGroup Condition="{DebugOnly}">
              <PackageReference Include="CROSS.Testing" Version="0.1.0" />
            </ItemGroup>
            """);

        Assert.Equal(DebugOnly, ConditionOf(reference));
    }

    [Fact]
    public void Sync_AnUnconditionalPackage_StaysUnconditional() {
        var reference = LinkedReference("""
            <ItemGroup>
              <PackageReference Include="CROSS.Testing" Version="0.1.0" />
            </ItemGroup>
            """);

        Assert.Null(ConditionOf(reference));
    }

    [Fact]
    public void Sync_AConditionOnTheElementAndItsGroup_AreBothKept() {
        var reference = LinkedReference($"""
            <ItemGroup Condition="{DebugOnly}">
              <PackageReference Include="CROSS.Testing" Version="0.1.0" Condition="{WindowsOnly}" />
            </ItemGroup>
            """);

        Assert.Equal($"({DebugOnly}) and ({WindowsOnly})", ConditionOf(reference));
    }

    [Fact]
    public void Sync_APackageDeclaredUnderTwoConditions_LinksUnderEither() {
        var reference = LinkedReference($"""
            <ItemGroup Condition="{DebugOnly}">
              <PackageReference Include="CROSS.Testing" Version="0.1.0" />
            </ItemGroup>
            <ItemGroup Condition="{WindowsOnly}">
              <PackageReference Include="CROSS.Testing" Version="0.1.0" />
            </ItemGroup>
            """);

        Assert.Equal($"({DebugOnly}) or ({WindowsOnly})", ConditionOf(reference));
    }

    [Fact]
    public void Sync_OneUnconditionalDeclaration_MakesTheLinkUnconditional() {
        var reference = LinkedReference($"""
            <ItemGroup Condition="{DebugOnly}">
              <PackageReference Include="CROSS.Testing" Version="0.1.0" />
            </ItemGroup>
            <ItemGroup>
              <PackageReference Include="CROSS.Testing" Version="0.1.0" />
            </ItemGroup>
            """);

        Assert.Null(ConditionOf(reference));
    }

    [Fact]
    public void Sync_APackageUnderChooseWhen_TakesTheWhenCondition() {
        var reference = LinkedReference($"""
            <Choose>
              <When Condition="{DebugOnly}">
                <ItemGroup>
                  <PackageReference Include="CROSS.Testing" Version="0.1.0" />
                </ItemGroup>
              </When>
            </Choose>
            """);

        Assert.Equal(DebugOnly, ConditionOf(reference));
    }

    [Fact]
    public void Sync_APackageUnderOtherwise_TakesNoneOfTheWhenConditions() {
        var reference = LinkedReference($"""
            <Choose>
              <When Condition="{DebugOnly}">
                <ItemGroup />
              </When>
              <When Condition="{WindowsOnly}">
                <ItemGroup />
              </When>
              <Otherwise>
                <ItemGroup>
                  <PackageReference Include="CROSS.Testing" Version="0.1.0" />
                </ItemGroup>
              </Otherwise>
            </Choose>
            """);

        Assert.Equal($"!({DebugOnly}) and !({WindowsOnly})", ConditionOf(reference));
    }

    [Fact]
    public void Sync_APackageInAnIncludeList_IsFoundCaseInsensitively() {
        var reference = LinkedReference($"""
            <ItemGroup Condition="{DebugOnly}">
              <PackageReference Include="Other.Package; cross.testing" Version="0.1.0" />
            </ItemGroup>
            """);

        Assert.Equal(DebugOnly, ConditionOf(reference));
    }

    [Fact]
    public void Sync_APackageReferenceInsideATarget_IsNotADeclaration() {
        // Items a target adds at build time are never restored, so they say nothing about when the
        // package applies.
        var reference = LinkedReference($"""
            <ItemGroup Condition="{DebugOnly}">
              <PackageReference Include="CROSS.Testing" Version="0.1.0" />
            </ItemGroup>
            <Target Name="Late">
              <ItemGroup>
                <PackageReference Include="CROSS.Testing" Version="0.1.0" />
              </ItemGroup>
            </Target>
            """);

        Assert.Equal(DebugOnly, ConditionOf(reference));
    }

    [Fact]
    public void Sync_APackageNotInTheCsproj_StaysUnconditional() {
        // Linked before it is installed: the next sync after the install reads the declaration.
        var reference = LinkedReference("<ItemGroup />");

        Assert.Null(ConditionOf(reference));
    }
}
