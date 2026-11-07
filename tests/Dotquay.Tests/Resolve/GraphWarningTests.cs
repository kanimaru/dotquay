using Dotquay.Core.Abstractions;
using Dotquay.Core.Manifest;
using Dotquay.Core.Resolve;
using Dotquay.Core.Sync;
using Dotquay.Tests.Support;
using Xunit;

namespace Dotquay.Tests.Resolve;

/// <summary>
/// Covers the graph walk's non-fatal warnings — the case where an addon looks dotquay-aware but its
/// dependencies get skipped anyway.
/// </summary>
/// <remarks>
/// Written after a real incident. An addon whose repo laid out as
/// <c>godot/addons/&lt;name&gt;/</c> with its <c>dotquay.toml</c> one level up in <c>godot/</c>
/// resolved and installed perfectly while every dependency it declared was silently dropped — and
/// nothing, anywhere, said a word. The resolution behaviour was correct (only the extracted subtree
/// exists from a consumer's point of view); the silence was not. These tests pin the noise.
/// </remarks>
public class GraphWarningTests {
    private const string LeafUrl = "https://example.com/leaf.git";
    private const string ChildUrl = "https://example.com/child.git";

    private static (DependencyGraphResolver Resolver, FakeGitClient GitClient) Build() {
        var fileSystem = new InMemoryFileSystem();
        var gitClient = new FakeGitClient { FileSystemForFetchedContent = fileSystem };
        var cache = new RepoCache(fileSystem, gitClient, "cache");
        return (new DependencyGraphResolver(fileSystem, gitClient, cache, "discovery"), gitClient);
    }

    private static void SetTags(FakeGitClient gitClient, string repoUrl, params string[] tags) =>
        gitClient.RemoteRefsByUrl[repoUrl] = tags
            .Select((tag, i) => new GitRef(tag, new string('a', 39) + i, GitRefKind.Tag))
            .ToList();

    private static Dictionary<string, AddonSpec> Roots(params AddonSpec[] specs) =>
        specs.ToDictionary(s => s.Name, StringComparer.Ordinal);

    private static Dictionary<string, AddonSpec> NoOverrides() => new();

    /// <summary>The real-world layout: addon subtree at <c>godot/addons/leaf</c>, manifest one
    /// level up at <c>godot/dotquay.toml</c> where a consumer will never see it.</summary>
    private static AddonSpec SetUpMisplacedManifest(FakeGitClient gitClient) {
        SetTags(gitClient, LeafUrl, "v1.0.0");
        gitClient.FetchedFilesByUrl[LeafUrl] = new Dictionary<string, string> {
            ["godot/addons/leaf/plugin.cfg"] = "cfg",
            ["godot/dotquay.toml"] = $"""
                [project]
                godot = "4.7"

                [addons.child]
                git = "{ChildUrl}"
                version = "~1.0"
                """,
        };

        return new AddonSpec {
            Name = "leaf",
            Git = LeafUrl,
            Version = "~1.0",
            Path = "godot/addons/leaf"
        };
    }

    private const string AlphaUrl = "https://example.com/alpha.git";
    private const string BetaUrl = "https://example.com/beta.git";

    /// <summary>Two addons that both declare the same analyzer package, with the asset metadata
    /// each one's author chose.</summary>
    private static AddonSpec[] SetUpTwoAddonsDeclaring(
        FakeGitClient gitClient, string alphaPrivateAssets, string betaPrivateAssets) {
        foreach (var (name, url, privateAssets) in
            new[] { ("alpha", AlphaUrl, alphaPrivateAssets),
                ("beta", BetaUrl, betaPrivateAssets) }) {
            SetTags(gitClient, url, "v1.0.0");
            gitClient.FetchedFilesByUrl[url] = new Dictionary<string, string> {
                [$"addons/{name}/plugin.cfg"] = "cfg",
                [$"addons/{name}/dotquay.toml"] = $"""
                    [package]
                    name = "{name}"

                    [dotnet."Shared.Analyzer"]
                    version = "1.0.0"
                    private_assets = [{privateAssets}]
                    """,
            };
        }

        return
        [
            new AddonSpec { Name = "alpha", Git = AlphaUrl, Version = "~1.0",
                Path = "addons/alpha" },
            new AddonSpec { Name = "beta", Git = BetaUrl, Version = "~1.0", Path = "addons/beta" },
        ];
    }

