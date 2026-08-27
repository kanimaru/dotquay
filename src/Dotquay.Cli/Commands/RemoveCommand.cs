using System.CommandLine;
using Dotquay.Core.Json;
using Dotquay.Core.Workflows;

namespace Dotquay.Cli.Commands;

internal static class RemoveCommand
{
    private const string CommandName = "remove";

    public static Command Build()
    {
        var nameArgument = new Argument<string>("name")
        {
            Description = "The addon or dotnet dependency name to remove.",
        };

        var jsonOption = JsonOutput.CreateOption();

        var command = new Command(CommandName, "Remove a dependency from the manifest, lockfile, and disk.")
        {
            nameArgument,
            jsonOption,
        };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var name = parseResult.GetValue(nameArgument)!;
            var json = parseResult.GetValue(jsonOption);
            var workflow = WorkflowFactory.Create();

            try
            {
                await workflow.RemoveAsync(GlobalOptions.Resolve(parseResult), name, cancellationToken);

                if (json)
                {
                    JsonOutput.WriteOperation(CommandName, new OperationJson
                    {
                        Changes =
                        [
                            new ChangeJson
                            {
                                Name = name,
                                // RemoveAsync accepts either an addon or a dotnet name and doesn't
                                // report which it matched, so the wire value stays "addon" — the
                                // caller already knows what it asked to remove, and inventing a
                                // third "unknown" kind would only add a case every consumer must
                                // handle.
                                Kind = JsonMapper.ChangeKind.Addon,
                                Action = JsonMapper.ChangeAction.Removed,
                            },
                        ],
                        Messages = [$"Removed '{name}'."],
                    });
                    return 0;
                }

                ConsoleOutput.WriteLine(ConsoleColor.Green, $"Removed '{name}'.");
                return 0;
            }
            catch (WorkflowException ex)
            {
                return JsonOutput.Fail(json, CommandName, ex);
            }
        });

        return command;
    }
}
