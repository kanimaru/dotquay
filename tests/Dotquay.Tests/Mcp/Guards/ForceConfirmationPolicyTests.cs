using Dotquay.Core.Abstractions;
using Dotquay.Core.Workflows;
using Dotquay.Mcp.Guards;
using Dotquay.Mcp.Json;
using Dotquay.Tests.Support;
using Xunit;

namespace Dotquay.Tests.Mcp.Guards;

/// <summary>
/// Design doc §8 test layer 2 for <see cref="ForceConfirmationPolicy"/> (§5.3): "force refused
/// without confirm_discard, refused with a partial list, accepted with a full one, not required when
/// every verdict is SafeToForce." Built directly against a fake-backed <see cref="DependencyWorkflow"/>
/// — the same fixture shape <c>ExplainModifiedTests</c> uses for the Core feature this policy reads,
/// since the policy's whole job is to read <c>StatusAsync(explain: true)</c> and decide, not to
/// reimplement anything about what "modified" or "safe to force" mean.
/// </summary>
public sealed class ForceConfirmationPolicyTests
{
    private const string RepoUrl = "https://example.com/dialogue_manager.git";
    private const string ProjectDir = "project";
    private const string AddonDir = $"{ProjectDir}/addons/dialogue_manager";

    private const string SceneWithUid =
        """[ext_resource type="Script" uid="uid://bxwpwbdv4fm02" path="res://addons/dialogue_manager/x.gd"]""";

    private static async Task<(DependencyWorkflow Workflow, InMemoryFileSystem FileSystem)> InstalledAsync()
    {
        var fileSystem = new InMemoryFileSystem();
        fileSystem.WriteAllText($"{ProjectDir}/dotquay.toml", "[project]\ngodot = \"4.3\"\n");

        var gitClient = new FakeGitClient { FileSystemForFetchedContent = fileSystem };
        gitClient.FetchedFilesByUrl[RepoUrl] = new Dictionary<string, string>
        {
            ["addons/dialogue_manager/plugin.cfg"] = "cfg",
            ["addons/dialogue_manager/main.tscn"] = SceneWithUid,
        };
        gitClient.RemoteRefsByUrl[RepoUrl] = [new GitRef("v1.0.0", new string('a', 40), GitRefKind.Tag)];

        var workflow = new DependencyWorkflow(fileSystem, gitClient, "cache");
        await workflow.AddAsync(ProjectDir, RepoUrl);

        return (workflow, fileSystem);
    }

    private static WriteGuardContext ContextFor(
        DependencyWorkflow workflow, bool force = false, IReadOnlyList<string>? confirmDiscard = null) => new()
    {
        ProjectDirectory = ProjectDir,
        InspectionWorkflow = workflow,
        Force = force,
        ConfirmDiscard = confirmDiscard ?? [],
    };

    [Fact]
    public async Task Force_false_allows_regardless_of_addon_state()
    {
        var (workflow, fileSystem) = await InstalledAsync();
        fileSystem.WriteAllText($"{AddonDir}/my-notes.gd", "hours of work");

        var verdict = await new ForceConfirmationPolicy().EvaluateAsync(ContextFor(workflow, force: false), CancellationToken.None);

        Assert.IsType<WriteGuardAllow>(verdict);
    }

    [Fact]
    public async Task Force_true_with_nothing_modified_allows()
    {
        var (workflow, _) = await InstalledAsync();

        var verdict = await new ForceConfirmationPolicy().EvaluateAsync(ContextFor(workflow, force: true), CancellationToken.None);

        Assert.IsType<WriteGuardAllow>(verdict);
    }

    [Fact]
    public async Task Force_true_with_a_safe_to_force_change_needs_no_confirmation()
    {
        // A Godot-rewritten uid: SafeToForce per ExplainModifiedTests.StatusAsync_GodotRewroteAUid_SaysForcingIsSafe.
        var (workflow, fileSystem) = await InstalledAsync();
        fileSystem.WriteAllText($"{AddonDir}/main.tscn", SceneWithUid.Replace("bxwpwbdv4fm02", "c7k2p9qq1a0zz"));

        var verdict = await new ForceConfirmationPolicy().EvaluateAsync(ContextFor(workflow, force: true), CancellationToken.None);

        Assert.IsType<WriteGuardAllow>(verdict);
    }

    [Fact]
    public async Task Force_true_with_a_real_change_and_no_confirm_discard_is_refused()
    {
        var (workflow, fileSystem) = await InstalledAsync();
        fileSystem.WriteAllText($"{AddonDir}/my-notes.gd", "hours of work");

        var verdict = await new ForceConfirmationPolicy().EvaluateAsync(ContextFor(workflow, force: true), CancellationToken.None);

        var refusal = Assert.IsType<WriteGuardRefusal>(verdict);
        var detail = Assert.IsType<ForceConfirmationDetailJson>(refusal.Detail);
        Assert.Equal("force_confirmation", detail.Policy);
        var addon = Assert.Single(detail.UnconfirmedAddons);
        Assert.Equal("dialogue_manager", addon.Name);
        Assert.Equal("review_needed", addon.Explanation!.Verdict);
    }

    [Fact]
    public async Task Force_true_with_confirm_discard_naming_the_addon_allows()
    {
        var (workflow, fileSystem) = await InstalledAsync();
        fileSystem.WriteAllText($"{AddonDir}/my-notes.gd", "hours of work");

        var verdict = await new ForceConfirmationPolicy().EvaluateAsync(
            ContextFor(workflow, force: true, confirmDiscard: ["dialogue_manager"]), CancellationToken.None);

        Assert.IsType<WriteGuardAllow>(verdict);
    }

    [Fact]
    public async Task Force_true_with_confirm_discard_naming_an_unrelated_addon_is_still_refused()
    {
        var (workflow, fileSystem) = await InstalledAsync();
        fileSystem.WriteAllText($"{AddonDir}/my-notes.gd", "hours of work");

        var verdict = await new ForceConfirmationPolicy().EvaluateAsync(
            ContextFor(workflow, force: true, confirmDiscard: ["some_other_addon"]), CancellationToken.None);

        var refusal = Assert.IsType<WriteGuardRefusal>(verdict);
        var detail = Assert.IsType<ForceConfirmationDetailJson>(refusal.Detail);
        Assert.Equal("dialogue_manager", Assert.Single(detail.UnconfirmedAddons).Name);
    }
}
