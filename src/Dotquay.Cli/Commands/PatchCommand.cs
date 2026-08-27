using System.CommandLine;
using Dotquay.Core.Workflows;

namespace Dotquay.Cli.Commands;

internal static class PatchCommand
{
    public static Command Build()
    {
        var nameArgument = new Argument<string>("name")
        {
            Description = "The installed addon to diff against its pristine cache copy.",
        };

        var command = new Command("patch", "Capture hand-edits to an installed addon as a patch file.")
        {
            nameArgument,
        };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var name = parseResult.GetValue(nameArgument)!;
            var workflow = WorkflowFactory.Create();

            try
            {
                var result = await workflow.PatchAsync(Directory.GetCurrentDirectory(), name, cancellationToken);

                if (result.Captured)
                {
                    ConsoleOutput.WriteLine(ConsoleColor.Green, $"Captured '{result.Name}' -> {result.PatchFilePath}");
                }
                else
                {
                    Console.WriteLine($"'{result.Name}' matches its pristine base — nothing to capture.");
                }

                return 0;
            }
            catch (WorkflowException ex)
            {
                ConsoleOutput.WriteLine(ConsoleColor.Red, ex.Message);
                return 1;
            }
        });

        return command;
    }
}
