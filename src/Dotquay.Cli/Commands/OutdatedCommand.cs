using System.CommandLine;
using Dotquay.Core.Json;

namespace Dotquay.Cli.Commands;

internal static class OutdatedCommand
{
    private const string CommandName = "outdated";

    public static Command Build()
    {
        var jsonOption = JsonOutput.CreateOption();
        var command = new Command(CommandName, "Check for newer versions without cloning anything.") { jsonOption };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var json = parseResult.GetValue(jsonOption);
            var (workflow, progress) = WorkflowFactory.CreateWithProgress(json);

            try
            {
                var result = await workflow.OutdatedAsync(GlobalOptions.Resolve(parseResult), cancellationToken);
                progress.Dispose();

                if (json)
                {
                    Console.WriteLine(DotquayJson.Write(JsonEnvelope<OutdatedJson>.Success(CommandName, JsonMapper.ToJson(result))));
                    return 0;
                }

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
            catch (Exception ex) when (JsonOutput.IsExpectedFailure(ex))
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
}
