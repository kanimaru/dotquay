using System.CommandLine;
using Dotquay.Core.Json;
using Dotquay.Core.Workflows;

namespace Dotquay.Cli.Commands;

internal static class InstallCommand
{
    private const string CommandName = "install";

    public static Command Build()
    {
        var forceOption = new Option<bool>("--force")
        {
            Description = "Overwrite a hand-modified addon instead of refusing (bypasses dirty detection, P0-6).",
        };

        var jsonOption = JsonOutput.CreateOption();

        var command = new Command(CommandName, "Make addons/ and .csproj match the lockfile exactly.")
        {
            forceOption,
            jsonOption,
        };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var force = parseResult.GetValue(forceOption);
            var json = parseResult.GetValue(jsonOption);
            var (workflow, progress) = WorkflowFactory.CreateWithProgress(json);

            try
            {
                var result = await workflow.InstallAsync(GlobalOptions.Resolve(parseResult), force, cancellationToken);
                progress.Dispose();

                if (json)
                {
                    JsonOutput.WriteOperation(CommandName, JsonMapper.ToJson(result));
                    return 0;
                }

                foreach (var addon in result.Addons.OrderBy(a => a.Name, StringComparer.Ordinal))
                {
                    ConsoleOutput.WriteLine(ConsoleColor.Green, $"  {addon.Name} @ {addon.Version} ({ShortSha(addon.Resolved)})");
                }

                foreach (var dependency in result.Dotnet.OrderBy(d => d.Name, StringComparer.Ordinal))
                {
                    ConsoleOutput.WriteLine(ConsoleColor.Green, $"  {dependency.Name} @ {dependency.Resolved} (NuGet)");
                }

                ConsoleOutput.WriteLine(
                    ConsoleColor.Green, $"Installed {result.Addons.Count} addon(s), {result.Dotnet.Count} dotnet package(s).");

                // After the summary, like update's warnings: the result someone waits for comes first.
                foreach (var note in result.SourceBuildNotes)
                {
                    ConsoleOutput.WriteLine(ConsoleColor.Yellow, note);
                }
                return 0;
            }
            catch (WorkflowException ex)
            {
                return JsonOutput.Fail(json, CommandName, ex);
            }
            finally
            {
                progress.Dispose();
            }
        });

        return command;
    }

    private static string ShortSha(string sha) => sha.Length > 7 ? sha[..7] : sha;
}
