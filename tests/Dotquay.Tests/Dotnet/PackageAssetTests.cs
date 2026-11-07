using Dotquay.Core.Lockfile;
using Dotquay.Core.Manifest;
using Dotquay.Core.Workflows;
using Dotquay.Tests.Support;
using Xunit;

namespace Dotquay.Tests.Dotnet;

/// <summary>
/// Covers <c>IncludeAssets</c>/<c>ExcludeAssets</c>/<c>PrivateAssets</c> passthrough
/// (docs/DESIGN-v0.10-PACKAGE-ASSETS.md) from manifest to lockfile to <c>.csproj</c>.
/// </summary>
/// <remarks>
/// The motivating package is <c>gdUnit4.analyzers</c>, which needs <c>PrivateAssets=none</c> —
/// deliberately the opposite of the conventional analyzer wiring — so a fixture using the
/// conventional <c>all</c> would pass while the case the feature exists for stayed broken.
/// </remarks>
public class PackageAssetTests {
    private const string ProjectDirectory = "project";

    private const string GdUnitManifest = """
        [dotnet."gdUnit4.analyzers"]
        version = "1.0.0"
        private_assets = ["none"]
        include_assets = ["runtime", "build", "native", "contentfiles", "analyzers", "buildtransitive"]
        """;

    // ---- manifest reading -------------------------------------------------

    [Fact]
    public void Read_TheGdUnitAnalyzerEntry_CarriesBothLists() {
        var spec = ManifestReader.ReadProject(GdUnitManifest).Dotnet["gdUnit4.analyzers"];

        Assert.Equal(["none"], spec.Assets.Private);
        Assert.Equal(
            ["runtime", "build", "native", "contentFiles", "analyzers", "buildTransitive"],
            spec.Assets.Include);
        Assert.Null(spec.Assets.Exclude);
    }

    [Fact]
    public void Read_TagCasing_IsCanonicalized() {
        // gdUnit4's own README writes these lowercase; MSBuild does not care, but a stable spelling
        // means a hand-written file and a dotquay-written one compare equal instead of reading as
        // drift forever.
        var spec = ManifestReader.ReadProject(GdUnitManifest).Dotnet["gdUnit4.analyzers"];

        Assert.Contains("contentFiles", spec.Assets.Include!);
        Assert.Contains("buildTransitive", spec.Assets.Include!);
    }

    [Fact]
    public void Read_UndeclaredLists_AreNullNotEmpty() {
        // Null is "dotquay has no opinion, leave the .csproj alone" — materially different from an
        // empty list, which would mean "write nothing here", and different again from NuGet's
        // default.
        var spec = ManifestReader.ReadProject("""
            [dotnet."Newtonsoft.Json"]
            version = "13.0.*"
            """).Dotnet["Newtonsoft.Json"];

        Assert.True(spec.Assets.IsEmpty);
        Assert.Null(spec.Assets.Include);
    }

    [Theory]
    [InlineData("""include_assets = ["all", "runtime"]""", "all")]
    [InlineData("""private_assets = ["runtime", "none"]""", "none")]
    public void Read_AllOrNoneMixedWithOtherTags_IsRejected(string line, string offender) {
        // NuGet's rule: 'all' and 'none' must appear by themselves. A mixed list has no meaning
        // MSBuild will honour, so guessing one would be worse than refusing.
        var ex = Assert.Throws<ManifestParseException>(() => ManifestReader.ReadProject($"""
            [dotnet."Some.Package"]
            version = "1.0.0"
            {line}
            """));

        Assert.Contains(offender, ex.Message);
    }

    [Theory]
    [InlineData("""private_assets = "none" """)]
    [InlineData("""private_assets = [1, 2]""")]
    [InlineData("""private_assets = []""")]
    public void Read_StructurallyWrongValues_AreRejected(string line) {
        Assert.Throws<ManifestParseException>(() => ManifestReader.ReadProject($"""
            [dotnet."Some.Package"]
            version = "1.0.0"
            {line}
            """));
    }

    [Fact]
    public void Read_AnUnknownTag_IsKeptAndReportedRatherThanRejected() {
        // 'analyzer' (singular) is the realistic typo. Refusing it would also refuse any tag
        // MSBuild adds after this code was written, so it passes through and is surfaced as a
        // warning.
        var spec = ManifestReader.ReadProject("""
            [dotnet."Some.Package"]
            version = "1.0.0"
            private_assets = ["analyzer"]
            """).Dotnet["Some.Package"];

        Assert.Equal(["analyzer"], spec.Assets.Private);
        Assert.Equal(["analyzer"], spec.Assets.UnknownTags);
    }

