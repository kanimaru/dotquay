using Dotquay.Core.Abstractions;
using Dotquay.Core.Manifest;
using Dotquay.Core.Resolve;
using Dotquay.Core.Sync;
using Dotquay.Tests.Support;
using Xunit;

namespace Dotquay.Tests.Resolve;

/// <summary>
/// Covers transport inheritance across the graph walk (v0.15): a transitively-discovered addon is
/// fetched over the transport the consuming project uses for that host, not the one its author
/// happened to write.
/// </summary>
/// <remarks>
/// From a real setup failure. A project whose every declared URL was HTTPS pulled in a dependency
/// declared <c>git@gitlab.kemomi.de:...</c> by an intermediate addon, so a developer with no SSH key
/// -- and no host key in <c>known_hosts</c> -- could not install it. Nothing they could edit in their
/// own manifest would have helped: the SSH URL lived in somebody else's repository.
/// <para>
/// The fake git client is keyed by exact URL string, which makes these tests sharp: if the rewrite
/// does not happen, the resolver looks up a URL nothing is configured for and resolution fails rather
/// than quietly passing.
/// </para>
/// </remarks>
public class GitTransportInheritanceTests
{
    private const string VbeamiHttps = "https://gitlab.kemomi.de/wild-alliance/vbeami";
    private const string VbeamiSsh = "git@gitlab.kemomi.de:wild-alliance/vbeami.git";
    private const string MtoonHttps = "https://gitlab.kemomi.de/wild-alliance/mtoon-shader";
    private const string MtoonSsh = "git@gitlab.kemomi.de:wild-alliance/mtoon-shader.git";
    private const string GutSsh = "git@github.com:bitwes/gut.git";
    private const string VbeamiGithubSsh = "git@github.com:wild-alliance/vbeami.git";

    private static (DependencyGraphResolver Resolver, FakeGitClient GitClient) Build()
    {
        var fileSystem = new InMemoryFileSystem();
        var gitClient = new FakeGitClient { FileSystemForFetchedContent = fileSystem };
        var cache = new RepoCache(fileSystem, gitClient, "cache");
        return (new DependencyGraphResolver(fileSystem, gitClient, cache, "discovery"), gitClient);
    }

    private static void SetTags(FakeGitClient gitClient, string repoUrl, params string[] tags) =>
        gitClient.RemoteRefsByUrl[repoUrl] = tags
            .Select((tag, i) => new GitRef(tag, new string('a', 39) + i, GitRefKind.Tag))
            .ToList();

    /// <summary>A leaf addon: resolvable, with a subtree to extract and no manifest of its own.</summary>
    private static void SetUpLeaf(FakeGitClient gitClient, string url, string addonName)
    {
        SetTags(gitClient, url, "v1.0.0");
        gitClient.FetchedFilesByUrl[url] = new Dictionary<string, string>
        {
            [$"addons/{addonName}/plugin.cfg"] = "cfg",
        };
    }

    /// <summary>An addon that declares one dependency, at whatever URL the test wants to inject.</summary>
    private static void SetUpParentDeclaring(
        FakeGitClient gitClient, string parentUrl, string parentName, string childName, string childUrl)
    {
        SetTags(gitClient, parentUrl, "v1.0.0");
        gitClient.FetchedFilesByUrl[parentUrl] = new Dictionary<string, string>
        {
            [$"addons/{parentName}/plugin.cfg"] = "cfg",
            [$"addons/{parentName}/dotquay.toml"] = $"""
                [package]
                name = "{parentName}"
                version = "1.0.0"

                [addons.{childName}]
                git = "{childUrl}"
                version = "~1.0"
                path = "addons/{childName}"
                """,
        };
    }

    private static Dictionary<string, AddonSpec> Root(string name, string url) => new()
    {
        [name] = new AddonSpec { Name = name, Git = url, Version = "~1.0", Path = $"addons/{name}" },
    };

    // -----------------------------------------------------------------------------------------
    // The reported failure, and its mirror
    // -----------------------------------------------------------------------------------------

    [Fact]
    public async Task HttpsProject_FetchesAnSshDeclaredTransitiveOverHttps()
    {
        var (resolver, gitClient) = Build();
        SetUpParentDeclaring(gitClient, VbeamiHttps, "vbeami", "mtoon-shader", MtoonSsh);
        SetUpLeaf(gitClient, MtoonHttps, "mtoon-shader");

        var result = await resolver.ResolveGraphAsync(Root("vbeami", VbeamiHttps), new Dictionary<string, AddonSpec>());

        var mtoon = Assert.Single(result.Addons, entry => entry.Resolved.Name == "mtoon-shader");
        Assert.Equal(MtoonHttps, mtoon.Resolved.Git);
        Assert.DoesNotContain(gitClient.FetchCalls, call => call.Url == MtoonSsh);
    }

    [Fact]
    public async Task SshProject_FetchesAnHttpsDeclaredTransitiveOverSsh()
    {
        // The mirror matters as much as the original: an SSH-only shop inheriting HTTPS URLs gets
        // credential prompts on every fetch where their agent would have answered silently.
        var (resolver, gitClient) = Build();
        SetUpParentDeclaring(gitClient, VbeamiSsh, "vbeami", "mtoon-shader", MtoonHttps);
        SetUpLeaf(gitClient, MtoonSsh, "mtoon-shader");

        var result = await resolver.ResolveGraphAsync(Root("vbeami", VbeamiSsh), new Dictionary<string, AddonSpec>());

        var mtoon = Assert.Single(result.Addons, entry => entry.Resolved.Name == "mtoon-shader");
        Assert.Equal(MtoonSsh, mtoon.Resolved.Git);
    }

