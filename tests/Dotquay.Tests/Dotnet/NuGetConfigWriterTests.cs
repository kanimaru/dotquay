using Dotquay.Core.Dotnet;
using Dotquay.Core.Manifest;
using Dotquay.Tests.Support;
using Xunit;

namespace Dotquay.Tests.Dotnet;

/// <summary>
/// Covers <see cref="NuGetConfigWriter"/> in isolation: creating a fresh <c>NuGet.Config</c>,
/// upserting the sources dotquay owns without disturbing anything else already there, pruning the
/// ones it no longer owns, respecting an existing lowercase <c>nuget.config</c>, and binding
/// restore to the right feed via <c>&lt;packageSourceMapping&gt;</c>.
/// </summary>
public class NuGetConfigWriterTests {
    private const string SourceUrl =
        "https://gitlab.example.com/api/v4/projects/1/packages/nuget/index.json";
    private const string SecondUrl =
        "https://gitlab.example.com/api/v4/projects/2/packages/nuget/index.json";

    private static NuGetSourceSet Legacy(string url = SourceUrl) =>
        NuGetSourceSet.From([new NuGetSourceSpec(NuGetSourceSpec.LegacyName, url)]);

    [Fact]
    public void EnsureSources_NoExistingConfig_CreatesOneWithTheSource() {
        var fileSystem = new InMemoryFileSystem();

        NuGetConfigWriter.EnsureSources(fileSystem, "project", Legacy());

        var config = fileSystem.ReadAllText("project/NuGet.Config");
        Assert.Contains($"key=\"{NuGetConfigWriter.DotquaySourceName}\"", config);
        Assert.Contains($"value=\"{SourceUrl}\"", config);
    }

    [Fact]
    public void EnsureSources_LegacyNamedSource_KeepsTheBareDotquayKey() {
        var fileSystem = new InMemoryFileSystem();

        NuGetConfigWriter.EnsureSources(fileSystem, "project", Legacy());

        // Grandfathered on purpose: pre-v0.7 projects already have <add key="dotquay"> committed,
        // and renaming it to "dotquay-default" would produce a spurious diff in every one of them.
        Assert.Contains("key=\"dotquay\"", fileSystem.ReadAllText("project/NuGet.Config"));
        Assert.DoesNotContain("key=\"dotquay-default\"",
            fileSystem.ReadAllText("project/NuGet.Config"));
    }

    [Fact]
    public void EnsureSources_NamedSources_GetPrefixedKeys() {
        var fileSystem = new InMemoryFileSystem();
        var sources = NuGetSourceSet.From(
            [new NuGetSourceSpec("core", SourceUrl), new NuGetSourceSpec("tools", SecondUrl)]);

        NuGetConfigWriter.EnsureSources(fileSystem, "project", sources);

        var config = fileSystem.ReadAllText("project/NuGet.Config");
        Assert.Contains("key=\"dotquay-core\"", config);
        Assert.Contains("key=\"dotquay-tools\"", config);
        Assert.Contains(SourceUrl, config);
        Assert.Contains(SecondUrl, config);
    }

    [Fact]
    public void EnsureSources_ExistingConfigWithOtherSources_AddsWithoutDisturbingThem() {
        var fileSystem = new InMemoryFileSystem();
        fileSystem.WriteAllText("project/NuGet.Config", """
            <configuration>
              <packageSources>
                <add key="corp-mirror" value="https://mirror.example.com/v3/index.json" />
              </packageSources>
            </configuration>
            """);

        NuGetConfigWriter.EnsureSources(fileSystem, "project", Legacy());

        var config = fileSystem.ReadAllText("project/NuGet.Config");
        Assert.Contains("corp-mirror", config);
        Assert.Contains("mirror.example.com", config);
        Assert.Contains(SourceUrl, config);
    }

    [Fact]
    public void EnsureSources_SourceRemovedFromTheManifest_IsPrunedFromTheConfig() {
        var fileSystem = new InMemoryFileSystem();
        var both = NuGetSourceSet.From(
            [new NuGetSourceSpec("core", SourceUrl), new NuGetSourceSpec("tools", SecondUrl)]);

        NuGetConfigWriter.EnsureSources(fileSystem, "project", both);
        NuGetConfigWriter.EnsureSources(fileSystem, "project",
            NuGetSourceSet.From([new NuGetSourceSpec("core", SourceUrl)]));

        // Pruning matters as much as adding: a feed left behind here would keep being searched by
        // dotnet restore forever, quietly undoing its removal from dotquay.toml.
        var config = fileSystem.ReadAllText("project/NuGet.Config");
        Assert.Contains("key=\"dotquay-core\"", config);
        Assert.DoesNotContain("key=\"dotquay-tools\"", config);
        Assert.DoesNotContain(SecondUrl, config);
    }

    [Fact]
    public void EnsureSources_PruningNeverTouchesSourcesDotquayDoesNotOwn() {
        var fileSystem = new InMemoryFileSystem();
        fileSystem.WriteAllText("project/NuGet.Config", """
            <configuration>
              <packageSources>
                <add key="corp-mirror" value="https://mirror.example.com/v3/index.json" />
                <add key="dotquay-gone" value="https://old.example.com/v3/index.json" />
              </packageSources>
            </configuration>
            """);

        NuGetConfigWriter.EnsureSources(fileSystem, "project", Legacy());

        var config = fileSystem.ReadAllText("project/NuGet.Config");
        Assert.Contains("corp-mirror", config);
        Assert.DoesNotContain("dotquay-gone", config);
    }