    [Fact]
    public void Read_KnownTags_ProduceNoUnknownWarnings() {
        var spec = ManifestReader.ReadProject(GdUnitManifest).Dotnet["gdUnit4.analyzers"];

        Assert.Empty(spec.Assets.UnknownTags);
    }

    [Fact]
    public void Read_APackageNamedLikeAnAssetKey_IsStillAPackage() {
        // The disambiguation is by TOML type: a metadata key is an array, while a dotted package ID
        // segment is a table. One key cannot be both, which is the same rule 'source' relies on.
        var manifest = ManifestReader.ReadProject("""
            [dotnet.Foo.include_assets]
            version = "2.0.0"
            """);

        Assert.True(manifest.Dotnet.ContainsKey("Foo.include_assets"));
        Assert.Equal("2.0.0", manifest.Dotnet["Foo.include_assets"].Version);
    }

    [Fact]
    public void Read_AssetKeysOnANonLeafTable_AreIgnored() {
        // A table without a 'version' is a path segment, not a package. Its keys describe nothing.
        var manifest = ManifestReader.ReadProject("""
            [dotnet.Foo]
            include_assets = ["runtime"]

            [dotnet.Foo.Bar]
            version = "1.0.0"
            """);

        Assert.False(manifest.Dotnet.ContainsKey("Foo"));
        Assert.True(manifest.Dotnet["Foo.Bar"].Assets.IsEmpty);
    }

    // ---- manifest writing -------------------------------------------------

    [Fact]
    public void Serialize_ThenRead_RoundTripsEveryList() {
        var manifest = new ProjectManifest {
            Dotnet = new Dictionary<string, DotnetSpec> {
                ["gdUnit4.analyzers"] = new DotnetSpec {
                    Name = "gdUnit4.analyzers",
                    Version = "1.0.0",
                    Assets = new PackageAssets {
                        Include = ["runtime", "build"],
                        Exclude = ["compile"],
                        Private = ["none"],
                    },
                },
            },
        };

        var roundTripped = ManifestReader.ReadProject(ManifestWriter.Serialize(manifest));

        Assert.True(manifest.Dotnet["gdUnit4.analyzers"].Assets
            .SameAs(roundTripped.Dotnet["gdUnit4.analyzers"].Assets));
    }

    [Fact]
    public void Serialize_APackageWithNoAssets_WritesNoAssetKeys() {
        var manifest = new ProjectManifest {
            Dotnet = new Dictionary<string, DotnetSpec> {
                ["Newtonsoft.Json"] = new DotnetSpec {
                    Name = "Newtonsoft.Json",
                    Version = "13.0.*"
                },
            },
        };

        Assert.DoesNotContain("assets", ManifestWriter.Serialize(manifest));
    }

    // ---- lockfile ---------------------------------------------------------

    [Fact]
    public void Lockfile_RoundTripsWhatDotquayWrote() {
        var lockfile = new DotquayLockfile {
            Dotnet =
            [
                new LockedDotnet
                {
                    Name = "gdUnit4.analyzers",
                    Resolved = "1.0.0",
                    Assets = new PackageAssets { Private = ["none"],
                        Include = ["runtime", "analyzers"] },
                },
            ],
        };

        var roundTripped = LockfileReader.Read(LockfileWriter.Serialize(lockfile), "dotquay.lock");

        Assert.True(lockfile.Dotnet[0].Assets.SameAs(roundTripped.Dotnet[0].Assets));
    }

    [Fact]
    public void Lockfile_WrittenBeforeV010_ReadsAsNothingWritten() {
        var lockfile = LockfileReader.Read("""
            [[dotnet]]
            name = "Newtonsoft.Json"
            resolved = "13.0.3"
            """, "dotquay.lock");

        Assert.True(lockfile.Dotnet[0].Assets.IsEmpty);
    }

    [Fact]
    public void Lockfile_NothingWritten_ProducesNoAssetKeys() {
        var text = LockfileWriter.Serialize(new DotquayLockfile {
            Dotnet = [new LockedDotnet { Name = "Newtonsoft.Json", Resolved = "13.0.3" }],
        });

        // A pre-v0.10 lockfile must survive an upgrade with no diff at all.
        Assert.DoesNotContain("assets", text);
    }

