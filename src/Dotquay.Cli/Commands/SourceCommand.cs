using System.CommandLine;
using Dotquay.Core.Json;
using Dotquay.Core.Manifest;
using Dotquay.Core.Workflows;

namespace Dotquay.Cli.Commands;

/// <summary>
/// <c>dotquay source add|list|remove</c> (v0.14) — declares the NuGet feeds a project searches
/// (SPEC.md §7.1, §11.1).
/// </summary>
/// <remarks>
/// <para>The gap this closes: through v0.13 <c>[nuget.sources.*]</c> could only be written by
/// <c>init --nuget-source</c> (once, at project creation) or by adopting a feed an addon declared,
/// so the second feed in any project arrived via a text editor. <c>override</c> and <c>link</c>
/// both exist for exactly that reason in their own sections; this is the same argument applied to
/// the one section that never got it.</para>
/// <para>A command group rather than a flag on <c>add</c>: a feed outlives every package in it, and
/// is read by <c>outdated</c>, <c>list</c>, and <c>dotnet restore</c> as well. It is also why this
/// must not be an interactive prompt — the editor plugin drives dotquay through captured stdout and
/// cannot answer one (SPEC.md §6.3/§18), so anything prompt-only would be invisible to the add
/// form.</para>
/// </remarks>
internal static class SourceCommand {
    private const string CommandName = "source";
    private const string ListCommandName = "source-list";

    public static Command Build() {
        var command = new Command(
            CommandName,
            "Declare, list, or remove the NuGet feeds this project searches, in order.")
        {
            BuildAdd(),
            BuildList(),
            BuildRemove(),
        };

        var jsonOption = JsonOutput.CreateOption();
        command.Options.Add(jsonOption);

        // A bare `dotquay source` lists, matching `dotquay override` and `dotquay link` with no
        // arguments. The most common thing anyone wants from this command is to see what is already
        // declared, and making that the zero-argument behaviour beats printing help.
        command.SetAction(parseResult => RunList(parseResult, jsonOption));

        return command;
    }

    private static Command BuildAdd() {
        var nameArgument = new Argument<string>("name") {
            Description = "Name for the feed, e.g. \"core\". This is what a package's source = "
                + "\"...\" pin refers to, so pick something stable — it belongs to this project, "
                + "not to whoever publishes the feed.",
        };

        var urlArgument = new Argument<string>("url") {
            Description = "The feed's V3 service-index URL. For GitLab that is "
                + "/api/v4/projects/<numeric project id>/packages/nuget/index.json — the numeric "
                + "ID, not the path.",
        };

        var noVerifyOption = new Option<bool>("--no-verify") {
            Description = "Write the feed without reaching it first. Skips the check that the URL "
                + "really is a V3 service index and that your credentials are accepted.",
        };

        var jsonOption = JsonOutput.CreateOption();

        var command = new Command("add",
            "Declare a NuGet feed, appended after the feeds already declared.")
        {
            nameArgument, urlArgument, noVerifyOption, jsonOption,
        };

        command.SetAction(async (parseResult, cancellationToken) => {
            var json = parseResult.GetValue(jsonOption);
            var name = parseResult.GetValue(nameArgument)!;

            try {
                var result = await WorkflowFactory.CreateSources().AddAsync(
                    GlobalOptions.Resolve(parseResult),
                    name,
                    parseResult.GetValue(urlArgument)!,
                    verify: !parseResult.GetValue(noVerifyOption),
                    cancellationToken);

                return Report(json, result);
            } catch (Exception ex) when (ex is not OperationCanceledException) {
                return JsonOutput.Fail(json, CommandName, ex);
            }
        });

        return command;
    }

    private static Command BuildList() {
        var jsonOption = JsonOutput.CreateOption();
        var command = new Command("list",
            "Show the declared feeds in the order they are searched.") { jsonOption };

        command.SetAction(parseResult => RunList(parseResult, jsonOption));

        return command;
    }

    private static Command BuildRemove() {
        var nameArgument = new Argument<string>("name") { Description = "The feed to remove." };

        var forceOption = new Option<bool>("--force") {
            Description = "Also clear every source = \"...\" pin and lockfile record naming this "
                + "feed. Each one is reported, because unpinning a package changes which feed it "
                + "resolves from next time.",
        };

        var jsonOption = JsonOutput.CreateOption();

        var command = new Command("remove", "Remove a declared NuGet feed.") { nameArgument,
            forceOption, jsonOption };

        command.SetAction(parseResult => {
            var json = parseResult.GetValue(jsonOption);

            try {
                var result = WorkflowFactory.CreateSources().Remove(
                    GlobalOptions.Resolve(parseResult),
                    parseResult.GetValue(nameArgument)!,
                    parseResult.GetValue(forceOption));

                return Report(json, result);
            } catch (Exception ex) when (ex is not OperationCanceledException) {
                return JsonOutput.Fail(json, CommandName, ex);
            }
        });

        return command;
    }

