using Dependi.Core.Dotnet;
using Dependi.Tests.Support;
using Xunit;

namespace Dependi.Tests.Dotnet;

/// <summary>
/// Covers <see cref="NuGetConfigWriter"/> in isolation: creating a fresh <c>NuGet.Config</c>,
/// upserting the "dependi" source into an existing one without disturbing anything else already
/// there, and respecting an existing lowercase <c>nuget.config</c> instead of creating a second file.
/// </summary>
public class NuGetConfigWriterTests
{
    private const string SourceUrl = "https://gitlab.example.com/api/v4/projects/1/packages/nuget/index.json";

    [Fact]
    public void EnsureSource_NoExistingConfig_CreatesOneWithTheSource()
    {
        var fileSystem = new InMemoryFileSystem();

        NuGetConfigWriter.EnsureSource(fileSystem, "project", SourceUrl);

        var config = fileSystem.ReadAllText("project/NuGet.Config");
        Assert.Contains($"key=\"{NuGetConfigWriter.DependiSourceName}\"", config);
        Assert.Contains($"value=\"{SourceUrl}\"", config);
    }

    [Fact]
    public void EnsureSource_ExistingConfigWithOtherSources_AddsWithoutDisturbingThem()
    {
        var fileSystem = new InMemoryFileSystem();
        fileSystem.WriteAllText("project/NuGet.Config", """
            <configuration>
              <packageSources>
                <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
              </packageSources>
            </configuration>
            """);

        NuGetConfigWriter.EnsureSource(fileSystem, "project", SourceUrl);

        var config = fileSystem.ReadAllText("project/NuGet.Config");
        Assert.Contains("nuget.org", config);
        Assert.Contains("api.nuget.org", config);
        Assert.Contains(SourceUrl, config);
    }

    [Fact]
    public void EnsureSource_CalledTwiceWithDifferentUrls_UpdatesInPlaceRatherThanDuplicating()
    {
        var fileSystem = new InMemoryFileSystem();
        NuGetConfigWriter.EnsureSource(fileSystem, "project", SourceUrl);

        const string newUrl = "https://gitlab.example.com/api/v4/projects/2/packages/nuget/index.json";
        NuGetConfigWriter.EnsureSource(fileSystem, "project", newUrl);

        var config = fileSystem.ReadAllText("project/NuGet.Config");
        Assert.DoesNotContain(SourceUrl, config);
        Assert.Contains(newUrl, config);
        Assert.Equal(1, config.Split(NuGetConfigWriter.DependiSourceName).Length - 1);
    }

    [Fact]
    public void EnsureSource_ExistingLowercaseConfig_UpdatesItRatherThanCreatingASecondFile()
    {
        var fileSystem = new InMemoryFileSystem();
        fileSystem.WriteAllText("project/nuget.config", "<configuration><packageSources /></configuration>");

        NuGetConfigWriter.EnsureSource(fileSystem, "project", SourceUrl);

        Assert.False(fileSystem.FileExists("project/NuGet.Config"));
        Assert.Contains(SourceUrl, fileSystem.ReadAllText("project/nuget.config"));
    }

    [Fact]
    public void EnsureSource_NeverWritesAnyCredentialAttribute()
    {
        var fileSystem = new InMemoryFileSystem();

        NuGetConfigWriter.EnsureSource(fileSystem, "project", SourceUrl);

        var config = fileSystem.ReadAllText("project/NuGet.Config");
        Assert.DoesNotContain("<packageSourceCredentials", config);
        Assert.DoesNotContain("Username", config);
        Assert.DoesNotContain("Password", config);
    }

    [Fact]
    public void EnsureSource_MalformedExistingConfig_ThrowsDotnetSyncException()
    {
        var fileSystem = new InMemoryFileSystem();
        fileSystem.WriteAllText("project/NuGet.Config", "not xml <<<");

        Assert.Throws<DotnetSyncException>(() => NuGetConfigWriter.EnsureSource(fileSystem, "project", SourceUrl));
    }
}
