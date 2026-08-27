using System.CommandLine;
using Dotquay.Core.Json;
using Dotquay.Core.Workflows;

namespace Dotquay.Cli.Commands;

internal static class StatusCommand
{
    private const string CommandName = "status";

    public static Command Build()
    {
        var jsonOption = JsonOutput.CreateOption();
        var command = new Command(CommandName, "Diff disk against the lockfile: clean / patched as expected / modified.")
        {
            jsonOption,
        };

        command.SetAction(parseResult =>
        {
            var json = parseResult.GetValue(jsonOption);

            try
            {
                var result = WorkflowFactory.Create().Status(GlobalOptions.Resolve(parseResult));

                if (json)
                {
                    Console.WriteLine(DotquayJson.Write(JsonEnvelope<StatusJson>.Success(CommandName, JsonMapper.ToJson(result))));
                    return 0;
                }

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
            }
            catch (WorkflowException ex)
            {
                return JsonOutput.Fail(json, CommandName, ex);
            }
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
