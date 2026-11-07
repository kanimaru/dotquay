using System.CommandLine;
using Dotquay.Core.Json;

namespace Dotquay.Cli.Commands;

internal static class ListCommand {
    private const string CommandName = "list";

    public static Command Build() {
        var jsonOption = JsonOutput.CreateOption();
        var command = new Command(CommandName, "Show installed dependencies with their versions.") { jsonOption };

        command.SetAction(parseResult => {
            var json = parseResult.GetValue(jsonOption);
            var workflow = WorkflowFactory.Create();

            try {
                var projectDirectory = GlobalOptions.Resolve(parseResult);
                var addons = workflow.ListInstalled(projectDirectory);
                var dotnet = workflow.ListInstalledDotnet(projectDirectory);

                if (json) {
                    Console.WriteLine(DotquayJson.Write(
                        JsonEnvelope<ListJson>.Success(CommandName, JsonMapper.ToListJson(addons, dotnet))));
                    return 0;
                }

                if (addons.Count == 0 && dotnet.Count == 0) {
                    Console.WriteLine("Nothing locked yet. Run 'dotquay add <git-url>' or 'dotquay add <nuget-id>' to add one.");
                    return 0;
                }

                foreach (var addon in addons.OrderBy(a => a.Name, StringComparer.Ordinal)) {
                    var version = addon.Version ?? "(unversioned)";
                    var dirty = addon.PatchHash is not null ? " [patched]" : "";
                    Console.WriteLine($"{addon.Name}  {version}  {ShortSha(addon.Resolved)}{dirty}");
                }

                foreach (var dependency in dotnet.OrderBy(d => d.Name, StringComparer.Ordinal)) {
                    Console.WriteLine($"{dependency.Name}  {dependency.Resolved}  (NuGet)");
                }

                return 0;
            } catch (Exception ex) when (JsonOutput.IsExpectedFailure(ex)) {
                return JsonOutput.Fail(json, CommandName, ex);
            }
        });

        return command;
    }

    private static string ShortSha(string sha) => sha.Length > 7 ? sha[..7] : sha;
}
