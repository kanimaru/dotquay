using System.CommandLine;
using Dotquay.Core.Workflows;

namespace Dotquay.Cli.Commands;

internal static class OutdatedCommand
{
    public static Command Build()
    {
        var command = new Command("outdated", "Check for newer versions without cloning anything.");

        command.SetAction(async (_, cancellationToken) =>
        {
            var workflow = WorkflowFactory.Create();

            try
            {
                var result = await workflow.OutdatedAsync(Directory.GetCurrentDirectory(), cancellationToken);

                var outdatedAddons = result.Addons.Where(a => a.IsOutdated).ToList();
                var outdatedDotnet = result.Dotnet.Where(d => d.IsOutdated).ToList();

                if (outdatedAddons.Count == 0 && outdatedDotnet.Count == 0)
                {
                    Console.WriteLine("Everything is up to date.");
                    return 0;
                }

                foreach (var addon in outdatedAddons.OrderBy(a => a.Name, StringComparer.Ordinal))
                {
                    ConsoleOutput.WriteLine(ConsoleColor.Yellow, $"{addon.Name}  {addon.CurrentDisplay} -> {addon.LatestDisplay}");
                }

                foreach (var dependency in outdatedDotnet.OrderBy(d => d.Name, StringComparer.Ordinal))
                {
                    ConsoleOutput.WriteLine(
                        ConsoleColor.Yellow, $"{dependency.Name}  {dependency.CurrentVersion} -> {dependency.LatestVersion}  (NuGet)");
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
