using Dotquay.Core.Workflows;
using Dotquay.Tests.Support;
using Xunit;

namespace Dotquay.Tests.Workflows;

/// <summary>
/// Covers <c>dotquay doctor</c>'s check for an installed addon shipping an unreadable package
/// manifest.
/// </summary>
/// <remarks>
/// This is the offline half of the same problem <see cref="Resolve.GraphWarningTests"/> covers.
/// It matters on its own because it needs no network and no re-resolve: the installed
/// <c>addons/&lt;name&gt;/</c> folder <em>is</em> the subtree the graph walk would extract, so
/// whatever is wrong with the manifest is already sitting on disk waiting to be read. Someone asking
/// "why isn't this dependency showing up" can get the answer from a command that works on a plane.
/// </remarks>
public class DoctorPackageManifestTests
{
    private const string ProjectDirectory = "project";
    private const string AddonUrl = "https://example.com/leaf.git";

    private static string Sha => new('a', 40);

    private static (DependencyWorkflow Workflow, InMemoryFileSystem FileSystem) Build(string? addonManifest)
    {
        var fileSystem = new InMemoryFileSystem();

        fileSystem.WriteAllText(fileSystem.CombinePath(ProjectDirectory, "dotquay.toml"), $"""
            [project]
            godot = "4.7"

            [addons.leaf]
            git = "{AddonUrl}"
            version = "~1.0"
            """);

        fileSystem.WriteAllText(fileSystem.CombinePath(ProjectDirectory, "dotquay.lock"), $"""
            [[addon]]
            name = "leaf"
            git = "{AddonUrl}"
            resolved = "{Sha}"
            version = "v1.0.0"
            path = "addons/leaf"
            """);

        fileSystem.WriteAllText(fileSystem.CombinePath(ProjectDirectory, "addons", "leaf", "plugin.cfg"), "cfg");

        if (addonManifest is not null)
        {
            fileSystem.WriteAllText(fileSystem.CombinePath(ProjectDirectory, "addons", "leaf", "dotquay.toml"), addonManifest);
        }

        var workflow = new DependencyWorkflow(fileSystem, new FakeGitClient(), cacheRoot: "cache");
        return (workflow, fileSystem);
    }

    private static async Task<IReadOnlyList<DoctorIssue>> RunAsync(string? addonManifest)
    {
        var (workflow, _) = Build(addonManifest);
        var result = await workflow.DoctorAsync(ProjectDirectory);

        return result.Issues.Where(i => i.Kind == DoctorIssueKind.UnreadablePackageManifest).ToList();
    }

    [Fact]
    public async Task DoctorAsync_AddonManifestWithoutAPackageHeader_IsReported()
    {
        var issues = await RunAsync("""
            [project]
            godot = "4.7"

            [addons.child]
            git = "https://example.com/child.git"
            version = "~1.0"
            """);

        var issue = Assert.Single(issues);
        Assert.Contains("leaf", issue.Message);
        Assert.Contains("[package]", issue.Message);
    }

    [Fact]
    public async Task DoctorAsync_MalformedTomlInAnAddon_IsReported()
    {
        var issues = await RunAsync("this is not valid toml = = =");

        Assert.Single(issues);
    }

    [Fact]
    public async Task DoctorAsync_PackageManifestMissingRequiredKeys_IsReported()
    {
        var issues = await RunAsync("""
            [package]
            name = "leaf"
            """);

        // [package] with no version is exactly as unusable as no header at all — ManifestReader
        // rejects it, so the graph walk would skip the addon's dependencies just the same.
        var issue = Assert.Single(issues);
        Assert.Contains("version", issue.Message);
    }

    [Fact]
    public async Task DoctorAsync_ValidPackageManifest_IsNotReported()
    {
        var issues = await RunAsync("""
            [package]
            name = "leaf"
            version = "1.0.0"

            [addons.child]
            git = "https://example.com/child.git"
            version = "~1.0"
            """);

        Assert.Empty(issues);
    }

    [Fact]
    public async Task DoctorAsync_AddonWithNoManifestAtAll_IsNotReported()
    {
        var issues = await RunAsync(addonManifest: null);

        // The overwhelmingly common case for third-party addons (SPEC.md §7.2). Flagging it would
        // make doctor useless noise on any real project.
        Assert.Empty(issues);
    }
}