    [Fact]
    public async Task AnalyzeGraphAsync_AddonsDisagreeingOnAssetMetadata_Warns() {
        var (resolver, gitClient) = Build();
        var specs = SetUpTwoAddonsDeclaring(gitClient, "\"all\"", "\"none\"");

        var analysis = await resolver.AnalyzeGraphAsync(Roots(specs), NoOverrides());

        // First-discovered wins, as it already does for the version — but unlike a version range,
        // one of these two addons is now getting wiring its author did not ask for, so it is said
        // out loud. The message names both sides because "something else disagrees" is
        // unactionable.
        var warning = Assert.Single(analysis.Warnings, w =>
            w.Kind == GraphWarningKind.DotnetAssetMetadataConflict);
        Assert.Contains("Shared.Analyzer", warning.Message);
        Assert.Contains("alpha", warning.Message);
        Assert.Contains("beta", warning.Message);
    }

    [Fact]
    public async Task AnalyzeGraphAsync_AddonsAgreeingOnAssetMetadata_IsSilent() {
        var (resolver, gitClient) = Build();
        var specs = SetUpTwoAddonsDeclaring(gitClient, "\"none\"", "\"none\"");

        var analysis = await resolver.AnalyzeGraphAsync(Roots(specs), NoOverrides());

        // Two addons wanting the same wiring is the normal case, and warning about it would be
        // noise arriving exactly when things go right.
        Assert.DoesNotContain(analysis.Warnings, w =>
            w.Kind == GraphWarningKind.DotnetAssetMetadataConflict);
    }

    [Fact]
    public async Task AnalyzeGraphAsync_ManifestOutsideTheSubtree_Warns() {
        var (resolver, gitClient) = Build();
        var spec = SetUpMisplacedManifest(gitClient);

        var analysis = await resolver.AnalyzeGraphAsync(Roots(spec), NoOverrides());

        var warning = Assert.Single(analysis.Warnings);
        Assert.Equal(GraphWarningKind.PackageManifestOutsideSubtree, warning.Kind);
        Assert.Equal("leaf", warning.AddonName);
    }

    [Fact]
    public async Task AnalyzeGraphAsync_ManifestOutsideTheSubtree_NamesBothPlaces() {
        var (resolver, gitClient) = Build();
        var spec = SetUpMisplacedManifest(gitClient);

        var analysis = await resolver.AnalyzeGraphAsync(Roots(spec), NoOverrides());

        // The whole point is to end the search: say where the file is and where it has to go.
        var message = analysis.Warnings[0].Message;
        Assert.Contains("godot/dotquay.toml", message);
        Assert.Contains("godot/addons/leaf", message);
    }

    [Fact]
    public async Task AnalyzeGraphAsync_ManifestOutsideTheSubtree_StillResolvesTheAddonAsALeaf() {
        var (resolver, gitClient) = Build();
        var spec = SetUpMisplacedManifest(gitClient);

        var analysis = await resolver.AnalyzeGraphAsync(Roots(spec), NoOverrides());

        // A warning must never change resolution. The addon installs exactly as it did before; only
        // the silence is gone.
        var entry = Assert.Single(analysis.Addons);
        Assert.Equal("leaf", entry.Resolved.Name);
        Assert.Empty(entry.DependencyNames);
        Assert.Empty(analysis.Conflicts);
    }

