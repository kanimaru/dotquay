using Dotquay.Core.Dotnet.SourceBuild;
using Dotquay.Tests.Support;
using Xunit;

namespace Dotquay.Tests.Dotnet.SourceBuild;

/// <summary>
/// DESIGN-v0.26 §A: when a ProjectReference counts as building a package. Every doubt must resolve
/// to "no provider", because "no provider" is today's behaviour and the only safe default.
/// </summary>
public class SourceProvidedPackagesTests {
    private const string Csproj = "game/Game.csproj";

    private static string Library(string properties = "") => $"""
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
            {properties}
          </PropertyGroup>
        </Project>
        """;

    private static string Consumer(string items) => $"""
        <Project Sdk="Godot.NET.Sdk/4.5.0">
          <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
          </PropertyGroup>
          {items}
        </Project>
        """;

    private static InMemoryFileSystem WithConsumer(string items,
        params (string Path, string Content)[] projects) {
        var fileSystem = new InMemoryFileSystem();
        fileSystem.WriteAllText(Csproj, Consumer(items));
        foreach (var (path, content) in projects) {
            fileSystem.WriteAllText(path, content);
        }

        return fileSystem;
    }

    // --- RelativeProjectPath ---------------------------------------------------------------------

    [Theory]
    [InlineData("game/Game.csproj", @"..\src\Core\Core.csproj", "src/Core/Core.csproj")]
    [InlineData("game/Game.csproj", "../src/Core/Core.csproj", "src/Core/Core.csproj")]
    [InlineData("game/Game.csproj", @".\lib\.\Lib.csproj", "game/lib/Lib.csproj")]
    [InlineData("a/b/c/Game.csproj", @"..\..\x\X.csproj", "a/x/X.csproj")]
    [InlineData("Game.csproj", @"..\outside\X.csproj", "../outside/X.csproj")]
    [InlineData(@"F:\projects\vbeami\godot\VBeami.csproj", @"..\src\VBeami.Core\VBeami.Core.csproj",
        "F:/projects/vbeami/src/VBeami.Core/VBeami.Core.csproj")]
    [InlineData("/home/kani/vbeami/godot/VBeami.csproj", "../src/Core.csproj",
        "/home/kani/vbeami/src/Core.csproj")]
    [InlineData("game/Game.csproj", @"D:\elsewhere\X.csproj", "D:/elsewhere/X.csproj")]
    [InlineData("/game/Game.csproj", "../../../X.csproj", "/X.csproj")]
    public void RelativeProjectPath_ResolvesLexically(string from, string include,
        string expected) {
        Assert.Equal(expected, RelativeProjectPath.Resolve(from, include));
    }

    // --- identity --------------------------------------------------------------------------------

    [Fact]
    public void Identity_PackageIdWins() {
        var fileSystem = WithConsumer("",
            ("lib/Lib.csproj",
                Library("<AssemblyName>Other</AssemblyName><PackageId>Acme.Lib</PackageId>")));

        Assert.Equal("Acme.Lib", ProjectIdentity.Read(fileSystem, "lib/Lib.csproj").PackageId);
    }

    [Fact]
    public void Identity_FallsBackToAssemblyName() {
        var fileSystem = WithConsumer("",
            ("lib/Lib.csproj", Library("<AssemblyName>Acme.Lib</AssemblyName>")));

        Assert.Equal("Acme.Lib", ProjectIdentity.Read(fileSystem, "lib/Lib.csproj").PackageId);
    }

    [Fact]
    public void Identity_FallsBackToTheFileName() {
        var fileSystem = WithConsumer("", ("lib/Acme.Lib.csproj", Library()));

        Assert.Equal("Acme.Lib", ProjectIdentity.Read(fileSystem, "lib/Acme.Lib.csproj").PackageId);
    }

    [Fact]
    public void Identity_LaterDefinitionOverridesEarlier_AsInMsbuild() {
        var fileSystem = WithConsumer("", ("lib/Lib.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><PackageId>First</PackageId></PropertyGroup>
              <PropertyGroup><PackageId>Second</PackageId></PropertyGroup>
            </Project>
            """));

        Assert.Equal("Second", ProjectIdentity.Read(fileSystem, "lib/Lib.csproj").PackageId);
    }

    [Fact]
    public void Identity_EmptyValue_BehavesAsUnset() {
        var fileSystem = WithConsumer("",
            ("lib/Acme.Lib.csproj", Library("<PackageId></PackageId>")));

        Assert.Equal("Acme.Lib", ProjectIdentity.Read(fileSystem, "lib/Acme.Lib.csproj").PackageId);
    }

    [Theory]
    [InlineData("<PackageId>$(RootNamespace).Core</PackageId>")]
    [InlineData("<PackageId Condition=\"'$(Configuration)' == 'Release'\">Acme.Lib</PackageId>")]
    [InlineData("<AssemblyName>$(MSBuildProjectName)</AssemblyName>")]
    public void Identity_ThatNeedsMsbuild_IsUnknown_NotAGuess(string properties) {
        // The file is named Acme.Lib.csproj, so falling through to the file name would "match" -
        // which is exactly the guess this must not make when a property is set but cannot be read.
        var fileSystem = WithConsumer("", ("lib/Acme.Lib.csproj", Library(properties)));

        Assert.Null(ProjectIdentity.Read(fileSystem, "lib/Acme.Lib.csproj").PackageId);
    }

    [Fact]
    public void Identity_ConditionalPropertyGroup_IsUnknown() {
        var fileSystem = WithConsumer("", ("lib/Acme.Lib.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup Condition="'$(Configuration)' == 'Debug'"><PackageId>Acme.Lib.Debug</PackageId></PropertyGroup>
            </Project>
            """));

        Assert.Null(ProjectIdentity.Read(fileSystem, "lib/Acme.Lib.csproj").PackageId);
    }

