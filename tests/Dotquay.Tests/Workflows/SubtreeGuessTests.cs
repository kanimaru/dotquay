using Dotquay.Core.Abstractions;
using Dotquay.Core.Dotnet;
using Dotquay.Core.Git;
using Dotquay.Core.Sync;
using Dotquay.Core.Workflows;
using Dotquay.Tests.Support;
using Xunit;

namespace Dotquay.Tests.Workflows;

/// <summary>
/// Covers <c>add</c>'s end of the subtree guess (docs/DESIGN-v0.20-SUBTREE-GUESS.md §5): probing the
/// repository before anything is written, asking, and recording the answer in the manifest.
/// </summary>
/// <remarks>
/// The case throughout is the reported one: <c>SproutyLabs/SproutyDialogs</c>, whose addon is
/// <c>addons/sprouty_dialogs</c>. Before this, <c>add</c> derived <c>addons/SproutyDialogs</c>,
/// carried it past resolution, and failed in the installer with a message naming only the path that
/// didn't work.
/// <para>
/// Two properties matter more than the happy path and are tested as their own facts: nothing is ever
/// adopted without an answer, and an accepted answer lands in <c>dotquay.toml</c> rather than being
/// re-derived on the next machine.
/// </para>
/// </remarks>
public class SubtreeGuessTests
{
    private const string ProjectDir = "project";
    private const string ManifestPath = $"{ProjectDir}/dotquay.toml";
    private const string Url = "https://github.com/SproutyLabs/SproutyDialogs";

    private const string EmptyProject = """
        [project]
        godot = "4.3"
        """;

    /// <summary>Says yes to everything, standing in for a user at a terminal.</summary>
    private sealed class AcceptSubtreeChoice : ISubtreeChoice
    {
        public SubtreeChoiceRequest? Asked { get; private set; }

        public bool Accept(SubtreeChoiceRequest request)
        {
            Asked = request;
            return true;
        }
    }

    private sealed class Fixture
    {
        public required DependencyWorkflow Workflow { get; init; }

        public required InMemoryFileSystem FileSystem { get; init; }

        public required AcceptSubtreeChoice Choice { get; init; }
    }

    private static Fixture Build(ISubtreeChoice? choice = null, params string[] repoFiles)
    {
        var fileSystem = new InMemoryFileSystem();
        fileSystem.WriteAllText(ManifestPath, EmptyProject);

        var gitClient = new FakeGitClient { FileSystemForFetchedContent = fileSystem };
        gitClient.RemoteRefsByUrl[Url] = [new GitRef("v1.0.0", new string('a', 40), GitRefKind.Tag)];
        gitClient.FetchedFilesByUrl[Url] = repoFiles.ToDictionary(path => path, _ => "cfg");

        var accepting = new AcceptSubtreeChoice();

        var addonInstaller = new AddonInstaller(
            fileSystem,
            new RepoCache(fileSystem, gitClient, "cache"),
            new SyncEngine(fileSystem),
            new FakePatchTool(fileSystem));

        var workflow = new DependencyWorkflow(
            fileSystem,
            gitClient,
            new FakeNuGetClientFactory(new FakeNuGetClient()),
            addonInstaller,
            new DotnetInstaller(fileSystem, new FakeDotnetCliRunner(), TestCredentials.None()))
        {
            SubtreeChoice = choice ?? accepting,
        };

        return new Fixture { Workflow = workflow, FileSystem = fileSystem, Choice = accepting };
    }

    /// <summary>The reported repository: PascalCase name, snake_case addon folder.</summary>
    private static Fixture SproutyDialogs(ISubtreeChoice? choice = null) =>
        Build(choice, "addons/sprouty_dialogs/plugin.cfg", "addons/sprouty_dialogs/dialogue.gd");

    // -- accepting ---------------------------------------------------------------------------------

    [Fact]
    public async Task Add_AcceptedGuess_InstallsFromTheRealSubtree()
    {
        var fixture = SproutyDialogs();

        var locked = await fixture.Workflow.AddAsync(ProjectDir, Url);

        Assert.Equal("addons/sprouty_dialogs", locked.Path);
        Assert.Equal("sprouty_dialogs", locked.EffectiveFolder);
        Assert.True(fixture.FileSystem.FileExists($"{ProjectDir}/addons/sprouty_dialogs/dialogue.gd"));
    }

    [Fact]
    public async Task Add_AcceptedGuess_WritesBothKeysToTheManifest()
    {
        // The whole point of asking once. Without install_as the addon's own
        // res://addons/sprouty_dialogs/ references would point at a folder Godot cannot find, which
        // is the Gut bug (SPEC.md §7.1.1); without path, the next machine re-derives the wrong guess.
        var fixture = SproutyDialogs();

        await fixture.Workflow.AddAsync(ProjectDir, Url);

        var manifest = fixture.FileSystem.ReadAllText(ManifestPath);
        Assert.Contains("addons/sprouty_dialogs", manifest);
        Assert.Contains("sprouty_dialogs", manifest);
        Assert.Contains("[addons.SproutyDialogs]", manifest);
    }

    [Fact]
    public async Task Add_AcceptedGuess_KeepsTheAddonsIdentity()
    {
        // Layout is corrected; identity is not. The manifest key, and therefore `remove`, `update`
        // and every dependency edge, stay on the name derived from the URL.
        var fixture = SproutyDialogs();

        var locked = await fixture.Workflow.AddAsync(ProjectDir, Url);

        Assert.Equal("SproutyDialogs", locked.Name);
    }

