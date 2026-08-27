using System.CommandLine;
using Dotquay.Core.Json;
using Dotquay.Core.Workflows;

namespace Dotquay.Cli.Commands;

internal static class AddCommand
{
    private const string CommandName = "add";

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

        var jsonOption = JsonOutput.CreateOption();

        var command = new Command(CommandName, "Add a dependency (git URL or NuGet package ID), resolve it, lock it, and install it.")
        {
            sourceArgument,
            pathOption,
            versionOption,
            jsonOption,
        };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var source = parseResult.GetValue(sourceArgument)!;
            var path = parseResult.GetValue(pathOption);
            var version = parseResult.GetValue(versionOption);
            var json = parseResult.GetValue(jsonOption);
            var projectDirectory = GlobalOptions.Resolve(parseResult);
            var workflow = WorkflowFactory.Create();

            try
            {
                return LooksLikeGitUrl(source)
                    ? await AddGitAsync(workflow, projectDirectory, source, path, json, cancellationToken)
                    : await AddDotnetAsync(workflow, projectDirectory, source, version, json, cancellationToken);
            }
            catch (WorkflowException ex)
            {
                return JsonOutput.Fail(json, CommandName, ex);
            }
        });

        return command;
    }

    private static async Task<int> AddGitAsync(
        DependencyWorkflow workflow, string projectDirectory, string gitUrl, string? path, bool json, CancellationToken cancellationToken)
    {
        var locked = await workflow.AddAsync(projectDirectory, gitUrl, path, cancellationToken);
        var summary = $"Added '{locked.Name}' @ {locked.Version} ({ShortSha(locked.Resolved)}) -> addons/{locked.Name}";

        // Pseudo-version means BuildDefaultSpecAsync fell back to pinning the default branch's tip as
        // a hard `rev` (SPEC.md §6: "No tags found -> ... print a hint"). Without --json this is a
        // yellow console line; with it, the hint travels in the envelope's warnings rather than being
        // dropped on the floor — a GUI user needs it just as much.
        var untagged = locked.Version is not null && locked.Version.StartsWith("0.0.0-", StringComparison.Ordinal);
        var hint = $"'{locked.Name}' has no git tags — pinned to its current default-branch commit instead of a "
            + "version range. Run 'dotquay update' later to move to a newer commit.";

        if (json)
        {
            JsonOutput.WriteOperation(
                CommandName,
                new OperationJson
                {
                    Changes =
                    [
                        new ChangeJson
                        {
                            Name = locked.Name,
                            Kind = JsonMapper.ChangeKind.Addon,
                            Action = JsonMapper.ChangeAction.Added,
                            To = locked.Version ?? ShortSha(locked.Resolved),
                        },
                    ],
                    Messages = [summary],
                },
                untagged ? [hint] : null);

            return 0;
        }

        ConsoleOutput.WriteLine(ConsoleColor.Green, summary);

        if (untagged)
        {
            ConsoleOutput.WriteLine(ConsoleColor.Yellow, hint);
        }

        return 0;
    }

    private static async Task<int> AddDotnetAsync(
        DependencyWorkflow workflow, string projectDirectory, string packageId, string? version, bool json, CancellationToken cancellationToken)
    {
        var locked = await workflow.AddDotnetAsync(projectDirectory, packageId, version, cancellationToken);
        var summary = $"Added '{locked.Name}' @ {locked.Resolved} (NuGet) -> .csproj, restored";

        if (json)
        {
            JsonOutput.WriteOperation(CommandName, new OperationJson
            {
                Changes =
                [
                    new ChangeJson
                    {
                        Name = locked.Name,
                        Kind = JsonMapper.ChangeKind.Dotnet,
                        Action = JsonMapper.ChangeAction.Added,
                        To = locked.Resolved,
                    },
                ],
                Messages = [summary],
            });
            return 0;
        }

        ConsoleOutput.WriteLine(ConsoleColor.Green, summary);
        return 0;
    }

    /// <summary>Distinguishes a git URL from a bare NuGet package ID (e.g. "Newtonsoft.Json").
    /// Package IDs don't contain "://" or start with an SSH-style "git@" — good enough to route
    /// v0.1's two supported `add` sources without a full URL grammar.</summary>
    private static bool LooksLikeGitUrl(string source) =>
        source.Contains("://", StringComparison.Ordinal) || source.StartsWith("git@", StringComparison.Ordinal);

    private static string ShortSha(string sha) => sha.Length > 7 ? sha[..7] : sha;
}
