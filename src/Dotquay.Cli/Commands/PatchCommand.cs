using System.CommandLine;
using Dotquay.Core.Json;
using Dotquay.Core.Workflows;

namespace Dotquay.Cli.Commands;

internal static class PatchCommand
{
    private const string CommandName = "patch";

    public static Command Build()
    {
        var nameArgument = new Argument<string>("name")
        {
            Description = "The installed addon to diff against its pristine cache copy.",
        };

        var jsonOption = JsonOutput.CreateOption();

        var command = new Command(CommandName, "Capture hand-edits to an installed addon as a patch file.")
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
                var result = await workflow.PatchAsync(GlobalOptions.Resolve(parseResult), name, cancellationToken);

                var message = result.Captured
                    ? $"Captured '{result.Name}' -> {result.PatchFilePath}"
                    : $"'{result.Name}' matches its pristine base — nothing to capture.";

                if (json)
                {
                    JsonOutput.WriteOperation(CommandName, new OperationJson
                    {
                        Changes =
                        [
                            new ChangeJson
                            {
                                Name = result.Name,
                                Kind = JsonMapper.ChangeKind.Addon,
                                Action = result.Captured ? JsonMapper.ChangeAction.Updated : JsonMapper.ChangeAction.Unchanged,
                                To = result.Captured ? result.PatchFilePath : null,
                            },
                        ],
                        Messages = [message],
                    });
                    return 0;
                }

                if (result.Captured)
                {
                    ConsoleOutput.WriteLine(ConsoleColor.Green, message);
                }
                else
                {
                    Console.WriteLine(message);
                }

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
