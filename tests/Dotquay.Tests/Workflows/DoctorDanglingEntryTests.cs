using Dotquay.Core.Workflows;
using Dotquay.Tests.Support;
using Xunit;

namespace Dotquay.Tests.Workflows;

/// <summary>
/// Covers <c>dotquay doctor</c>'s dangling-lockfile-entry check against a real dependency graph.
/// </summary>
/// <remarks>
/// The check originally asked "is this name in the project manifest?", which was right in v0.1 and
/// wrong the moment the dependency graph landed (v0.4): a transitive addon and an addon-declared
/// NuGet package (requirement P1-5) are both correctly locked while appearing in no manifest the
/// project owns. The result was doctor flagging every transitive dependency the instant the graph
/// feature started working — noise arriving exactly when things go right, which is the fastest way to
/// train someone to ignore a check.
/// </remarks>
public class DoctorDanglingEntryTests {
    private const string ProjectDirectory = "project";
    private const string RootUrl = "https://example.com/root.git";
    private const string ChildUrl = "https://example.com/child.git";

    private static string Sha(char c) => new(c, 40);

    /// <summary>A project declaring only <c>root</c>, whose lockfile also holds <c>child</c> as a
    /// transitive dependency plus a NuGet package <c>root</c> declares in its own package manifest.</summary>
    private static DependencyWorkflow Build(
        InMemoryFileSystem fileSystem,
        string projectManifest,
        string lockfile,
        string? installedRootPackageManifest) {
        fileSystem.WriteAllText(fileSystem.CombinePath(ProjectDirectory, "dotquay.toml"), projectManifest);
        fileSystem.WriteAllText(fileSystem.CombinePath(ProjectDirectory, "dotquay.lock"), lockfile);
        fileSystem.WriteAllText(fileSystem.CombinePath(ProjectDirectory, "addons", "root", "plugin.cfg"), "cfg");
        fileSystem.WriteAllText(fileSystem.CombinePath(ProjectDirectory, "addons", "child", "plugin.cfg"), "cfg");

        if (installedRootPackageManifest is not null) {
            fileSystem.WriteAllText(
                fileSystem.CombinePath(ProjectDirectory, "addons", "root", "dotquay.toml"), installedRootPackageManifest);
        }

        return new DependencyWorkflow(fileSystem, new FakeGitClient(), cacheRoot: "cache");
    }

    private static readonly string ProjectManifest = $"""
        [project]
        godot = "4.7"

        [addons.root]
        git = "{RootUrl}"
        version = "~1.0"
        """;

    /// <summary>Addon entries are kept contiguous and the dotnet block last, exactly as
    /// <c>LockfileWriter</c> emits them. That ordering is load-bearing for the reader, so a fixture
    /// that interleaves them wouldn't be testing a lockfile dotquay could ever produce.</summary>
    private static string LockfileWith(string extraAddons = "") => $"""
        [[addon]]
        name = "root"
        git = "{RootUrl}"
        resolved = "{Sha('a')}"
        version = "v1.0.0"
        path = "addons/root"
        dependencies = ["child"]

        [[addon]]
        name = "child"
        git = "{ChildUrl}"
        resolved = "{Sha('b')}"
        version = "v1.0.0"
        path = "addons/child"
        {extraAddons}
        [[dotnet]]
        name = "Newtonsoft.Json"
        resolved = "13.0.3"
        """;

    private static readonly string RootPackageManifest = """
        [package]
        name = "root"
        version = "1.0.0"

        [dotnet.Newtonsoft.Json]
        version = "13.0.*"
        """;

    private static async Task<IReadOnlyList<DoctorIssue>> DanglingAsync(
        string? installedRootPackageManifest, string? lockfileOverride = null) {
        var fileSystem = new InMemoryFileSystem();
        var workflow = Build(fileSystem, ProjectManifest, lockfileOverride ?? LockfileWith(), installedRootPackageManifest);
        var result = await workflow.DoctorAsync(ProjectDirectory);

        return result.Issues.Where(i => i.Kind == DoctorIssueKind.DanglingLockfileEntry).ToList();
    }

    [Fact]
    public async Task DoctorAsync_TransitiveAddon_IsNotDangling() {
        var issues = await DanglingAsync(RootPackageManifest);

        // 'child' is in no manifest the project owns, but 'root' requires it — the lockfile's own
        // dependency edges say so (SPEC.md §8), which is exactly what they exist for.
        Assert.DoesNotContain(issues, i => i.Message.Contains("child"));
    }

    [Fact]
    public async Task DoctorAsync_AddonDeclaredNuGetPackage_IsNotDangling() {
        var issues = await DanglingAsync(RootPackageManifest);

        // Unioned in from root's own package manifest (requirement P1-5). Read off disk, since the
        // package manifest ships inside addons/root/ — no network needed to answer this.
        Assert.DoesNotContain(issues, i => i.Message.Contains("Newtonsoft.Json"));
    }

    [Fact]
    public async Task DoctorAsync_AGraphWithNothingStale_IsClean() {
        var issues = await DanglingAsync(RootPackageManifest);

        Assert.Empty(issues);
    }

    [Fact]
    public async Task DoctorAsync_UnreachableAddon_IsStillReported() {
        var lockfile = LockfileWith($"""

            [[addon]]
            name = "ghost"
            git = "{ChildUrl}"
            resolved = "{Sha('c')}"
            path = "addons/ghost"

            """);

        var issues = await DanglingAsync(RootPackageManifest, lockfile);

        // The check still has to do its actual job: nothing declares or requires 'ghost'.
        var issue = Assert.Single(issues);
        Assert.Contains("ghost", issue.Message);
    }

    [Fact]
    public async Task DoctorAsync_NuGetPackageNoAddonDeclares_IsStillReported() {
        // root ships a package manifest, but it no longer mentions Newtonsoft.Json — so the locked
        // entry really is stale now.
        var issues = await DanglingAsync("""
            [package]
            name = "root"
            version = "1.0.0"
            """);

        var issue = Assert.Single(issues);
        Assert.Contains("Newtonsoft.Json", issue.Message);
    }

    [Fact]
    public async Task DoctorAsync_AddonWithNoPackageManifest_DoesNotVouchForNuGetEntries() {
        var issues = await DanglingAsync(installedRootPackageManifest: null);

        // Nothing on disk claims Newtonsoft.Json is needed, so reporting it is the honest answer —
        // silence here would let a genuinely stale entry hide behind a missing file.
        Assert.Contains(issues, i => i.Message.Contains("Newtonsoft.Json"));
        Assert.DoesNotContain(issues, i => i.Message.Contains("child"));
    }
}
