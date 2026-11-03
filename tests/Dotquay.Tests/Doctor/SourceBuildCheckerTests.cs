using Dotquay.Core.Doctor;
using Dotquay.Core.Dotnet;
using Dotquay.Core.Json;
using Dotquay.Core.Lockfile;
using Dotquay.Core.Manifest;
using Dotquay.Core.Sync;
using Dotquay.Core.Workflows;
using Dotquay.Tests.Support;
using Xunit;

namespace Dotquay.Tests.Doctor;

/// <summary>DESIGN-v0.26 §C: the two doctor kinds, and what they deliberately leave alone.</summary>
public class SourceBuildCheckerTests
{
    private const string CsprojPath = "project/Game.csproj";
    private const string Package = "Acme.Core";
    private const string Include = @"..\src\Acme.Core\Acme.Core.csproj";

    private static string Library(string version = "<Version>0.7.50</Version>") => $"""
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <PackageId>Acme.Core</PackageId>
            {version}
          </PropertyGroup>
        </Project>
        """;

    private static string Consumer(bool withPackageReference, string referenceAttributes = "") => $"""
        <Project Sdk="Godot.NET.Sdk/4.5.0">
          <ItemGroup>
            <ProjectReference Include="{Include}"{referenceAttributes} />
          </ItemGroup>
          {(withPackageReference ? $"""<ItemGroup><PackageReference Include="{Package}" Version="0.7.20" /></ItemGroup>""" : "")}
        </Project>
        """;

    private static DotquayLockfile Locked(string resolved = "0.7.20", string name = Package) => new()
    {
        Dotnet = [new LockedDotnet { Name = name, Resolved = resolved }],
    };

    private static InMemoryFileSystem Files(string consumer, string library)
    {
        var fileSystem = new InMemoryFileSystem();
        fileSystem.WriteAllText(CsprojPath, consumer);
        fileSystem.WriteAllText("src/Acme.Core/Acme.Core.csproj", library);
        return fileSystem;
    }

    [Fact]
    public void BothReferences_Unconditional_IsReported_WithTheFix()
    {
        var fileSystem = Files(Consumer(withPackageReference: true), Library("<Version>0.7.20</Version>"));

        var issue = Assert.Single(SourceBuildChecker.Check(fileSystem, CsprojPath, Locked()));

        Assert.Equal(DoctorIssueKind.PackageAlsoBuiltFromSource, issue.Kind);
        Assert.StartsWith("'Acme.Core' is built from source by ..\\src\\Acme.Core\\Acme.Core.csproj", issue.Message);
        Assert.Contains("dotquay install", issue.Message);
    }

    [Fact]
    public void BothReferences_Conditional_IsReported_AndLeftToTheAuthor()
    {
        var fileSystem = Files(
            Consumer(withPackageReference: true, referenceAttributes: " Condition=\"'$(Configuration)' == 'Debug'\""),
            Library("<Version>0.7.20</Version>"));

        var issue = Assert.Single(SourceBuildChecker.Check(fileSystem, CsprojPath, Locked()));

        Assert.Equal(DoctorIssueKind.PackageAlsoBuiltFromSource, issue.Kind);
        Assert.Contains("when '$(Configuration)' == 'Debug'", issue.Message);
        Assert.Contains("leaves this one to you", issue.Message);
        Assert.DoesNotContain("dotquay install", issue.Message);
    }

    [Fact]
    public void SourceVersionDiffersFromTheLock_IsReported_NamingBoth()
    {
        // vbeami today: 0.7.50 compiles from source, 0.7.20 is what consumers restore.
        var fileSystem = Files(Consumer(withPackageReference: false), Library());

        var issue = Assert.Single(SourceBuildChecker.Check(fileSystem, CsprojPath, Locked()));

        Assert.Equal(DoctorIssueKind.SourceBuildVersionMismatch, issue.Kind);
        Assert.StartsWith("'Acme.Core' compiles from source at 0.7.50", issue.Message);
        Assert.Contains("resolves 0.7.20", issue.Message);
    }

