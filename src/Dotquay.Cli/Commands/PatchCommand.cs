using System.CommandLine;
using Dotquay.Core.Json;
using Dotquay.Core.Workflows;

namespace Dotquay.Cli.Commands;

/// <summary>
/// <c>dotquay patch</c> — capture hand-edits to an installed addon (SPEC.md §10), and the two ways
/// out of a conflict when upstream later touches the same lines.
/// </summary>
/// <remarks>
/// Re-applying a patch is automatic on every install and update; this command exists for the two
/// moments that aren't: recording the edit in the first place, and deciding what to do when a
/// three-way merge can't settle it. <c>--resolve</c> and <c>--abandon</c> replace a three-step
/// instruction the conflict message used to print, whose middle step (recapturing) was the one
/// people skipped — and skipping it leaves the lockfile describing a patch that no longer matches
/// the tree.
/// </remarks>
internal static class PatchCommand {
    private const string CommandName = "patch";

    public static Command Build() {
        var nameArgument = new Argument<string>("name") {
            Description = "The installed addon to diff against its pristine cache copy.",
        };

        var resolveOption = new Option<bool>("--resolve") {
            Description = "Finish a conflicted update: install the files you resolved in staging, "
                + "then recapture the patch against the new upstream commit.",
        };

        var abandonOption = new Option<bool>("--abandon") {
            Description = "Drop this addon's patch and take upstream as-is. For when the change "
                + "was upstreamed or is no longer wanted.",
        };

        var jsonOption = JsonOutput.CreateOption();

        var command = new Command(CommandName,
            "Capture hand-edits to an installed addon as a patch file.")
        {
            nameArgument,
            resolveOption,
            abandonOption,
            jsonOption,
        };

        command.SetAction(async (parseResult, cancellationToken) => {
            var name = parseResult.GetValue(nameArgument)!;
            var json = parseResult.GetValue(jsonOption);
            var resolve = parseResult.GetValue(resolveOption);
            var abandon = parseResult.GetValue(abandonOption);
            var projectDirectory = GlobalOptions.Resolve(parseResult);
            var workflow = WorkflowFactory.Create();

            if (resolve && abandon) {
                return JsonOutput.Fail(
                    json, CommandName, JsonErrorKind.Workflow,
                    "--resolve and --abandon are opposites: one keeps your patch, the other "
                    + "discards it.");
            }

            try {
                var result = resolve
                    ? await workflow.ResolvePatchAsync(projectDirectory, name, cancellationToken)
                    : abandon
                        ? await workflow.AbandonPatchAsync(projectDirectory, name, force: true,
                            cancellationToken)
                        : await workflow.PatchAsync(projectDirectory, name, cancellationToken);

                var message = Describe(result, resolve, abandon);

                if (json) {
                    JsonOutput.WriteOperation(CommandName, new OperationJson {
                        Changes =
                        [
                            new ChangeJson
                            {
                                Name = result.Name,
                                Kind = JsonMapper.ChangeKind.Addon,
                                Action = abandon ? JsonMapper.ChangeAction.Removed
                                    : result.Captured ? JsonMapper.ChangeAction.Updated
                                    : JsonMapper.ChangeAction.Unchanged,
                                To = result.Captured ? result.PatchFilePath : null,
                            },
                        ],
                        Messages = [message],
                    });
                    return 0;
                }

                if (result.Captured || abandon) {
                    ConsoleOutput.WriteLine(ConsoleColor.Green, message);
                } else {
                    Console.WriteLine(message);
                }

                return 0;
            } catch (Exception ex) when (JsonOutput.IsExpectedFailure(ex)) {
                return JsonOutput.Fail(json, CommandName, ex);
            }
        });

        return command;
    }

    private static string Describe(PatchCaptureResult result, bool resolve, bool abandon) {
        if (abandon) {
            return $"Dropped '{result.Name}'s patch and reinstalled it from upstream.";
        }

        if (resolve) {
            return result.Captured
                ? $"Resolved '{result.Name}' and recaptured its patch -> {result.PatchFilePath}"
                : $"Resolved '{result.Name}'. The result matches upstream exactly, so there is no "
                + $"patch left to keep.";
        }

        return result.Captured
            ? $"Captured '{result.Name}' -> {result.PatchFilePath}"
            : $"'{result.Name}' matches its pristine base — nothing to capture.";
    }
}