    // ---- doctor -----------------------------------------------------------

    private const string Csproj = """
        <Project Sdk="Godot.NET.Sdk/4.3.0">
          <PropertyGroup>
            <TargetFramework>net8.0</TargetFramework>
          </PropertyGroup>
          <ItemGroup>
            <PackageReference Include="gdUnit4.analyzers" Version="1.0.0" />
          </ItemGroup>
        </Project>
        """;

    private static async Task<IReadOnlyList<DoctorIssue>> DriftAsync(string csproj) {
        var fileSystem = new InMemoryFileSystem();
        fileSystem.WriteAllText(fileSystem.CombinePath(ProjectDirectory, "dotquay.toml"),
            GdUnitManifest);
        fileSystem.WriteAllText(fileSystem.CombinePath(ProjectDirectory, "dotquay.lock"), """
            [[dotnet]]
            name = "gdUnit4.analyzers"
            resolved = "1.0.0"
            """);
        fileSystem.WriteAllText(fileSystem.CombinePath(ProjectDirectory, "Game.csproj"), csproj);

        var workflow = new DependencyWorkflow(fileSystem, new FakeGitClient(), cacheRoot: "cache");
        var result = await workflow.DoctorAsync(ProjectDirectory);

        return result.Issues.Where(i => i.Kind == DoctorIssueKind.PackageAssetMetadataDrift)
            .ToList();
    }

    [Fact]
    public async Task Doctor_ACsprojMissingDeclaredMetadata_ReportsDrift() {
        // This is what a manifest edit followed by 'install' leaves behind: install replays the
        // lockfile by design, so the declaration lands nowhere until 'update' runs. Without this
        // check that is completely silent.
        var issues = await DriftAsync(Csproj);

        var issue = Assert.Single(issues);
        Assert.Contains("gdUnit4.analyzers", issue.Message);
    }

    [Fact]
    public async Task Doctor_ACsprojCarryingTheDeclaredMetadata_IsClean() {
        var issues = await DriftAsync("""
            <Project Sdk="Godot.NET.Sdk/4.3.0">
              <ItemGroup>
                <PackageReference Include="gdUnit4.analyzers" Version="1.0.0">
                  <PrivateAssets>none</PrivateAssets>
                  <IncludeAssets>runtime;build;native;contentFiles;analyzers;buildTransitive</IncludeAssets>
                </PackageReference>
              </ItemGroup>
            </Project>
            """);

        Assert.Empty(issues);
    }

    [Fact]
    public async Task Doctor_MetadataWrittenAsAttributes_IsAlsoClean() {
        // Attribute form means the same thing to MSBuild, so it must mean the same thing here — or
        // doctor reports permanent drift on a file that is actually correct.
        var issues = await DriftAsync("""
            <Project Sdk="Godot.NET.Sdk/4.3.0">
              <ItemGroup>
                <PackageReference Include="gdUnit4.analyzers" Version="1.0.0"
                                  PrivateAssets="none"
                                  IncludeAssets="runtime; build; native; contentfiles; analyzers; buildtransitive" />
              </ItemGroup>
            </Project>
            """);

        Assert.Empty(issues);
    }

    [Fact]
    public async Task Doctor_AProjectWithNoCsproj_IsNotUnhealthy() {
        var fileSystem = new InMemoryFileSystem();
        fileSystem.WriteAllText(fileSystem.CombinePath(ProjectDirectory, "dotquay.toml"),
            GdUnitManifest);
        fileSystem.WriteAllText(fileSystem.CombinePath(ProjectDirectory, "dotquay.lock"), """
            [[dotnet]]
            name = "gdUnit4.analyzers"
            resolved = "1.0.0"
            """);

        var workflow = new DependencyWorkflow(fileSystem, new FakeGitClient(), cacheRoot: "cache");
        var result = await workflow.DoctorAsync(ProjectDirectory);

        // A GDScript-only project is perfectly healthy. Complaining here would train people to
        // ignore doctor, which is the one thing it cannot afford.
        Assert.DoesNotContain(result.Issues, i =>
            i.Kind == DoctorIssueKind.PackageAssetMetadataDrift);
    }
}
