using Dotquay.Core.Abstractions;
using Dotquay.Core.Git;
using Dotquay.Core.Workflows;
using Dotquay.Mcp.Guards;
using Dotquay.Mcp.Json;
using Dotquay.Tests.Support;
using Xunit;

namespace Dotquay.Tests.Mcp.Guards;

/// <summary>
/// Design doc §8 test layer 2 for <see cref="SourceApprovalPolicy"/> (§5.1): "feed adoption refused
/// without approvals and accepted with them." Same addon-declares-a-private-feed fixture shape as
/// <c>AddPreviewTests</c>, since this policy's pre-flight check for <c>dotquay_add</c> is a plain
/// <c>PreviewAddAsync</c> call — it reads the same facts the preview tool already reports.
/// </summary>
public sealed class SourceApprovalPolicyTests
{
    private const string ProjectDir = "project";
    private const string VbeamiUrl = "https://gitlab.example.com/wild-alliance/vbeami.git";
    private const string FeedUrl = "https://gitlab.example.com/api/v4/groups/7/-/packages/nuget/index.json";

    private const string VbeamiPackageManifest = $"""
        [package]
        name = "vbeami"
        version = "1.0.0"

        [nuget.sources.vbeami]
        url = "{FeedUrl}"
        packages = ["VBeami.*"]

        [dotnet.VBeami.Core]
        version = "1.0.0"
        """;

    private static DependencyWorkflow Build()
    {
        var fileSystem = new InMemoryFileSystem();
        fileSystem.WriteAllText($"{ProjectDir}/dotquay.toml", "[project]\ngodot = \"4.3\"\n");

        var gitClient = new FakeGitClient { FileSystemForFetchedContent = fileSystem };
        gitClient.RemoteRefsByUrl[VbeamiUrl] = [new GitRef("v1.0.0", new string('b', 40), GitRefKind.Tag)];
        gitClient.FetchedFilesByUrl[VbeamiUrl] = new Dictionary<string, string>
        {
            ["addons/vbeami/plugin.cfg"] = "vbeami",
            ["addons/vbeami/dotquay.toml"] = VbeamiPackageManifest,
        };

        var factory = new FakeNuGetClientFactory(new FakeNuGetClient());
        factory.ClientsBySource["vbeami"] = new FakeNuGetClient("vbeami").With("VBeami.Core", "1.2.0");

        return new DependencyWorkflow(fileSystem, gitClient, factory, "cache")
        {
            SourceApproval = DenyAllSourceApproval.Instance,
        };
    }

    private static WriteGuardContext ContextFor(DependencyWorkflow workflow, IReadOnlyList<string>? approvedSourceUrls = null) => new()
    {
        ProjectDirectory = ProjectDir,
        InspectionWorkflow = workflow,
        AddPreview = new AddSourcePreviewRequest(VbeamiUrl, new AddonAddOptions()),
        ApprovedSourceUrls = approvedSourceUrls ?? [],
    };

    [Fact]
    public async Task No_approvals_refuses_naming_the_declared_feed()
    {
        var workflow = Build();

        var verdict = await new SourceApprovalPolicy().EvaluateAsync(ContextFor(workflow), CancellationToken.None);

        var refusal = Assert.IsType<WriteGuardRefusal>(verdict);
        var detail = Assert.IsType<SourceApprovalDetailJson>(refusal.Detail);
        Assert.Equal("source_approval", detail.Policy);
        var source = Assert.Single(detail.UnapprovedSources);
        Assert.Equal(FeedUrl, source.Url);
        Assert.Equal("vbeami", source.DeclaredByAddon);
    }

    [Fact]
    public async Task Approving_the_declared_feed_url_allows()
    {
        var workflow = Build();

        var verdict = await new SourceApprovalPolicy().EvaluateAsync(ContextFor(workflow, [FeedUrl]), CancellationToken.None);

        Assert.IsType<WriteGuardAllow>(verdict);
    }

    [Fact]
    public async Task No_preview_request_allows_without_checking_anything()
    {
        // dotquay_update has no dry run (§5.1) — the policy is a no-op pre-check for it.
        var workflow = Build();

        var verdict = await new SourceApprovalPolicy().EvaluateAsync(
            new WriteGuardContext { ProjectDirectory = ProjectDir, InspectionWorkflow = workflow }, CancellationToken.None);

        Assert.IsType<WriteGuardAllow>(verdict);
    }

    [Fact]
    public void Resolve_with_no_urls_denies_everything()
    {
        Assert.IsType<DenyAllSourceApproval>(SourceApprovalPolicy.Resolve([]));
    }

    [Fact]
    public void Resolve_with_urls_approves_exactly_those()
    {
        var approval = Assert.IsType<PreApprovedSourceApproval>(SourceApprovalPolicy.Resolve([FeedUrl]));
        Assert.Contains(FeedUrl, approval.Urls);
    }
}
