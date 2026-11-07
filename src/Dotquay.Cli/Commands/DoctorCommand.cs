using System.CommandLine;
using Dotquay.Core.Json;
using Dotquay.Core.Workflows;

namespace Dotquay.Cli.Commands;

internal static class DoctorCommand {
    private const string CommandName = "doctor";

    public static Command Build() {
        var jsonOption = JsonOutput.CreateOption();
        var command = new Command(CommandName,
            "Run sanity checks: git on PATH, dangling lockfile entries, plugin.cfg drift.")
        {
            jsonOption,
        };

        command.SetAction(async (parseResult, cancellationToken) => {
            var json = parseResult.GetValue(jsonOption);

            try {
                var result = await WorkflowFactory.Create()
                    .DoctorAsync(GlobalOptions.Resolve(parseResult), cancellationToken);

                if (json) {
                    // Issues are data, not a crash: the envelope stays ok:true and the caller reads
                    // data.healthy. Only the exit code carries the "something is wrong" signal, for
                    // shell users and CI.
                    Console.WriteLine(
                        DotquayJson.Write(JsonEnvelope<DoctorJson>.Success(CommandName,
                            JsonMapper.ToJson(result))));
                    return result.IsHealthy ? 0 : 1;
                }

                foreach (var issue in result.Issues) {
                    ConsoleOutput.WriteLine(ConsoleColor.Yellow, issue.Message);
                }

                if (result.IsHealthy) {
                    ConsoleOutput.WriteLine(ConsoleColor.Green, "No issues found.");
                }

                // Printed whether or not anything is wrong, and after the issues so it reads as
                // context rather than as a complaint. Trust is not a fault — but it is a security
                // control that gets copy-pasted between projects and then forgotten, and the only
                // way an over-broad entry stays visible is if something says it out loud.
                WriteTrust(result.Trust);

                return result.IsHealthy ? 0 : 1;
            } catch (Exception ex) when (JsonOutput.IsExpectedFailure(ex)) {
                return JsonOutput.Fail(json, CommandName, ex);
            }
        });

        return command;
    }

    /// <summary>Shows what the project trusts and which feeds arrived through that trust
    /// (SPEC.md §11.3). Silent for a project that never opted in, so the common case gains no
    /// noise.</summary>
    private static void WriteTrust(DoctorTrust trust) {
        if (trust.IsEmpty) {
            return;
        }

        Console.WriteLine();

        if (trust.TrustedHosts.Count > 0) {
            Console.WriteLine("Trusted NuGet hosts (addon-declared feeds on these are adopted "
                + "without asking):");
            foreach (var host in trust.TrustedHosts) {
                Console.WriteLine($"  {host}");
            }
        }

        if (trust.AdoptedSources.Count == 0) {
            return;
        }

        Console.WriteLine("NuGet sources adopted from addons:");
        foreach (var source in trust.AdoptedSources) {
            Console.WriteLine($"  {source.Name}  ({source.Url})  from addon "
                + $"'{source.DeclaredByAddon}'");
        }
    }
}
