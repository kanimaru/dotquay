using System.CommandLine;
using Dependi.Core.Workflows;

namespace Dependi.Cli.Commands;

internal static class DoctorCommand
{
    public static Command Build()
    {
        var command = new Command("doctor", "Run sanity checks: git on PATH, dangling lockfile entries, plugin.cfg drift.");

        command.SetAction(async (_, cancellationToken) =>
        {
            var workflow = WorkflowFactory.Create();
            var result = await workflow.DoctorAsync(Directory.GetCurrentDirectory(), cancellationToken);

            if (result.IsHealthy)
            {
                ConsoleOutput.WriteLine(ConsoleColor.Green, "No issues found.");
                return 0;
            }

            foreach (var issue in result.Issues)
            {
                ConsoleOutput.WriteLine(ConsoleColor.Yellow, issue.Message);
            }

            return 1;
        });

        return command;
    }
}