    [Fact]
    public async Task ARewriteIsWarnedAbout_NotDoneQuietly()
    {
        // The lockfile ends up naming a URL that appears in no manifest anywhere. Finding that out by
        // diffing the lockfile against somebody else's dotquay.toml is not a reasonable ask.
        var (resolver, gitClient) = Build();
        SetUpParentDeclaring(gitClient, VbeamiHttps, "vbeami", "mtoon-shader", MtoonSsh);
        SetUpLeaf(gitClient, MtoonHttps, "mtoon-shader");

        var result = await resolver.ResolveGraphAsync(Root("vbeami", VbeamiHttps), new Dictionary<string, AddonSpec>());

        var warning = Assert.Single(result.Warnings, w => w.Kind == GraphWarningKind.GitTransportRewritten);
        Assert.Equal("mtoon-shader", warning.AddonName);
        Assert.Contains("vbeami", warning.Message, StringComparison.Ordinal);
        Assert.Contains(MtoonSsh, warning.Message, StringComparison.Ordinal);
        Assert.Contains(MtoonHttps, warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoRewrite_ProducesNoWarning()
    {
        var (resolver, gitClient) = Build();
        SetUpParentDeclaring(gitClient, VbeamiHttps, "vbeami", "mtoon-shader", MtoonHttps);
        SetUpLeaf(gitClient, MtoonHttps, "mtoon-shader");

        var result = await resolver.ResolveGraphAsync(Root("vbeami", VbeamiHttps), new Dictionary<string, AddonSpec>());

        Assert.DoesNotContain(result.Warnings, w => w.Kind == GraphWarningKind.GitTransportRewritten);
    }

    // -----------------------------------------------------------------------------------------
    // What inference must not do
    // -----------------------------------------------------------------------------------------

    [Fact]
    public async Task AHostTheProjectNeverDescribed_IsLeftAlone()
    {
        // The project speaks HTTPS to gitlab.kemomi.de. That is not evidence about github.com, where
        // an SSH URL may be the only one that works for this user.
        var (resolver, gitClient) = Build();
        SetUpParentDeclaring(gitClient, VbeamiHttps, "vbeami", "gut", GutSsh);
        SetUpLeaf(gitClient, GutSsh, "gut");

        var result = await resolver.ResolveGraphAsync(Root("vbeami", VbeamiHttps), new Dictionary<string, AddonSpec>());

        var gut = Assert.Single(result.Addons, entry => entry.Resolved.Name == "gut");
        Assert.Equal(GutSsh, gut.Resolved.Git);
        Assert.DoesNotContain(result.Warnings, w => w.Kind == GraphWarningKind.GitTransportRewritten);
    }

    [Fact]
    public async Task AnOverride_CountsAsTheProjectSpeaking()
    {
        // An override is the project's own statement about a dependency, in the project's own voice,
        // so it is evidence about a host exactly like an [addons.*] entry is.
        //
        // Constructed so the override is the *only* evidence that can decide: the single root addon
        // lives on github, leaving gitlab.kemomi.de undescribed by [addons.*]. The override names a
        // name nothing in the graph requires -- a stale pin, which is realistic and, more to the
        // point, inert for resolution. If overrides were skipped, the SSH-declared child would stay
        // SSH and the lookup would fail.
        var (resolver, gitClient) = Build();
        SetUpParentDeclaring(gitClient, VbeamiGithubSsh, "vbeami", "mtoon-shader", MtoonSsh);
        SetUpLeaf(gitClient, MtoonHttps, "mtoon-shader");

        var overrides = new Dictionary<string, AddonSpec>
        {
            ["core"] = new AddonSpec
            {
                Name = "core",
                Git = "https://gitlab.kemomi.de/wild-alliance/core",
                Version = "~1.0",
                Path = "addons/core",
            },
        };

        var result = await resolver.ResolveGraphAsync(Root("vbeami", VbeamiGithubSsh), overrides);

        var mtoon = Assert.Single(result.Addons, entry => entry.Resolved.Name == "mtoon-shader");
        Assert.Equal(MtoonHttps, mtoon.Resolved.Git);
    }

    [Fact]
    public async Task TheProjectsOwnRootUrls_AreNeverRewritten()
    {
        // The roots are the evidence. Rewriting them would be circular, and would mean dotquay
        // fetching from a URL the user did not write while their manifest says otherwise.
        var (resolver, gitClient) = Build();
        SetUpLeaf(gitClient, VbeamiSsh, "vbeami");
        SetUpLeaf(gitClient, MtoonHttps, "mtoon-shader");

        var rootAddons = new Dictionary<string, AddonSpec>
        {
            ["vbeami"] = new AddonSpec { Name = "vbeami", Git = VbeamiSsh, Version = "~1.0", Path = "addons/vbeami" },
            ["mtoon-shader"] = new AddonSpec { Name = "mtoon-shader", Git = MtoonHttps, Version = "~1.0", Path = "addons/mtoon-shader" },
        };

        var result = await resolver.ResolveGraphAsync(rootAddons, new Dictionary<string, AddonSpec>());

        Assert.Equal(VbeamiSsh, Assert.Single(result.Addons, e => e.Resolved.Name == "vbeami").Resolved.Git);
        Assert.Equal(MtoonHttps, Assert.Single(result.Addons, e => e.Resolved.Name == "mtoon-shader").Resolved.Git);
    }
}
