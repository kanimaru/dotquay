using System.CommandLine;
using Dotquay.Core.Json;
using Dotquay.Core.Workflows;

namespace Dotquay.Cli.Commands;

internal static class DoctorCommand
{
    private const string CommandName = "doctor";

    public static Command Build()
    {
        var jsonOption = JsonOutput.CreateOption();
        var command = new Command(CommandName, "Run sanity checks: git on PATH, dangling lockfile entries, plugin.cfg drift.")
        {
            jsonOption,
        };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var json = parseResult.GetValue(jsonOption);

            try
            {
                var result = await WorkflowFactory.Create().DoctorAsync(GlobalOptions.Resolve(parseResult), cancellationToken);

                if (json)
                {
                    // Issues are data, not a crash: the envelope stays ok:true and the caller reads
                    // data.healthy. Only the exit code carries the "something is wrong" signal, for
                    // shell users and CI.
                    Console.WriteLine(DotquayJson.Write(JsonEnvelope<DoctorJson>.Success(CommandName, JsonMapper.ToJson(result))));
                    return result.IsHealthy ? 0 : 1;
                }

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
            }
            catch (WorkflowException ex)
            {
                return JsonOutput.Fail(json, CommandName, ex);
            }
        });

        return command;
    }
}
