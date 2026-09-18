using Dotquay.Core.Lockfile;
using Dotquay.Core.Sync;
using Dotquay.Tests.Support;
using Xunit;

namespace Dotquay.Tests.Sync;

/// <summary>
/// Covers <see cref="InstalledPackageManifests"/>, the single reader doctor, the graph view and
/// <c>remove</c> share for "what does this installed addon declare?" (DESIGN-v0.19 §3.1).
/// </summary>
public class InstalledPackageManifestsTests
{
    private const string ProjectDirectory = "project";

    private static LockedAddon Addon(string name, string? folder = null) => new()
    {
        Name = name,
        Git = $"https://example.com/{name}.git",
        Resolved = new string('a', 40),
        Path = $"addons/{name}",
        Folder = folder,
    };

    private static (InstalledPackageManifests Reader, InMemoryFileSystem FileSystem) Build()
    {
        var fileSystem = new InMemoryFileSystem();
        return (new InstalledPackageManifests(fileSystem), fileSystem);
    }

    private static void Install(InMemoryFileSystem fileSystem, string folder, string manifest) =>
        fileSystem.WriteAllText(fileSystem.CombinePath(ProjectDirectory, "addons", folder, "dotquay.toml"), manifest);

    private const string DeclaresTwo = """
        [package]
        name = "a"
        version = "1.0.0"

        [dotnet.Serilog]
        version = "3.*"

        [dotnet.Newtonsoft.Json]
        version = "13.0.*"
        """;

    [Fact]
    public void DotnetDeclarationsByAddon_ReturnsEachAddonsDeclaredPackages()
    {
        var (reader, fileSystem) = Build();
        Install(fileSystem, "a", DeclaresTwo);

        var declarations = reader.DotnetDeclarationsByAddon(ProjectDirectory, [Addon("a")]);

        // The dotted ID has to survive intact: the manifest reader walks TOML's dotted tables, and
        // "Newtonsoft.Json" split into two keys would be a package nobody declared.
        Assert.Equal(["Serilog", "Newtonsoft.Json"], declarations["a"].Select(d => d.Name).ToList());
        Assert.Equal("3.*", declarations["a"][0].Version);
    }

    [Fact]
    public void DotnetDeclarationsByAddon_FollowsInstallAs()
    {
        var (reader, fileSystem) = Build();
        Install(fileSystem, "renamed", DeclaresTwo);

        var declarations = reader.DotnetDeclarationsByAddon(ProjectDirectory, [Addon("a", folder: "renamed")]);

        Assert.True(declarations.ContainsKey("a"));
    }

    [Fact]
    public void DotnetDeclarationsByAddon_SkipsAddonsThatDeclareNothing()
    {
        var (reader, fileSystem) = Build();
        Install(fileSystem, "plain", "[package]\nname = \"plain\"\nversion = \"1.0.0\"\n");

        var declarations = reader.DotnetDeclarationsByAddon(ProjectDirectory, [Addon("plain"), Addon("missing")]);

        Assert.Empty(declarations);
    }

    [Fact]
    public void TryRead_MalformedManifest_IsNull()
    {
        var (reader, fileSystem) = Build();
        Install(fileSystem, "broken", "this is [not toml");

        Assert.Null(reader.TryRead(ProjectDirectory, "broken"));
    }

    [Fact]
    public void TryRead_NoManifest_IsNull()
    {
        var (reader, _) = Build();

        Assert.Null(reader.TryRead(ProjectDirectory, "nothing-here"));
    }
}
