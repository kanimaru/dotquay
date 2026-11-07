using Dotquay.Core.Dotnet;
using Dotquay.Core.Workflows;
using Dotquay.Tests.Support;
using Xunit;

namespace Dotquay.Tests.Workflows;

/// <summary>
/// Covers <see cref="SourceWorkflow"/> — the manifest half of <c>dotquay source</c> (v0.14).
/// </summary>
/// <remarks>
/// Two of these assertions are security properties rather than ergonomics, and are the reason this
/// class exists at all. A new feed must land <em>last</em>, because declaration order is the
/// resolution rule and a prepending convenience command would silently re-point every unpinned
/// package in the project. And a feed that is still pinned must not be removable without saying so,
/// because dropping the declaration hands those packages back to a first-match search across
/// whatever feeds remain — both of them dependency confusion produced by a helper.
/// </remarks>
public class SourceWorkflowTests {
    private const string ProjectDirectory = "project";
    private const string CoreUrl =
        "https://gitlab.example.com/api/v4/projects/1/packages/nuget/index.json";
    private const string ToolsUrl =
        "https://gitlab.example.com/api/v4/projects/2/packages/nuget/index.json";

    private static (SourceWorkflow Workflow, InMemoryFileSystem FileSystem,
        FakeNuGetClient Client) Build(
        string manifest, string? lockfile = null) {
        var fileSystem = new InMemoryFileSystem();
        fileSystem.WriteAllText(fileSystem.CombinePath(ProjectDirectory, "dotquay.toml"), manifest);

        if (lockfile is not null) {
            fileSystem.WriteAllText(fileSystem.CombinePath(ProjectDirectory, "dotquay.lock"),
                lockfile);
        }

        var client = new FakeNuGetClient();
        return (new SourceWorkflow(fileSystem, new FakeNuGetClientFactory(client)), fileSystem,
            client);
    }

    private static string ReadManifest(InMemoryFileSystem fileSystem) =>
        fileSystem.ReadAllText(fileSystem.CombinePath(ProjectDirectory, "dotquay.toml"));

    private static string ReadLockfile(InMemoryFileSystem fileSystem) =>
        fileSystem.ReadAllText(fileSystem.CombinePath(ProjectDirectory, "dotquay.lock"));

    private const string EmptyManifest = """
        # Dotquay project manifest.
        """;

    private const string ManifestWithCore = $"""
        [nuget.sources.core]
        url = "{CoreUrl}"
        """;

    [Fact]
    public async Task Add_DeclaresTheFeed() {
        var (workflow, fileSystem, _) = Build(EmptyManifest);

        var result = await workflow.AddAsync(ProjectDirectory, "core", CoreUrl);

        Assert.Equal(SourceChange.Added, result.Change);
        Assert.Contains("[nuget.sources.core]", ReadManifest(fileSystem));
        Assert.Contains($"url = \"{CoreUrl}\"", ReadManifest(fileSystem));
    }

    /// <summary>The load-bearing one. First-match search order means position decides which feed a
    /// package comes from, so a second feed must never end up ahead of the first.</summary>
    [Fact]
    public async Task Add_AppendsAfterExistingFeeds() {
        var (workflow, fileSystem, _) = Build(ManifestWithCore);

        await workflow.AddAsync(ProjectDirectory, "tools", ToolsUrl);

        var manifest = ReadManifest(fileSystem);
        Assert.True(
            manifest.IndexOf("[nuget.sources.core]", StringComparison.Ordinal)
            < manifest.IndexOf("[nuget.sources.tools]", StringComparison.Ordinal),
            "a newly added feed must be searched after the ones already declared");
    }

    [Fact]
    public async Task Add_IsIdempotentForTheSameNameAndUrl() {
        var (workflow, fileSystem, _) = Build(ManifestWithCore);

        var result = await workflow.AddAsync(ProjectDirectory, "core", CoreUrl);

        Assert.Equal(SourceChange.Unchanged, result.Change);
        Assert.Equal(1, CountOccurrences(ReadManifest(fileSystem), "[nuget.sources.core]"));
    }

