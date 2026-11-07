using System.CommandLine;
using Dotquay.Core.Auth;
using Dotquay.Core.Json;

namespace Dotquay.Cli.Commands;

/// <summary><c>dotquay logout &lt;host&gt;</c> — removes a stored credential (SPEC.md §11.2).</summary>
/// <remarks>
/// Matches the exact scope given and nothing else. Prefix matching would mean
/// <c>dotquay logout gitlab.example.com</c> silently deleted a narrower, differently-scoped token
/// stored for one project on that host — removing more credentials than were named is not a thing a
/// <c>logout</c> should ever do, and the recovery is "find the token again".
/// </remarks>
internal static class LogoutCommand {
    private const string CommandName = "logout";

    public static Command Build() {
        var hostArgument = new Argument<string>("host") {
            Description = "The host or scope to forget, exactly as 'dotquay login --list' shows it.",
        };

        var jsonOption = JsonOutput.CreateOption();

        var command = new Command(CommandName, "Remove stored credentials for a host.")
        {
            hostArgument, jsonOption,
        };

        command.SetAction(parseResult => {
            var json = parseResult.GetValue(jsonOption);
            var host = parseResult.GetValue(hostArgument)!;

            try {
                if (!CredentialScope.TryParse(host, out var scope, out var error)) {
                    return JsonOutput.Fail(json, CommandName, JsonErrorKind.Workflow, error);
                }

                var removed = CredentialFactory.CreateStore().Remove(scope);
                var message = removed
                    ? $"Removed stored credentials for {scope.Key}."
                    : $"No stored credentials for {scope.Key} — nothing to do.";

                if (json) {
                    Console.WriteLine(DotquayJson.Write(JsonEnvelope<LoginJson>.Success(CommandName, new LoginJson {
                        StorePath = CredentialFactory.StorePath(),
                        Messages = [message],
                    })));
                    return 0;
                }

                ConsoleOutput.WriteLine(removed ? ConsoleColor.Green : ConsoleColor.Yellow, message);
                return 0;
            } catch (CredentialException ex) {
                return JsonOutput.Fail(json, CommandName, ex);
            }
        });

        return command;
    }
}