    [Fact]
    public async Task AnalyzeGraphAsync_TrailingSlashOnPath_DoesNotDefeatTheSubtreeCheck() {
        var (resolver, gitClient) = Build();
        SetTags(gitClient, LeafUrl, "v1.0.0");
        gitClient.FetchedFilesByUrl[LeafUrl] = new Dictionary<string, string> {
            ["addons/leaf/plugin.cfg"] = "cfg",
            ["addons/leaf/dotquay.toml"] = """
                [package]
                name = "leaf"
                version = "1.0.0"
                """,
        };

        // Real manifests are written by hand and routinely carry a trailing slash — comparing raw
        // strings would make this look like a manifest outside the subtree and warn about a repo
        // that is laid out perfectly.
        var spec = new AddonSpec {
            Name = "leaf",
            Git = LeafUrl,
            Version = "~1.0",
            Path = "addons/leaf/"
        };

        var analysis = await resolver.AnalyzeGraphAsync(Roots(spec), NoOverrides());

        Assert.Empty(analysis.Warnings);
    }

    [Fact]
    public async Task AnalyzeGraphAsync_ManifestInsideTheSubtreeWithNoPackageHeader_Warns() {
        var (resolver, gitClient) = Build();
        SetTags(gitClient, LeafUrl, "v1.0.0");
        gitClient.FetchedFilesByUrl[LeafUrl] = new Dictionary<string, string> {
            ["addons/leaf/plugin.cfg"] = "cfg",
            // Moved into the right folder, but still a [project] manifest — the exact halfway state
            // someone lands in when they fix the location and not the header.
            ["addons/leaf/dotquay.toml"] = $"""
                [project]
                godot = "4.7"

                [addons.child]
                git = "{ChildUrl}"
                version = "~1.0"
                """,
        };

        var spec = new AddonSpec {
            Name = "leaf",
            Git = LeafUrl,
            Version = "~1.0",
            Path = "addons/leaf"
        };

        var analysis = await resolver.AnalyzeGraphAsync(Roots(spec), NoOverrides());

        var warning = Assert.Single(analysis.Warnings);
        Assert.Equal(GraphWarningKind.PackageManifestNotReadable, warning.Kind);
        Assert.Contains("[package]", warning.Message);
    }

    [Fact]
    public async Task AnalyzeGraphAsync_ValidPackageManifest_WarnsAboutNothing() {
        var (resolver, gitClient) = Build();
        SetTags(gitClient, LeafUrl, "v1.0.0");
        SetTags(gitClient, ChildUrl, "v1.0.0");
        gitClient.FetchedFilesByUrl[LeafUrl] = new Dictionary<string, string> {
            ["addons/leaf/plugin.cfg"] = "cfg",
            ["addons/leaf/dotquay.toml"] = $"""
                [package]
                name = "leaf"
                version = "1.0.0"

                [addons.child]
                git = "{ChildUrl}"
                version = "~1.0"
                """,
        };
        gitClient.FetchedFilesByUrl[ChildUrl] = new Dictionary<string,
            string> { ["addons/child/plugin.cfg"] = "cfg" };

        var spec = new AddonSpec {
            Name = "leaf",
            Git = LeafUrl,
            Version = "~1.0",
            Path = "addons/leaf"
        };

        var analysis = await resolver.AnalyzeGraphAsync(Roots(spec), NoOverrides());

        Assert.Empty(analysis.Warnings);
        Assert.Equal(2, analysis.Addons.Count);
        Assert.Contains(analysis.Addons, a => a.Resolved.Name == "child");
    }

    [Fact]
    public async Task AnalyzeGraphAsync_ThirdPartyAddonWithNoManifestAnywhere_WarnsAboutNothing() {
        var (resolver, gitClient) = Build();
        SetTags(gitClient, LeafUrl, "v1.0.0");
        gitClient.FetchedFilesByUrl[LeafUrl] = new Dictionary<string, string> {
            ["addons/leaf/plugin.cfg"] = "cfg",
        };

        var spec = new AddonSpec {
            Name = "leaf",
            Git = LeafUrl,
            Version = "~1.0",
            Path = "addons/leaf"
        };

        var analysis = await resolver.AnalyzeGraphAsync(Roots(spec), NoOverrides());

        // SPEC.md §7.2: "third-party addons without this file are leaf nodes. The graph feature
        // costs them nothing." Warning about every unmanaged addon in the ecosystem would be pure
        // noise and would train people to ignore the warning that matters.
        Assert.Empty(analysis.Warnings);
    }