    [Fact]
    public void DuplicateAndMismatch_AreTwoSeparateIssues()
    {
        var fileSystem = Files(Consumer(withPackageReference: true), Library());

        var kinds = SourceBuildChecker.Check(fileSystem, CsprojPath, Locked()).Select(i => i.Kind).ToList();

        Assert.Equal([DoctorIssueKind.PackageAlsoBuiltFromSource, DoctorIssueKind.SourceBuildVersionMismatch], kinds);
    }

    [Theory]
    [InlineData("0.7.20", "0.7.20")]
    [InlineData("0.7.20.0", "0.7.20")]
    [InlineData("v0.7.20", "0.7.20")]
    [InlineData("0.7.20+build.5", "0.7.20")]
    [InlineData("1.0", "1.0.0")]
    [InlineData("1.0.0-Beta.1", "1.0.0-beta.1")]
    [InlineData("0.07.20", "0.7.20")]
    public void SameNuGetVersion_IsNotAMismatch(string source, string locked)
    {
        Assert.True(SourceBuildChecker.SameVersion(source, locked));

        var fileSystem = Files(Consumer(withPackageReference: false), Library($"<Version>{source}</Version>"));
        Assert.Empty(SourceBuildChecker.Check(fileSystem, CsprojPath, Locked(locked)));
    }

    [Theory]
    [InlineData("0.7.21", "0.7.20")]
    [InlineData("1.0.0-beta.1", "1.0.0")]
    [InlineData("1.0.0.1", "1.0.0")]
    public void DifferentVersions_AreAMismatch(string source, string locked)
    {
        Assert.False(SourceBuildChecker.SameVersion(source, locked));
    }

    [Fact]
    public void SourceWithoutALiteralVersion_IsNotReportedAsAMismatch()
    {
        // Nothing stated means MSBuild's default 1.0.0, which is not this project's statement about anything.
        var fileSystem = Files(Consumer(withPackageReference: false), Library(version: ""));

        Assert.Empty(SourceBuildChecker.Check(fileSystem, CsprojPath, Locked()));
    }

    [Fact]
    public void ConditionalProvider_IsNotCheckedForVersion()
    {
        var fileSystem = Files(
            Consumer(withPackageReference: false, referenceAttributes: " Condition=\"'$(Configuration)' == 'Debug'\""),
            Library());

        Assert.Empty(SourceBuildChecker.Check(fileSystem, CsprojPath, Locked()));
    }

    [Fact]
    public void PackageTheLockfileDoesNotRecord_IsNotDotquaysToReport()
    {
        var fileSystem = Files(Consumer(withPackageReference: true), Library());

        Assert.Empty(SourceBuildChecker.Check(fileSystem, CsprojPath, Locked(name: "Something.Else")));
    }

    [Fact]
    public void KindsHaveTheirWireNames()
    {
        Assert.Equal("package_also_built_from_source", JsonMapper.ToWire(DoctorIssueKind.PackageAlsoBuiltFromSource));
        Assert.Equal("source_build_version_mismatch", JsonMapper.ToWire(DoctorIssueKind.SourceBuildVersionMismatch));
    }

    [Fact]
    public async Task Doctor_ReportsBothKinds_ThroughTheWorkflow()
    {
        var fileSystem = Files(Consumer(withPackageReference: true), Library());
        fileSystem.WriteAllText("project/dotquay.toml", "[project]\n\n[dotnet.\"Acme.Core\"]\nversion = \"0.7.20\"\n");
        fileSystem.WriteAllText("project/dotquay.lock", LockfileWriter.Serialize(Locked()));

        var gitClient = new FakeGitClient { FileSystemForFetchedContent = fileSystem };
        var addonInstaller = new AddonInstaller(
            fileSystem, new RepoCache(fileSystem, gitClient, "cache"), new SyncEngine(fileSystem), new FakePatchTool(fileSystem));
        var workflow = new DependencyWorkflow(
            fileSystem, gitClient, new FakeNuGetClientFactory(new FakeNuGetClient()), addonInstaller,
            new DotnetInstaller(fileSystem, new FakeDotnetCliRunner(), TestCredentials.None()));

        var result = await workflow.DoctorAsync("project");

        Assert.Contains(result.Issues, i => i.Kind == DoctorIssueKind.PackageAlsoBuiltFromSource);
        Assert.Contains(result.Issues, i => i.Kind == DoctorIssueKind.SourceBuildVersionMismatch);
    }
}
