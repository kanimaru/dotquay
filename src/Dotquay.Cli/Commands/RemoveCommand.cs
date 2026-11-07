using System.CommandLine;
using Dotquay.Core.Json;
using Dotquay.Core.Workflows;

namespace Dotquay.Cli.Commands;

internal static class RemoveCommand {
    private const string CommandName = "remove";

    public static Command Build() {
        var nameArgument = new Argument<string>("name") {
            Description = "The addon or dotnet dependency name to remove.",
        };

        var jsonOption = JsonOutput.CreateOption();

        var command = new Command(CommandName, "Remove a dependency from the manifest, lockfile, and disk.")
        {
            nameArgument,
            jsonOption,
        };

        command.SetAction(async (parseResult, cancellationToken) => {
            var name = parseResult.GetValue(nameArgument)!;
            var json = parseResult.GetValue(jsonOption);
            var workflow = WorkflowFactory.Create();

            try {
                var removal = await workflow.RemoveAsync(GlobalOptions.Resolve(parseResult), name, cancellationToken);

                if (json) {
                    JsonOutput.WriteOperation(CommandName, new OperationJson {
                        Changes =
                        [
                            new ChangeJson
                            {
                                Name = name,
                                Kind = removal.Kind == DependencyKind.Dotnet ? JsonMapper.ChangeKind.Dotnet : JsonMapper.ChangeKind.Addon,
                                Action = JsonMapper.ChangeAction.Removed,
                            },
                        ],
                        Messages = [removal.Describe(name)],
                    });
                    return 0;
                }

                ConsoleOutput.WriteLine(removal.KeptForAddons ? ConsoleColor.Yellow : ConsoleColor.Green, removal.Describe(name));
                return 0;
            } catch (Exception ex) when (JsonOutput.IsExpectedFailure(ex)) {
                return JsonOutput.Fail(json, CommandName, ex);
            }
        });

        return command;
    }
}