    [Fact]
    public void EnsureSources_CalledTwiceWithDifferentUrls_UpdatesInPlaceRatherThanDuplicating() {
        var fileSystem = new InMemoryFileSystem();
        NuGetConfigWriter.EnsureSources(fileSystem, "project", Legacy());
        NuGetConfigWriter.EnsureSources(fileSystem, "project", Legacy(SecondUrl));

        var config = fileSystem.ReadAllText("project/NuGet.Config");
        Assert.DoesNotContain(SourceUrl, config);
        Assert.Contains(SecondUrl, config);
        Assert.Equal(1, config.Split($"key=\"{NuGetConfigWriter.DotquaySourceName}\"").Length - 1);
    }

    [Fact]
    public void EnsureSources_ExistingLowercaseConfig_UpdatesItRatherThanCreatingASecondFile() {
        var fileSystem = new InMemoryFileSystem();
        fileSystem.WriteAllText("project/nuget.config",
            "<configuration><packageSources /></configuration>");

        NuGetConfigWriter.EnsureSources(fileSystem, "project", Legacy());

        Assert.False(fileSystem.FileExists("project/NuGet.Config"));
        Assert.Contains(SourceUrl, fileSystem.ReadAllText("project/nuget.config"));
    }

    [Fact]
    public void EnsureSources_NeverWritesAnyCredentialAttribute() {
        var fileSystem = new InMemoryFileSystem();

        NuGetConfigWriter.EnsureSources(fileSystem, "project", Legacy());

        var config = fileSystem.ReadAllText("project/NuGet.Config");
        Assert.DoesNotContain("<packageSourceCredentials", config);
        Assert.DoesNotContain("Username", config);
        Assert.DoesNotContain("Password", config);
    }

    [Fact]
    public void EnsureSources_MalformedExistingConfig_ThrowsDotnetSyncException() {
        var fileSystem = new InMemoryFileSystem();
        fileSystem.WriteAllText("project/NuGet.Config", "not xml <<<");

        Assert.Throws<DotnetSyncException>(() =>
            NuGetConfigWriter.EnsureSources(fileSystem, "project", Legacy()));
    }

    [Fact]
    public void EnsureSources_Mapping_PinsThePackageAndAlwaysWritesTheCatchAll() {
        var fileSystem = new InMemoryFileSystem();
        var sources = NuGetSourceSet.From([new NuGetSourceSpec("core", SourceUrl)]);

        NuGetConfigWriter.EnsureSources(
            fileSystem, "project", sources,
            new Dictionary<string, string> { ["Acme.Internal"] = "core" });

        var config = fileSystem.ReadAllText("project/NuGet.Config");
        Assert.Contains("<packageSourceMapping>", config);
        Assert.Contains("pattern=\"Acme.Internal\"", config);

        // The catch-all is mandatory, not a nicety: packageSourceMapping is all-or-nothing per
        // config, so any package matching no pattern fails restore outright with NU1100.
        Assert.Contains("pattern=\"*\"", config);
    }

    [Fact]
    public void EnsureSources_Mapping_DeclaresNuGetOrgExplicitlyForTheCatchAllToPointAt() {
        var fileSystem = new InMemoryFileSystem();

        NuGetConfigWriter.EnsureSources(fileSystem, "project", Legacy());

        // A mapping key must name a source declared in the same file — it cannot be inherited from
        // machine-level config.
        var config = fileSystem.ReadAllText("project/NuGet.Config");
        Assert.Contains($"key=\"{NuGetSourceSpec.PublicName}\" "
            + $"value=\"{NuGetSourceSpec.PublicUrl}\"", config);
    }

    [Fact]
    public void EnsureSources_MappingDisabled_WritesNoMappingAndNoImplicitPublicSource() {
        var fileSystem = new InMemoryFileSystem();

        NuGetConfigWriter.EnsureSources(
            fileSystem, "project", Legacy(), pinnedPackages: null, writeSourceMapping: false);

        var config = fileSystem.ReadAllText("project/NuGet.Config");
        Assert.DoesNotContain("packageSourceMapping", config);
        Assert.DoesNotContain($"key=\"{NuGetSourceSpec.PublicName}\"", config);
    }

    [Fact]
    public void EnsureSources_MappingRewrittenOnEachCall_RatherThanAccumulating() {
        var fileSystem = new InMemoryFileSystem();
        var sources = NuGetSourceSet.From([new NuGetSourceSpec("core", SourceUrl)]);

        NuGetConfigWriter.EnsureSources(
            fileSystem, "project", sources,
            new Dictionary<string, string> { ["Acme.Internal"] = "core" });
        NuGetConfigWriter.EnsureSources(
            fileSystem, "project", sources,
            new Dictionary<string, string> { ["Acme.Other"] = "core" });

        var config = fileSystem.ReadAllText("project/NuGet.Config");
        Assert.Equal(1, config.Split("<packageSourceMapping>").Length - 1);
        Assert.DoesNotContain("Acme.Internal", config);
        Assert.Contains("Acme.Other", config);
    }
}