    [Fact]
    public void Identity_PropertyInsideChoose_IsUnknown() {
        var fileSystem = WithConsumer("", ("lib/Acme.Lib.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <Choose>
                <When Condition="'$(X)' == '1'"><PropertyGroup><AssemblyName>One</AssemblyName></PropertyGroup></When>
                <Otherwise><PropertyGroup><AssemblyName>Two</AssemblyName></PropertyGroup></Otherwise>
              </Choose>
            </Project>
            """));

        Assert.Null(ProjectIdentity.Read(fileSystem, "lib/Acme.Lib.csproj").PackageId);
    }

    [Theory]
    [InlineData("<Version>0.7.50</Version>", "0.7.50")]
    [InlineData("<VersionPrefix>1.2.3</VersionPrefix>", "1.2.3")]
    [InlineData("<VersionPrefix>1.2.3</VersionPrefix><VersionSuffix>beta.1</VersionSuffix>",
        "1.2.3-beta.1")]
    [InlineData("<Version>$(BaseVersion).1</Version>", null)]
    [InlineData("", null)]
    public void Identity_Version(string properties, string? expected) {
        var fileSystem = WithConsumer("", ("lib/Lib.csproj", Library(properties)));

        Assert.Equal(expected, ProjectIdentity.Read(fileSystem, "lib/Lib.csproj").Version);
    }

    [Fact]
    public void Identity_UnreadableProject_IsUnknown() {
        var fileSystem = WithConsumer("", ("lib/Lib.csproj", "<Project><PropertyGroup>"));

        Assert.Equal(ProjectIdentity.Unknown, ProjectIdentity.Read(fileSystem, "lib/Lib.csproj"));
        Assert.Equal(ProjectIdentity.Unknown,
            ProjectIdentity.Read(fileSystem, "lib/Missing.csproj"));
    }

    // --- finding the provider --------------------------------------------------------------------

    [Fact]
    public void FindProvider_UnconditionalReference_ToTheSamePackage_IsTheProvider() {
        var fileSystem = WithConsumer(
            """<ItemGroup><ProjectReference Include="..\src\VBeami.Core\VBeami.Core.csproj" />"""
                + """</ItemGroup>""",
            ("src/VBeami.Core/VBeami.Core.csproj",
                Library("<PackageId>VBeami.Core</PackageId><Version>0.7.50</Version>")));

        var provider = SourceProvidedPackages.FindProvider(fileSystem, Csproj, "VBeami.Core");

        Assert.NotNull(provider);
        Assert.Equal(@"..\src\VBeami.Core\VBeami.Core.csproj", provider.Include);
        Assert.Equal("src/VBeami.Core/VBeami.Core.csproj", provider.ProjectPath);
        Assert.Equal("0.7.50", provider.Identity.Version);
        Assert.True(provider.IsUnconditional);
    }

    [Fact]
    public void FindProvider_MatchesPackageIdCaseInsensitively_LikeNuGet() {
        var fileSystem = WithConsumer(
            """<ItemGroup><ProjectReference Include="../lib/Acme.Lib.csproj" /></ItemGroup>""",
            ("lib/Acme.Lib.csproj", Library()));

        Assert.NotNull(SourceProvidedPackages.FindProvider(fileSystem, Csproj, "acme.LIB"));
    }

    [Fact]
    public void FindProvider_ReferenceToADifferentPackage_IsNotAProvider() {
        var fileSystem = WithConsumer(
            """<ItemGroup><ProjectReference Include="../lib/Acme.Twitch.csproj" /></ItemGroup>""",
            ("lib/Acme.Twitch.csproj", Library()));

        Assert.Null(SourceProvidedPackages.FindProvider(fileSystem, Csproj, "Acme.Core"));
    }

    [Theory]
    [InlineData(
        """<ItemGroup>"""
            + """<ProjectReference Include="../lib/Acme.Lib.csproj" """
            + """Condition="'$(Configuration)' == 'Debug'" /></ItemGroup>""")]
    [InlineData(
        """<ItemGroup Condition="'$(Configuration)' == 'Debug'">"""
            + """<ProjectReference Include="../lib/Acme.Lib.csproj" /></ItemGroup>""")]
    [InlineData(
        """<Choose><When Condition="'$(X)' == '1'"><ItemGroup>"""
            + """<ProjectReference Include="../lib/Acme.Lib.csproj" /></ItemGroup></When>"""
            + """</Choose>""")]
    [InlineData(
        """<Choose><When Condition="'$(X)' == '1'" /><Otherwise><ItemGroup>"""
            + """<ProjectReference Include="../lib/Acme.Lib.csproj" /></ItemGroup></Otherwise>"""
            + """</Choose>""")]
    public void FindProvider_ConditionalReference_IsNotTheProvider_ButIsStillReported(
        string items) {
        // Debug-only source would leave Release with no reference at all if the package were
        // skipped.
        var fileSystem = WithConsumer(items, ("lib/Acme.Lib.csproj", Library()));

        Assert.Null(SourceProvidedPackages.FindProvider(fileSystem, Csproj, "Acme.Lib"));

        var reference = Assert.Single(SourceProvidedPackages.ReferencesTo(fileSystem, Csproj,
            "Acme.Lib"));
        Assert.False(reference.IsUnconditional);
        Assert.False(string.IsNullOrWhiteSpace(reference.Condition));
    }

    [Theory]
    [InlineData("../lib/*.csproj")]
    [InlineData("$(LibRoot)/Acme.Lib.csproj")]
    [InlineData("@(Libraries)")]
    public void FindProvider_UnevaluableInclude_IsNotAProvider(string include) {
        var fileSystem = WithConsumer(
            $"""<ItemGroup><ProjectReference Include="{include}" /></ItemGroup>""",
            ("lib/Acme.Lib.csproj", Library()));

        Assert.Null(SourceProvidedPackages.FindProvider(fileSystem, Csproj, "Acme.Lib"));
    }

    [Fact]
    public void FindProvider_MissingTarget_IsNotAProvider() {
        // Named like the package, so a file-name guess would match: the target has to exist and be
        // read.
        var fileSystem =
            WithConsumer(
                """<ItemGroup><ProjectReference Include="../lib/Acme.Lib.csproj" /></ItemGroup>""");

        Assert.Null(SourceProvidedPackages.FindProvider(fileSystem, Csproj, "Acme.Lib"));
    }

    [Fact]
    public void FindProvider_SemicolonSeparatedIncludes_AreEachConsidered() {
        var fileSystem = WithConsumer(
            """<ItemGroup>"""
                + """<ProjectReference """
                + """Include="../lib/Acme.Twitch.csproj;../lib/Acme.Lib.csproj" /></ItemGroup>""",
            ("lib/Acme.Twitch.csproj", Library()),
            ("lib/Acme.Lib.csproj", Library()));

        Assert.Equal("../lib/Acme.Lib.csproj",
            SourceProvidedPackages.FindProvider(fileSystem, Csproj, "Acme.Lib")?.Include);
    }

    [Fact]
    public void FindProvider_ReferenceTheFileRemovesAgain_IsNotAProvider() {
        var fileSystem = WithConsumer(
            """
            <ItemGroup><ProjectReference Include="../lib/Acme.Lib.csproj" /></ItemGroup>
            <ItemGroup><ProjectReference Remove="..\lib\Acme.Lib.csproj" /></ItemGroup>
            """,
            ("lib/Acme.Lib.csproj", Library()));

        Assert.Null(SourceProvidedPackages.FindProvider(fileSystem, Csproj, "Acme.Lib"));
    }

    [Fact]
    public void ReadAll_UnreadableOrMissingConsumer_HasNoReferences() {
        var fileSystem = new InMemoryFileSystem();
        fileSystem.WriteAllText(Csproj, "<Project>");

        Assert.Empty(SourceProvidedPackages.ReadAll(fileSystem, Csproj));
        Assert.Empty(SourceProvidedPackages.ReadAll(fileSystem, "nowhere/Game.csproj"));
    }

    [Fact]
    public void ReadAll_DoesNotWriteAnything() {
        var fileSystem = WithConsumer(
            """<ItemGroup><ProjectReference Include="../lib/Acme.Lib.csproj" /></ItemGroup>""",
            ("lib/Acme.Lib.csproj", Library()));
        var before = fileSystem.AllFilePaths().Order().Select(p => (p, fileSystem.ReadAllText(p)))
            .ToList();

        SourceProvidedPackages.ReadAll(fileSystem, Csproj);

        Assert.Equal(before, fileSystem.AllFilePaths().Order()
            .Select(p => (p, fileSystem.ReadAllText(p))).ToList());
    }
}
