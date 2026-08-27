using System.CommandLine;
using Dotquay.Core.Workflows;

namespace Dotquay.Cli.Commands;

internal static class StatusCommand
{
    public static Command Build()
    {
        var command = new Command("status", "Diff disk against the lockfile: clean / patched as expected / modified.");

        command.SetAction(_ =>
        {
            var workflow = WorkflowFactory.Create();
            var result = workflow.Status(Directory.GetCurrentDirectory());

            if (result.Addons.Count == 0)
            {
                Console.WriteLine("Nothing locked yet. Run 'dotquay add <git-url>' to add one.");
                return 0;
            }

            foreach (var addon in result.Addons.OrderBy(a => a.Name, StringComparer.Ordinal))
            {
                Print(addon);
            }

            return 0;
        });

        return command;
    }

    private static void Print(AddonStatusInfo addon)
    {
        switch (addon.Kind)
        {
            case AddonStatusKind.Clean:
                Console.WriteLine($"{addon.Name}  clean");
                break;
            case AddonStatusKind.PatchedAsExpected:
                ConsoleOutput.WriteLine(ConsoleColor.Cyan, $"{addon.Name}  patched as expected");
                break;
            case AddonStatusKind.Modified:
                ConsoleOutput.WriteLine(ConsoleColor.Red, $"{addon.Name}  modified (hand-edited, not captured as a patch)");
                break;
            case AddonStatusKind.Missing:
                ConsoleOutput.WriteLine(ConsoleColor.Yellow, $"{addon.Name}  missing (run 'dotquay install')");
                break;
        }
    }
}