    [Fact]
    public async Task Add_RefusesTheSameNameForADifferentUrl() {
        var (workflow, _, _) = Build(ManifestWithCore);

        var ex = await Assert.ThrowsAsync<WorkflowException>(
            () => workflow.AddAsync(ProjectDirectory, "core", ToolsUrl));

        Assert.Contains(CoreUrl, ex.Message);
        Assert.Contains("source remove core", ex.Message);
    }

    [Fact]
    public async Task Add_RefusesTheSameUrlUnderADifferentName() {
        var (workflow, _, _) = Build(ManifestWithCore);

        var ex = await Assert.ThrowsAsync<WorkflowException>(
            () => workflow.AddAsync(ProjectDirectory, "core_again", CoreUrl));

        Assert.Contains("already declared as [nuget.sources.core]", ex.Message);
    }

    [Theory]
    [InlineData("has space")]
    [InlineData("-leading-dash")]
    [InlineData("")]
    public async Task Add_RejectsAnUnusableName(string name) {
        var (workflow, fileSystem, _) = Build(EmptyManifest);

        await Assert.ThrowsAsync<WorkflowException>(() =>
            workflow.AddAsync(ProjectDirectory, name, CoreUrl));
        Assert.DoesNotContain("nuget.sources", ReadManifest(fileSystem));
    }

    [Theory]
    [InlineData("not-a-url")]
    [InlineData("file:///tmp/feed/index.json")]
    [InlineData("")]
    public async Task Add_RejectsAnythingThatIsNotAnHttpUrl(string url) {
        var (workflow, fileSystem, _) = Build(EmptyManifest);

        await Assert.ThrowsAsync<WorkflowException>(() =>
            workflow.AddAsync(ProjectDirectory, "core", url));
        Assert.DoesNotContain("nuget.sources", ReadManifest(fileSystem));
    }

    /// <summary>A failed verification must leave nothing behind: a `source add` that could not
    /// reach the feed should never be something the user has to undo.</summary>
    [Fact]
    public async Task Add_VerificationFailureAbortsBeforeWriting() {
        var (workflow, fileSystem, client) = Build(EmptyManifest);
        client.VerifyFailure = new NuGetSourceException("core", CoreUrl,
            "404 while fetching the service index.");

        await Assert.ThrowsAsync<NuGetSourceException>(() =>
            workflow.AddAsync(ProjectDirectory, "core", CoreUrl));

        Assert.DoesNotContain("nuget.sources", ReadManifest(fileSystem));
    }

    [Fact]
    public async Task Add_WithoutVerifyMakesNoNetworkCall() {
        var (workflow, fileSystem, client) = Build(EmptyManifest);
        client.VerifyFailure = new NuGetSourceException("core", CoreUrl,
            "the feed is offline right now.");

        var result = await workflow.AddAsync(ProjectDirectory, "core", CoreUrl, verify: false);

        Assert.Equal(SourceChange.Added, result.Change);
        Assert.Equal(0, client.VerifyCount);
        Assert.Contains("[nuget.sources.core]", ReadManifest(fileSystem));
    }

    [Fact]
    public async Task Add_VerifiesOnlyTheFeedBeingDeclared() {
        var (workflow, _, client) = Build(ManifestWithCore);

        await workflow.AddAsync(ProjectDirectory, "tools", ToolsUrl);

        // One declared feed plus the implicitly appended nuget.org would be two if the whole
        // project set had been handed to the factory. Verifying a set that includes an
        // already-working feed can pass on the strength of the wrong one.
        Assert.Equal(1, client.VerifyCount);
    }

