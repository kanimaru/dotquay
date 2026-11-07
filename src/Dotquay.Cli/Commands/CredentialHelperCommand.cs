using System.CommandLine;
using Dotquay.Core.Auth;

namespace Dotquay.Cli.Commands;

/// <summary>
/// <c>dotquay credential-helper &lt;get|store|erase&gt;</c> — a git credential helper, not a user
/// command (SPEC.md §11.2). git invokes it on the git child processes dotquay itself launches,
/// having been registered by <see cref="Dotquay.Core.Git.GitEnvironment"/>.
/// </summary>
/// <remarks>
/// <para>Hidden, because it is a protocol endpoint. Its stdout belongs to git, which is also why it
/// must never learn <c>--json</c>: a JSON envelope on that stream is a parse failure inside git,
/// not a nicer error message.</para>
/// <para>dotquay is registered *after* whatever helpers the machine already has, so this only ever
/// runs when the user's own credential manager or SSH agent had no answer.</para>
/// </remarks>
internal static class CredentialHelperCommand {
    public static Command Build() {
        var operationArgument = new Argument<string>("operation") {
            Description = "get, store, or erase — git's credential helper protocol.",
        };

        var command = new Command(
            Dotquay.Core.Git.GitEnvironment.HelperCommandName,
            "Internal: answers git's credential helper protocol on stdin/stdout.")
        {
            operationArgument,

            // Deliberately no --json option. See the remarks above.
        };

        command.Hidden = true;

        command.SetAction(parseResult => {
            var operation = parseResult.GetValue(operationArgument);

            // 'store' and 'erase' succeed and do nothing. dotquay's store is written by
            // 'dotquay login', deliberately: a helper that quietly persisted whatever a prompt
            // produced would put secrets on disk without anyone asking for it. Exiting 0 rather
            // than erroring keeps git from treating a clone as failed over a helper that simply
            // declines.
            if (!string.Equals(operation, "get", StringComparison.Ordinal)) {
                return 0;
            }

            try {
                var request = GitCredentialProtocol.ParseRequest(Console.In.ReadToEnd());
                var url = GitCredentialProtocol.BuildUrl(request);
                if (url is null) {
                    return 0;
                }

                var lookup = CredentialFactory.CreateResolver().ForGitRepository(url);
                if (lookup.Credential is not null) {
                    Console.Out.Write(GitCredentialProtocol.FormatResponse(lookup.Credential));
                }

                // Empty output is how a helper says "I don't know this one" — git moves on to the
                // next helper, or reports a real authentication failure. Anything written to stderr
                // here would surface as noise in the middle of an otherwise clean clone.
                return 0;
            } catch (CredentialException) {
                // A malformed credentials file must not take a clone down with it. The user finds
                // out about it from 'dotquay login' or 'dotquay doctor', where there's room to
                // explain; here, the honest answer is "no credential".
                return 0;
            }
        });

        return command;
    }
}