    [Fact]
    public async Task ResolveGraphAsync_CarriesWarningsToTheInstallPath() {
        var (resolver, gitClient) = Build();
        var spec = SetUpMisplacedManifest(gitClient);

        var result = await resolver.ResolveGraphAsync(Roots(spec), NoOverrides());

        // `update` is where someone notices a transitive dependency failing to appear, so the
        // warning has to reach that path too — not only `dotquay graph`.
        var warning = Assert.Single(result.Warnings);
        Assert.Equal(GraphWarningKind.PackageManifestOutsideSubtree, warning.Kind);
    }

    // -----------------------------------------------------------------------------------------
    // A subtree that is a Godot project, not an addon (v0.19)
    // -----------------------------------------------------------------------------------------

    private const string VbeamiUrl = "https://example.com/vbeami.git";
    private const string VrmUrl = "https://example.com/vrm-addon.git";
    private static string VrmSha => new('c', 40);

    /// <summary>The real incident: vrm-addon moved its plugin into <c>addons/vrm</c> and turned its
    /// root into a project, while vbeami's package manifest kept declaring it with the path from
    /// before the move.</summary>
    private static AddonSpec SetUpProjectRootDependency(FakeGitClient gitClient,
        string declaredPath = ".") {
        SetTags(gitClient, VbeamiUrl, "v0.7.22");
        gitClient.FetchedFilesByUrl[VbeamiUrl] = new Dictionary<string, string> {
            ["godot/addons/vbeami/plugin.cfg"] = "cfg",
            ["godot/addons/vbeami/dotquay.toml"] = $"""
                [package]
                name = "vbeami"

                [addons.vrm]
                git = "{VrmUrl}"
                branch = "main"
                path = "{declaredPath}"
                """,
        };

        gitClient.BranchTips[(VrmUrl, "main")] = VrmSha;
        gitClient.FetchedFilesByUrl[VrmUrl] = new Dictionary<string, string> {
            ["project.godot"] = "config_version=5",
            ["dotquay.toml"] = """
                [project]
                godot = "4.3"
                """,
            ["addons/vrm/plugin.cfg"] = "cfg",
            ["addons/gut/plugin.cfg"] = "cfg",
        };

        return new AddonSpec {
            Name = "vbeami",
            Git = VbeamiUrl,
            Version = "~0.7",
            Path = "godot/addons/vbeami"
        };
    }

    [Fact]
    public async Task AnalyzeGraphAsync_TransitiveSubtreeIsAGodotProject_Warns() {
        var (resolver, gitClient) = Build();
        var spec = SetUpProjectRootDependency(gitClient);

        var analysis = await resolver.AnalyzeGraphAsync(Roots(spec), NoOverrides());

        var warning = Assert.Single(analysis.Warnings);
        Assert.Equal(GraphWarningKind.SubtreeIsGodotProject, warning.Kind);
        Assert.Equal("vrm", warning.AddonName);
    }

    [Fact]
    public async Task AnalyzeGraphAsync_TransitiveSubtreeIsAGodotProject_NamesTheDeclaringManifestAndTheRealPath() {
        var (resolver, gitClient) = Build();
        var spec = SetUpProjectRootDependency(gitClient);

        var analysis = await resolver.AnalyzeGraphAsync(Roots(spec), NoOverrides());

        // The consumer's own manifest never mentions vrm. Unless the warning says whose path is
        // wrong and what it should be, the reader is left grepping for a "." they never wrote.
        var message = analysis.Warnings[0].Message;
        Assert.Contains("'vbeami''s package manifest", message);
        Assert.Contains("path = \".\"", message);
        Assert.Contains("'addons/vrm'", message);
        Assert.Contains("project.godot", message);
    }

    [Fact]
    public async Task AnalyzeGraphAsync_TransitiveSubtreeIsAGodotProject_OffersAnOverrideOnTheDeclaredBranch() {
        var (resolver, gitClient) = Build();
        var spec = SetUpProjectRootDependency(gitClient);

        var analysis = await resolver.AnalyzeGraphAsync(Roots(spec), NoOverrides());

        // The resolver knows vbeami declared vrm by branch, so the hint tracks that branch rather
        // than freezing a commit the way doctor's offline hint has to.
        Assert.Contains("dotquay override vrm --branch main --path addons/vrm",
            analysis.Warnings[0].Message);
    }

