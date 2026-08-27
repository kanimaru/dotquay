using System.CommandLine;
using Dotquay.Core.Workflows;

namespace Dotquay.Cli.Commands;

internal static class AddCommand
{
    public static Command Build()
    {
        var sourceArgument = new Argument<string>("source")
        {
            Description = "A git URL or a NuGet package ID.",
        };

        var pathOption = new Option<string?>("--path")
        {
            Description = "Git sources only. Subtree to extract from the addon's repo, if it isn't at "
                + "addons/<name> (e.g. the repo is named godot_dialogue_manager but the addon folder "
                + "inside it is just dialogue_manager). Repos where the addon lives at the repo root: pass \".\".",
        };

        var versionOption = new Option<string?>("--version")
        {
            Description = "NuGet sources only. Exact package version to add. Defaults to the latest stable release.",
        };

        var command = new Command("add", "Add a dependency (git URL or NuGet package ID), resolve it, lock it, and install it.")
        {
            sourceArgument,
            pathOption,
            versionOption,
        };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var source = parseResult.GetValue(sourceArgument)!;
            var path = parseResult.GetValue(pathOption);
            var version = parseResult.GetValue(versionOption);
            var workflow = WorkflowFactory.Create();

            try
            {
                return LooksLikeGitUrl(source)
                    ? await AddGitAsync(workflow, source, path, cancellationToken)
                    : await AddDotnetAsync(workflow, source, version, cancellationToken);
            }
            catch (WorkflowException ex)
            {
                ConsoleOutput.WriteLine(ConsoleColor.Red, ex.Message);
                return 1;
            }
        });

        return command;
    }

    private static async Task<int> AddGitAsync(DependencyWorkflow workflow, string gitUrl, string? path, CancellationToken cancellationToken)
    {
        var locked = await workflow.AddAsync(Directory.GetCurrentDirectory(), gitUrl, path, cancellationToken);
        ConsoleOutput.WriteLine(ConsoleColor.Green, $"Added '{locked.Name}' @ {locked.Version} ({ShortSha(locked.Resolved)}) -> addons/{locked.Name}");

        if (locked.Version is not null && locked.Version.StartsWith("0.0.0-", StringComparison.Ordinal))
        {
            // Pseudo-version means BuildDefaultSpecAsync fell back to pinning the default branch's
            // tip as a hard `rev` (SPEC.md §6: "No tags found -> ... print a hint").
            ConsoleOutput.WriteLine(
                ConsoleColor.Yellow,
                $"'{locked.Name}' has no git tags — pinned to its current default-branch commit instead of a "
                + "version range. Run 'dotquay update' later to move to a newer commit.");
        }

        return 0;
    }

    private static async Task<int> AddDotnetAsync(
        DependencyWorkflow workflow, string packageId, string? version, CancellationToken cancellationToken)
    {
        var locked = await workflow.AddDotnetAsync(Directory.GetCurrentDirectory(), packageId, version, cancellationToken);
        ConsoleOutput.WriteLine(ConsoleColor.Green, $"Added '{locked.Name}' @ {locked.Resolved} (NuGet) -> .csproj, restored");
        return 0;
    }

    /// <summary>Distinguishes a git URL from a bare NuGet package ID (e.g. "Newtonsoft.Json").
    /// Package IDs don't contain "://" or start with an SSH-style "git@" — good enough to route
    /// v0.1's two supported `add` sources without a full URL grammar.</summary>
    private static bool LooksLikeGitUrl(string source) =>
        source.Contains("://", StringComparison.Ordinal) || source.StartsWith("git@", StringComparison.Ordinal);

    private static string ShortSha(string sha) => sha.Length > 7 ? sha[..7] : sha;
}