    [Fact]
    public async Task Add_Guess_IsAskedAboutWithItsEvidence()
    {
        var fixture = SproutyDialogs();

        await fixture.Workflow.AddAsync(ProjectDir, Url);

        var asked = fixture.Choice.Asked;
        Assert.NotNull(asked);
        Assert.Equal("SproutyDialogs", asked!.AddonName);
        Assert.Equal("addons/SproutyDialogs", asked.Finding.GuessedPath);
        Assert.Equal(SubtreeMatchKind.NameShape, asked.Suggestion.Kind);
    }

    // -- declining ---------------------------------------------------------------------------------

    [Fact]
    public async Task Add_DeclinedGuess_RefusesAndNamesTheFlags()
    {
        var fixture = SproutyDialogs(DeclineSubtreeChoice.Instance);

        var error = await Assert.ThrowsAsync<WorkflowException>(
            () => fixture.Workflow.AddAsync(ProjectDir, Url));

        Assert.Contains("--path addons/sprouty_dialogs", error.Message);
        Assert.Contains("--install-as sprouty_dialogs", error.Message);
    }

    [Fact]
    public async Task Add_DeclinedGuess_WritesNothing()
    {
        var fixture = SproutyDialogs(DeclineSubtreeChoice.Instance);

        await Assert.ThrowsAsync<WorkflowException>(() => fixture.Workflow.AddAsync(ProjectDir, Url));

        Assert.Equal(EmptyProject, fixture.FileSystem.ReadAllText(ManifestPath));
        Assert.False(fixture.FileSystem.DirectoryExists($"{ProjectDir}/addons/sprouty_dialogs"));
        Assert.False(fixture.FileSystem.FileExists($"{ProjectDir}/dotquay.lock"));
    }

    [Fact]
    public async Task Add_NoSuggestion_RefusesWithoutAsking()
    {
        // Several plugins and none obviously this one. There is nothing to confirm, so nobody is
        // asked — the message lists what it found and the user names one.
        var fixture = Build(null, "addons/one/plugin.cfg", "addons/two/plugin.cfg");

        var error = await Assert.ThrowsAsync<WorkflowException>(
            () => fixture.Workflow.AddAsync(ProjectDir, Url));

        Assert.Null(fixture.Choice.Asked);
        Assert.Contains("'addons/one'", error.Message);
        Assert.Contains("'addons/two'", error.Message);
        Assert.Contains("--path <subtree>", error.Message);
    }

    // -- when the question doesn't arise -----------------------------------------------------------

    [Fact]
    public async Task Add_ExplicitPath_IsNeverSecondGuessed()
    {
        // Also the channel a UI replays a confirmed suggestion through, so this doubles as the test
        // that confirming in the editor plugin doesn't re-ask on the run that follows.
        var fixture = SproutyDialogs();

        var locked = await fixture.Workflow.AddAsync(
            ProjectDir,
            Url,
            new AddonAddOptions { Path = "addons/sprouty_dialogs", InstallAs = "sprouty_dialogs" });

        Assert.Null(fixture.Choice.Asked);
        Assert.Equal("addons/sprouty_dialogs", locked.Path);
    }

    [Fact]
    public async Task Add_GuessIsRight_AsksNothing()
    {
        var fixture = Build(null, "addons/SproutyDialogs/plugin.cfg");

        var locked = await fixture.Workflow.AddAsync(ProjectDir, Url);

        Assert.Null(fixture.Choice.Asked);
        Assert.Equal("SproutyDialogs", locked.EffectiveFolder);
    }

    // -- the preview's half of the two-phase flow ---------------------------------------------------

    [Fact]
    public async Task Preview_ReportsTheGuessInsteadOfAskingAboutIt()
    {
        var fixture = SproutyDialogs();

        var preview = await fixture.Workflow.PreviewAddAsync(ProjectDir, Url);

        Assert.Null(fixture.Choice.Asked);
        Assert.NotNull(preview.Subtree);
        Assert.Equal("addons/SproutyDialogs", preview.Subtree!.GuessedPath);
        Assert.Equal("addons/sprouty_dialogs", preview.Subtree.Path);
        Assert.Equal("sprouty_dialogs", preview.Subtree.Folder);
        Assert.Equal("name_shape", preview.Subtree.Reason);
        Assert.Contains("sprouty_dialogs", preview.Subtree.Explanation);
    }

    [Fact]
    public async Task Preview_ResolvesTheGraphAgainstTheSuggestion()
    {
        // The preview has to describe the install a confirmation would produce. Resolving against a
        // path that isn't there would report an addon with no dependencies and no version, which is
        // precisely the misinformation a confirmation dialog exists to prevent.
        var fixture = SproutyDialogs();

        var preview = await fixture.Workflow.PreviewAddAsync(ProjectDir, Url);

        var addon = Assert.Single(preview.Addons);
        Assert.Equal("SproutyDialogs", addon.Name);
        Assert.Equal("sprouty_dialogs", addon.Folder);
    }

    [Fact]
    public async Task Preview_WritesNothing()
    {
        var fixture = SproutyDialogs();

        await fixture.Workflow.PreviewAddAsync(ProjectDir, Url);

        Assert.Equal(EmptyProject, fixture.FileSystem.ReadAllText(ManifestPath));
        Assert.False(fixture.FileSystem.DirectoryExists($"{ProjectDir}/addons/sprouty_dialogs"));
    }

    [Fact]
    public async Task Preview_GuessIsRight_ReportsNoSubtree()
    {
        var fixture = Build(null, "addons/SproutyDialogs/plugin.cfg");

        Assert.Null((await fixture.Workflow.PreviewAddAsync(ProjectDir, Url)).Subtree);
    }
}
