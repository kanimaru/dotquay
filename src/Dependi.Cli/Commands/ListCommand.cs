using System.CommandLine;
using Dependi.Core.Workflows;

namespace Dependi.Cli.Commands;

internal static class ListCommand
{
    public static Command Build()
    {
        var command = new Command("list", "Show installed dependencies with their versions.");

        command.SetAction(_ =>
        {
            var workflow = WorkflowFactory.Create();

            try
            {
                var projectDirectory = Directory.GetCurrentDirectory();
                var addons = workflow.ListInstalled(projectDirectory);
                var dotnet = workflow.ListInstalledDotnet(projectDirectory);

                if (addons.Count == 0 && dotnet.Count == 0)
                {
                    Console.WriteLine("Nothing locked yet. Run 'dependi add <git-url>' or 'dependi add <nuget-id>' to add one.");
                    return 0;
                }

                foreach (var addon in addons.OrderBy(a => a.Name, StringComparer.Ordinal))
                {
                    var version = addon.Version ?? "(unversioned)";
                    var dirty = addon.PatchHash is not null ? " [patched]" : "";
                    Console.WriteLine($"{addon.Name}  {version}  {ShortSha(addon.Resolved)}{dirty}");
                }

                foreach (var dependency in dotnet.OrderBy(d => d.Name, StringComparer.Ordinal))
                {
                    Console.WriteLine($"{dependency.Name}  {dependency.Resolved}  (NuGet)");
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

    private static string ShortSha(string sha) => sha.Length > 7 ? sha[..7] : sha;
}
