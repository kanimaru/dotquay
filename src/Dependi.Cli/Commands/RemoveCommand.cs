using System.CommandLine;
using Dependi.Core.Workflows;

namespace Dependi.Cli.Commands;

internal static class RemoveCommand
{
    public static Command Build()
    {
        var nameArgument = new Argument<string>("name")
        {
            Description = "The addon or dotnet dependency name to remove.",
        };

        var command = new Command("remove", "Remove a dependency from the manifest, lockfile, and disk.")
        {
            nameArgument,
        };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var name = parseResult.GetValue(nameArgument)!;
            var workflow = WorkflowFactory.Create();

            try
            {
                await workflow.RemoveAsync(Directory.GetCurrentDirectory(), name, cancellationToken);
                ConsoleOutput.WriteLine(ConsoleColor.Green, $"Removed '{name}'.");
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
