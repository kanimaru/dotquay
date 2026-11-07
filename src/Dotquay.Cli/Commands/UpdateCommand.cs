using System.CommandLine;
using Dotquay.Core.Json;
using Dotquay.Core.Workflows;

namespace Dotquay.Cli.Commands;

internal static class UpdateCommand {
    private const string CommandName = "update";

    public static Command Build() {
        var nameArgument = new Argument<string?>("name") {
            Description = "Dependency to update. Omit to re-resolve and reinstall everything.",
            Arity = ArgumentArity.ZeroOrOne,
        };

        var forceOption = new Option<bool>("--force") {
            Description = "Overwrite a hand-modified addon instead of refusing (bypasses dirty "
                + "detection, P0-6).",
        };

        var jsonOption = JsonOutput.CreateOption();

        var command = new Command(CommandName,
            "Re-resolve one dependency (or all of them) and reinstall.")
        {
            nameArgument,
            forceOption,
            jsonOption,
        };

        command.SetAction(async (parseResult, cancellationToken) => {
            var name = parseResult.GetValue(nameArgument);
            var force = parseResult.GetValue(forceOption);
            var json = parseResult.GetValue(jsonOption);
            var (workflow, progress) = WorkflowFactory.CreateWithProgress(json,
                allowSourceAdoption: true);

            try {
                var result = await workflow.UpdateAsync(GlobalOptions.Resolve(parseResult), name,
                    force, cancellationToken);

                // Stopped before anything is printed, so the spinner's last frame isn't left
                // sitting on the line the results are about to occupy.
                progress.Dispose();

                if (json) {
                    JsonOutput.WriteOperation(CommandName, JsonMapper.ToJson(result),
                        result.Warnings);
                    return 0;
                }

                if (result.Addons.Count == 0 && result.Dotnet.Count == 0) {
                    Console.WriteLine("Nothing declared in the manifest — nothing to update.");
                    return 0;
                }

                foreach (var addon in result.Addons.OrderBy(r => r.Name, StringComparer.Ordinal)) {
                    PrintAddonResult(addon);
                }

                foreach (var dependency in result.Dotnet.OrderBy(r => r.Name,
                    StringComparer.Ordinal)) {
                    PrintDotnetResult(dependency);
                }

                // Printed last, and only after the results, so the thing someone is actually
                // waiting for isn't pushed off screen by an advisory note.
                foreach (var warning in result.Warnings) {
                    Console.WriteLine();
                    ConsoleOutput.WriteRedacted(ConsoleColor.Yellow, warning);
                }

                foreach (var note in result.SourceBuildNotes) {
                    Console.WriteLine();
                    ConsoleOutput.WriteLine(ConsoleColor.Yellow, note);
                }

                return 0;
            } catch (Exception ex) when (JsonOutput.IsExpectedFailure(ex)) {
                progress.Dispose();
                return JsonOutput.Fail(json, CommandName, ex);
            } finally {
                progress.Dispose();
            }
        });

        return command;
    }

    private static void PrintAddonResult(AddonUpdateResult result) {
        if (!result.Changed) {
            Console.WriteLine($"  {result.Name} @ {result.Locked.Version} (unchanged)");
            return;
        }

        var from = result.PreviousResolved is null ? "(not previously locked)"
            : ShortSha(result.PreviousResolved);
        ConsoleOutput.WriteLine(
            ConsoleColor.Green,
            $"  {result.Name} @ {result.Locked.Version}: {from} -> "
            + $"{ShortSha(result.Locked.Resolved)}");
    }

    private static void PrintDotnetResult(DotnetUpdateResult result) {
        if (!result.Changed) {
            Console.WriteLine($"  {result.Name} @ {result.Locked.Resolved} (unchanged, NuGet)");
            return;
        }

        var from = result.PreviousResolved ?? "(not previously locked)";
        ConsoleOutput.WriteLine(ConsoleColor.Green,
            $"  {result.Name} (NuGet): {from} -> {result.Locked.Resolved}");
    }

    private static string ShortSha(string sha) => sha.Length > 7 ? sha[..7] : sha;
}