    private static int RunList(ParseResult parseResult, Option<bool> jsonOption) {
        var json = parseResult.GetValue(jsonOption);

        try {
            var sources = WorkflowFactory.CreateSources().List(GlobalOptions.Resolve(parseResult));

            // Built here rather than in the workflow because resolving a credential is a CLI-side
            // concern (the store's location comes from the environment), and Dotquay.Core has no
            // business deciding what a UI is told about one.
            var credentials = CredentialFactory.CreateResolver();

            if (json) {
                JsonOutput.WriteSourceList(ListCommandName, new SourceListJson {
                    Sources = sources.Select(source => new SourceJson {
                        Name = source.Name,
                        Url = source.Url,
                        Packages = source.Packages,
                        DeclaredByAddon = source.DeclaredByAddon,
                        HasCredential = HasCredential(credentials, source),
                    }).ToList(),
                });

                return 0;
            }

            if (sources.Count == 0) {
                Console.WriteLine(
                    "No NuGet feeds declared — packages resolve from nuget.org. Add one with "
                    + "'dotquay source add <name> <url>'.");
                return 0;
            }

            foreach (var source in sources) {
                var notes = new List<string>();

                if (HasCredential(credentials, source)) {
                    notes.Add("logged in");
                }

                if (source.DeclaredByAddon is not null) {
                    notes.Add($"from addon '{source.DeclaredByAddon}'");
                }

                if (source.Packages.Count > 0) {
                    notes.Add($"owns {string.Join(", ", source.Packages)}");
                }

                var suffix = notes.Count > 0 ? $"  ({string.Join("; ", notes)})" : "";
                Console.WriteLine($"{source.Name}  {source.Url}{suffix}");
            }

            // Stated rather than left to be inferred: the implicit public feed is why an undeclared
            // package still resolves, and it is always last unless the project declared it itself.
            if (!sources.Any(source => source.IsPublicDefault)) {
                Console.WriteLine($"{NuGetSourceSpec.PublicName}  {NuGetSourceSpec.PublicUrl}  "
                    + $"(implicit, always last)");
            }

            return 0;
        } catch (Exception ex) {
            return JsonOutput.Fail(json, ListCommandName, ex);
        }
    }

    /// <summary>Whether a credential resolves, never which one. The token is not printed anywhere
    /// in dotquay, and the username is already more than a caller needs to render "logged
    /// in".</summary>
    private static bool HasCredential(Core.Auth.CredentialResolver credentials,
        NuGetSourceSpec source) =>
        !source.IsPublicDefault && credentials.ForNuGetSource(source.Name, source.Url)
        .Credential is not null;

    private static int Report(bool json, SourceResult result) {
        var messages = new List<string> { Describe(result) };
        messages.AddRange(result.DroppedPins.Select(pin => $"  cleared {pin}"));

        if (json) {
            JsonOutput.WriteOperation(CommandName, new OperationJson {
                Changes =
                [
                    new ChangeJson
                    {
                        Name = result.Name,
                        Kind = JsonMapper.ChangeKind.Dotnet,
                        Action = result.Change switch
                        {
                            SourceChange.Added => JsonMapper.ChangeAction.Added,
                            SourceChange.Removed => JsonMapper.ChangeAction.Removed,
                            _ => JsonMapper.ChangeAction.Unchanged,
                        },
                        To = result.Source?.Url,
                    },
                ],
                Messages = messages,
            });

            return 0;
        }

        foreach (var message in messages) {
            ConsoleOutput.WriteLine(ConsoleColor.Green, message);
        }

        return 0;
    }

    private static string Describe(SourceResult result) => result.Change switch {
        SourceChange.Added =>
            $"Added [nuget.sources.{result.Name}] -> {result.Source!.Url} (searched last)",
        SourceChange.Removed => $"Removed [nuget.sources.{result.Name}]",
        _ when result.Source is not null =>
            $"[nuget.sources.{result.Name}] already points there — nothing to do.",
        _ => $"[nuget.sources.{result.Name}] isn't declared — nothing to do.",
    };
}