    [Fact]
    public void List_ReturnsDeclaredFeedsInDeclarationOrder() {
        var (workflow, _, _) = Build($"""
            [nuget.sources.tools]
            url = "{ToolsUrl}"

            [nuget.sources.core]
            url = "{CoreUrl}"
            """);

        Assert.Equal(["tools", "core"], workflow.List(ProjectDirectory)
            .Select(source => source.Name));
    }

    [Fact]
    public void Remove_DropsTheFeed() {
        var (workflow, fileSystem, _) = Build(ManifestWithCore);

        var result = workflow.Remove(ProjectDirectory, "core");

        Assert.Equal(SourceChange.Removed, result.Change);
        Assert.DoesNotContain("nuget.sources", ReadManifest(fileSystem));
    }

    [Fact]
    public void Remove_OfAnUndeclaredFeedChangesNothing() {
        var (workflow, fileSystem, _) = Build(ManifestWithCore);

        var result = workflow.Remove(ProjectDirectory, "tools");

        Assert.Equal(SourceChange.Unchanged, result.Change);
        Assert.Contains("[nuget.sources.core]", ReadManifest(fileSystem));
    }

    [Fact]
    public void Remove_RefusesWhileAManifestPinNamesIt() {
        var (workflow, fileSystem, _) = Build($"""
            {ManifestWithCore}

            [dotnet.Kani.Core]
            version = "1.2.*"
            source = "core"
            """);

        var ex = Assert.Throws<WorkflowException>(() => workflow.Remove(ProjectDirectory, "core"));

        Assert.Contains("Kani.Core", ex.Message);
        Assert.Contains("--force", ex.Message);
        Assert.Contains("[nuget.sources.core]", ReadManifest(fileSystem));
    }

    /// <summary>A transitive package appears in no manifest, so the lockfile is the only local
    /// record that it resolved from this feed — and leaving a lockfile entry pointing at a feed the
    /// project no longer declares aims packageSourceMapping at nothing.</summary>
    [Fact]
    public void Remove_RefusesWhileOnlyTheLockfileNamesIt() {
        var (workflow, _, _) = Build(ManifestWithCore, LockfileWithPinnedPackage);

        var ex = Assert.Throws<WorkflowException>(() => workflow.Remove(ProjectDirectory, "core"));

        Assert.Contains("VBeami.Core", ex.Message);
    }

    [Fact]
    public void Remove_WithForceClearsThePinsAndReportsEachOne() {
        var (workflow, fileSystem, _) = Build(
            $"""
            {ManifestWithCore}

            [dotnet.Kani.Core]
            version = "1.2.*"
            source = "core"
            """,
            LockfileWithPinnedPackage);

        var result = workflow.Remove(ProjectDirectory, "core", force: true);

        Assert.Equal(SourceChange.Removed, result.Change);

        var manifest = ReadManifest(fileSystem);
        Assert.DoesNotContain("nuget.sources", manifest);

        // The package itself survives; only its feed pin goes. Removing the declaration is not a
        // request to remove the dependency.
        Assert.Contains("[dotnet.Kani.Core]", manifest);
        Assert.DoesNotContain("source = \"core\"", manifest);
        Assert.DoesNotContain("source = \"core\"", ReadLockfile(fileSystem));

        Assert.Contains(result.DroppedPins, pin =>
            pin.Contains("Kani.Core", StringComparison.Ordinal));
        Assert.Contains(result.DroppedPins, pin =>
            pin.Contains("VBeami.Core", StringComparison.Ordinal));
    }

    private const string LockfileWithPinnedPackage = """
        version = 1

        [[dotnet]]
        name = "VBeami.Core"
        resolved = "0.7.17"
        source = "core"
        """;

    private static int CountOccurrences(string haystack, string needle) {
        var count = 0;
        var index = haystack.IndexOf(needle, StringComparison.Ordinal);

        while (index >= 0) {
            count++;
            index = haystack.IndexOf(needle, index + needle.Length, StringComparison.Ordinal);
        }

        return count;
    }
}