    [Fact]
    public async Task AnalyzeGraphAsync_SubtreeIsAGodotProject_DoesNotAlsoWarnAboutItsProjectManifest() {
        var (resolver, gitClient) = Build();
        var spec = SetUpProjectRootDependency(gitClient);

        var analysis = await resolver.AnalyzeGraphAsync(Roots(spec), NoOverrides());

        // The project root's own dotquay.toml is a [project] manifest. Before this warning existed,
        // "missing [package] header" was the only thing said about the situation — and following
        // that advice would have been exactly wrong.
        Assert.DoesNotContain(analysis.Warnings, w =>
            w.Kind == GraphWarningKind.PackageManifestNotReadable);
    }

    [Fact]
    public async Task AnalyzeGraphAsync_SubtreeIsAGodotProject_StillResolvesExactlyAsBefore() {
        var (resolver, gitClient) = Build();
        var spec = SetUpProjectRootDependency(gitClient);

        var analysis = await resolver.AnalyzeGraphAsync(Roots(spec), NoOverrides());

        // A warning never changes resolution (SPEC.md §8). Silently swapping the path would be
        // dotquay inferring a layout it wasn't told, which §7.1 rules out; the message says what to
        // change instead.
        var vrm = Assert.Single(analysis.Addons, a => a.Resolved.Name == "vrm");
        Assert.Equal(".", vrm.Resolved.Path);
        Assert.Equal(VrmSha, vrm.Resolved.ResolvedSha);
        Assert.Empty(analysis.Conflicts);
    }

    [Fact]
    public async Task AnalyzeGraphAsync_RootAddonPointingAtAGodotProject_NamesTheProjectsOwnDeclaration() {
        var (resolver, gitClient) = Build();
        SetUpProjectRootDependency(gitClient);
        var spec = new AddonSpec { Name = "vrm", Git = VrmUrl, Branch = "main", Path = "." };

        var analysis = await resolver.AnalyzeGraphAsync(Roots(spec), NoOverrides());

        var warning = Assert.Single(analysis.Warnings, w =>
            w.Kind == GraphWarningKind.SubtreeIsGodotProject);
        Assert.Contains("this project's dotquay.toml ([addons.vrm])", warning.Message);
    }

    [Fact]
    public async Task AnalyzeGraphAsync_DeclaredPathFixed_WarnsAboutNothing() {
        var (resolver, gitClient) = Build();
        var spec = SetUpProjectRootDependency(gitClient, declaredPath: "addons/vrm");

        var analysis = await resolver.AnalyzeGraphAsync(Roots(spec), NoOverrides());

        // The fix the warning asks for, applied: the subtree is now the plugin folder, which has a
        // plugin.cfg and no project.godot, so this check has nothing left to say. (§7.2's
        // misplaced-manifest heuristic still notices the project's own dotquay.toml at the root —
        // that is the addon author's to settle by shipping a [package] manifest, not this check's.)
        Assert.DoesNotContain(analysis.Warnings, w =>
            w.Kind == GraphWarningKind.SubtreeIsGodotProject);
        Assert.DoesNotContain(analysis.Warnings, w =>
            w.Kind == GraphWarningKind.PackageManifestNotReadable);
        Assert.Contains(analysis.Addons, a => a.Resolved.Name == "vrm"
            && a.Resolved.Path == "addons/vrm");
    }

    [Fact]
    public async Task ResolveGraphAsync_SubtreeIsAGodotProject_CarriesTheWarningToTheInstallPath() {
        var (resolver, gitClient) = Build();
        var spec = SetUpProjectRootDependency(gitClient);

        var result = await resolver.ResolveGraphAsync(Roots(spec), NoOverrides());

        // `update` is where somebody watches addons/vrm fill up with a whole repository; the
        // warning has to be printed there, not only by `dotquay graph`.
        var warning = Assert.Single(result.Warnings);
        Assert.Equal(GraphWarningKind.SubtreeIsGodotProject, warning.Kind);
    }
}
