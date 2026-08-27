using System.CommandLine;
using Dotquay.Core.Workflows;

namespace Dotquay.Cli.Commands;

internal static class UpdateCommand
{
    public static Command Build()
    {
        var nameArgument = new Argument<string?>("name")
        {
            Description = "Dependency to update. Omit to re-resolve and reinstall everything.",
            Arity = ArgumentArity.ZeroOrOne,
        };

        var forceOption = new Option<bool>("--force")
        {
            Description = "Overwrite a hand-modified addon instead of refusing (bypasses dirty detection, P0-6).",
        };

        var command = new Command("update", "Re-resolve one dependency (or all of them) and reinstall.")
        {
            nameArgument,
            forceOption,
        };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var name = parseResult.GetValue(nameArgument);
            var force = parseResult.GetValue(forceOption);
            var workflow = WorkflowFactory.Create();

            try
            {
                var result = await workflow.UpdateAsync(Directory.GetCurrentDirectory(), name, force, cancellationToken);

                if (result.Addons.Count == 0 && result.Dotnet.Count == 0)
                {
                    Console.WriteLine("Nothing declared in the manifest — nothing to update.");
                    return 0;
                }

                foreach (var addon in result.Addons.OrderBy(r => r.Name, StringComparer.Ordinal))
                {
                    PrintAddonResult(addon);
                }

                foreach (var dependency in result.Dotnet.OrderBy(r => r.Name, StringComparer.Ordinal))
                {
                    PrintDotnetResult(dependency);
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

    private static void PrintAddonResult(AddonUpdateResult result)
    {
        if (!result.Changed)
        {
            Console.WriteLine($"  {result.Name} @ {result.Locked.Version} (unchanged)");
            return;
        }

        var from = result.PreviousResolved is null ? "(not previously locked)" : ShortSha(result.PreviousResolved);
        ConsoleOutput.WriteLine(
            ConsoleColor.Green, $"  {result.Name} @ {result.Locked.Version}: {from} -> {ShortSha(result.Locked.Resolved)}");
    }

    private static void PrintDotnetResult(DotnetUpdateResult result)
    {
        if (!result.Changed)
        {
            Console.WriteLine($"  {result.Name} @ {result.Locked.Resolved} (unchanged, NuGet)");
            return;
        }

        var from = result.PreviousResolved ?? "(not previously locked)";
        ConsoleOutput.WriteLine(ConsoleColor.Green, $"  {result.Name} (NuGet): {from} -> {result.Locked.Resolved}");
    }

    private static string ShortSha(string sha) => sha.Length > 7 ? sha[..7] : sha;
}
