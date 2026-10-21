using System.CommandLine;
using Dotquay.Core.Auth;
using Dotquay.Core.IO;
using Dotquay.Core.Json;

namespace Dotquay.Cli.Commands;

/// <summary>
/// <c>dotquay login</c> — stores a username/token for a host in the shared credential file
/// (SPEC.md §11.2), the way <c>docker login</c> does.
/// </summary>
/// <remarks>
/// Host-keyed, not project-keyed, because that is how tokens actually work: GitLab publishes a NuGet
/// feed per project and issues one personal access token covering all of them, so per-feed login
/// would ask for the same secret a dozen times. Works with no project directory at all — the state it
/// writes is the user's, not a project's.
/// </remarks>
internal static class LoginCommand
{
    private const string CommandName = "login";

    public static Command Build()
    {
        var hostArgument = new Argument<string?>("host")
        {
            Description = "Host or feed URL to log in to, e.g. gitlab.example.com. "
                + "Omit to use the project's only configured NuGet source.",
            Arity = ArgumentArity.ZeroOrOne,
        };

        var usernameOption = new Option<string?>("--username", "-u")
        {
            Description = "Username for the host. Prompted for if omitted.",
        };

        var tokenOption = new Option<string?>("--token")
        {
            Description = "Token or password. Prefer --token-stdin: a token given here lands in your shell history.",
        };

        var tokenStdinOption = new Option<bool>("--token-stdin")
        {
            Description = "Read the token from stdin. The right choice for scripts and CI.",
        };

        var noVerifyOption = new Option<bool>("--no-verify")
        {
            Description = "Store the credential without checking it against the server first.",
        };

        var listOption = new Option<bool>("--list")
        {
            Description = "List stored logins (hosts and usernames — never tokens).",
        };

        var jsonOption = JsonOutput.CreateOption();

        var command = new Command(CommandName, "Store credentials for a private feed or git host.")
        {
            hostArgument, usernameOption, tokenOption, tokenStdinOption, noVerifyOption, listOption, jsonOption,
        };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var json = parseResult.GetValue(jsonOption);

            try
            {
                if (parseResult.GetValue(listOption))
                {
                    return ListLogins(json);
                }

                return await LoginAsync(
                    parseResult.GetValue(hostArgument),
                    parseResult.GetValue(usernameOption),
                    parseResult.GetValue(tokenOption),
                    parseResult.GetValue(tokenStdinOption),
                    parseResult.GetValue(noVerifyOption),
                    GlobalOptions.Resolve(parseResult),
                    json,
                    cancellationToken);
            }
            catch (CredentialException ex)
            {
                return JsonOutput.Fail(json, CommandName, ex);
            }
        });

        return command;
    }

    private static async Task<int> LoginAsync(
        string? hostArgument,
        string? username,
        string? token,
        bool tokenFromStdin,
        bool noVerify,
        string projectDirectory,
        bool json,
        CancellationToken cancellationToken)
    {
        var target = hostArgument ?? InferHost(projectDirectory);
        if (target is null)
        {
            return JsonOutput.Fail(
                json, CommandName, JsonErrorKind.Workflow,
                "Which host? Pass one, e.g. 'dotquay login gitlab.example.com'. "
                + "(Omitting it only works in a project with exactly one configured NuGet source.)");
        }

        if (!CredentialScope.TryParse(target, out var scope, out var scopeError))
        {
            return JsonOutput.Fail(json, CommandName, JsonErrorKind.Workflow, scopeError);
        }

        var messages = new List<string>();

        if (!scope.IsSecure)
        {
            return JsonOutput.Fail(
                json, CommandName, JsonErrorKind.Auth,
                $"{scope.Key} is plain HTTP. dotquay won't store a token it would have to send in cleartext. "
                + "Use https, or a loopback address for local testing.");
        }

        if (!TryReadCredential(username, token, tokenFromStdin, json, scope, out var credential, out var inputError))
        {
            return JsonOutput.Fail(json, CommandName, JsonErrorKind.Workflow, inputError);
        }

        var verified = false;
        if (!noVerify)
        {
            var (outcome, detail) = await VerifyAsync(target, scope, credential, projectDirectory, cancellationToken);
            switch (outcome)
            {
                case CredentialVerifier.Result.Rejected:
                    return JsonOutput.Fail(
                        json, CommandName, JsonErrorKind.Auth,
                        $"{scope.Key} rejected that credential ({detail}). Nothing was stored. "
                        + "Check the token and the username - GitLab wants the account's username, not its "
                        + "email address - or pass --no-verify to store it anyway.");

                case CredentialVerifier.Result.Inconclusive:
                    // Not fatal. "I couldn't reach the server" is a different statement from "your
                    // token is wrong", and refusing to store a credential because the network is
                    // down would make offline setup impossible.
                    messages.Add($"Couldn't verify against {scope.Key} ({detail}). Stored it anyway.");
                    break;

                default:
                    verified = true;
                    break;
            }
        }

        var store = CredentialFactory.CreateStore();
        store.Set(scope, credential);

        var storePath = CredentialFactory.StorePath();
        messages.Add($"Stored credentials for {scope.Key} in {storePath}.");
        messages.AddRange(ShadowWarnings(scope));

        if (json)
        {
            Console.WriteLine(DotquayJson.Write(JsonEnvelope<LoginJson>.Success(CommandName, new LoginJson
            {
                Credentials = [new CredentialEntryJson { Scope = scope.Key, Username = credential.Username }],
                StorePath = storePath,
                Verified = verified,
                Messages = messages,
            })));
            return 0;
        }

        ConsoleOutput.WriteLine(ConsoleColor.Green, messages[0]);
        foreach (var message in messages.Skip(1))
        {
            ConsoleOutput.WriteLine(ConsoleColor.Yellow, message);
        }

        return 0;
    }

    /// <summary>
    /// Warns when an environment variable will win over what was just stored.
    /// </summary>
    /// <remarks>
    /// Environment beats the file (SPEC.md §11.2) so CI can inject secrets without writing one. The
    /// cost is this exact confusion: a stale exported <c>DOTQUAY_NUGET_TOKEN</c> from an earlier
    /// session makes a successful <c>login</c> appear to have done nothing at all. Saying so here is
    /// cheaper than the debugging session it prevents.
    /// </remarks>
    private static IEnumerable<string> ShadowWarnings(CredentialScope scope)
    {
        var environment = new SystemEnvironmentReader();

        if (!string.IsNullOrEmpty(environment.GetVariable(CredentialResolver.GlobalUsernameVariable))
            && !string.IsNullOrEmpty(environment.GetVariable(CredentialResolver.GlobalTokenVariable)))
        {
            yield return
                $"Note: {CredentialResolver.GlobalUsernameVariable}/{CredentialResolver.GlobalTokenVariable} are set "
                + "and take precedence over stored credentials for NuGet feeds. Unset them to use what you just "
                + $"stored for {scope.Key}.";
        }
    }

    /// <summary>
    /// Collects the username and token from flags, stdin, or an interactive prompt.
    /// </summary>
    /// <remarks>
    /// With <c>--json</c> the prompt is never reached: a GUI caller (SPEC.md §18) captures stdout and
    /// has no way to answer a question, so a missing token is an error there rather than a hang —
    /// the same failure mode <c>GIT_TERMINAL_PROMPT=0</c> exists to prevent for git.
    /// </remarks>
    private static bool TryReadCredential(
        string? username, string? token, bool tokenFromStdin, bool json, CredentialScope scope,
        out Credential credential, out string error)
    {
        credential = new Credential(string.Empty, string.Empty);
        error = string.Empty;

        if (tokenFromStdin)
        {
            token = ReadSecretFromStdin();
        }

        if (json && (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(token)))
        {
            error = "--json can't prompt. Pass --username and either --token or --token-stdin.";
            return false;
        }

        if (string.IsNullOrEmpty(username))
        {
            Console.Write($"Username for {scope.Key}: ");
            username = Console.ReadLine()?.Trim();
        }

        if (string.IsNullOrEmpty(username))
        {
            error = "A username is required.";
            return false;
        }

        if (string.IsNullOrEmpty(token))
        {
            token = ReadSecret($"Token for {scope.Key}: ");
        }

        if (string.IsNullOrEmpty(token))
        {
            error = "A token is required.";
            return false;
        }

        credential = new Credential(username, token);
        return true;
    }

    /// <summary>
    /// Reads a piped secret from standard input, decoding it explicitly rather than through
    /// <see cref="Console.In"/>.
    /// </summary>
    /// <remarks>
    /// <para>This is a real bug, found by running the command rather than by testing it.
    /// <c>'token' | dotquay login --token-stdin</c> in Windows PowerShell 5.1 emits UTF-8 <em>with</em>
    /// a byte-order mark, and <see cref="Console.In"/> decodes the pipe using the console's OEM
    /// codepage — so those three BOM bytes arrive as three ordinary characters and were being stored
    /// as part of the secret. The stored credential then looks perfectly fine in <c>--list</c> (which
    /// prints no token, by design) and is rejected by every server, with nothing anywhere to explain
    /// why.</para>
    /// <para>Reading the raw stream with <c>detectEncodingFromByteOrderMarks</c> fixes both halves:
    /// the BOM is consumed as a BOM, and a non-ASCII token survives instead of being mangled by a
    /// codepage that can't represent it. Piping is the documented path for scripts and CI, so this is
    /// the common case rather than an edge one.</para>
    /// </remarks>
    private static string ReadSecretFromStdin()
    {
        using var stream = Console.OpenStandardInput();
        using var reader = new StreamReader(
            stream,
            new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            detectEncodingFromByteOrderMarks: true);

        // The explicit U+FEFF trim is belt-and-braces for a BOM that isn't at offset zero, which
        // StreamReader will not strip.
        return reader.ReadToEnd().Trim('﻿').Trim();
    }

    /// <summary>Reads a line without echoing it.</summary>
    /// <remarks>
    /// Falls back to a plain visible read when there's no console attached (piped input, a CI runner,
    /// an IDE's output pane), because <see cref="Console.ReadKey(bool)"/> throws there. Echoing is
    /// the lesser evil against crashing, and anyone piping input should be using
    /// <c>--token-stdin</c> anyway.
    /// </remarks>
    private static string ReadSecret(string prompt)
    {
        Console.Write(prompt);

        if (Console.IsInputRedirected)
        {
            // Same decoding path as --token-stdin, so a piped token behaves identically whether or
            // not the caller remembered the flag.
            return ReadSecretFromStdin();
        }

        var builder = new System.Text.StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);

            if (key.Key == ConsoleKey.Enter)
            {
                Console.WriteLine();
                return builder.ToString();
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (builder.Length > 0)
                {
                    builder.Length--;
                }

                continue;
            }

            // Control characters (arrow keys, function keys) arrive with KeyChar '\0' and must not
            // become part of the secret.
            if (!char.IsControl(key.KeyChar))
            {
                builder.Append(key.KeyChar);
            }
        }
    }

    /// <summary>
    /// Verifies against a real URL: the one given, or — for a bare host — the URLs the project's own
    /// manifest already uses on that host (its NuGet feeds, then its git remotes). Guessing a
    /// well-known path instead would produce confident failures against servers that simply don't
    /// have it, so with no project to borrow URLs from the check is skipped and says so.
    /// </summary>
    private static async Task<(CredentialVerifier.Result Outcome, string? Detail)> VerifyAsync(
        string target,
        CredentialScope scope,
        Credential credential,
        string projectDirectory,
        CancellationToken cancellationToken)
    {
        var verifier = new CredentialVerifier();

        if (target.Contains("://", StringComparison.Ordinal))
        {
            return await verifier.VerifyAsync(target, credential, cancellationToken);
        }

        var manifest = TryReadManifest(projectDirectory);
        IReadOnlyList<string> urls = manifest is null ? [] : CredentialProbeTargets.For(scope, manifest);

        if (urls.Count == 0)
        {
            return (
                CredentialVerifier.Result.Inconclusive,
                "no URL to check — pass the full feed URL, or run this inside a project whose dotquay.toml "
                + $"uses {scope.Key}");
        }

        return await verifier.VerifyAnyAsync(urls, credential, cancellationToken);
    }

    /// <summary>The project's only NuGet source host, when there is exactly one. Convenience only —
    /// a missing or unreadable manifest just means "no inference", never an error, because
    /// <c>login</c> is a user-level command that must work outside a project.</summary>
    private static string? InferHost(string projectDirectory)
    {
        var sources = TryReadManifest(projectDirectory)?.NuGetSources
            .Where(source => !source.IsPublicDefault)
            .ToList();

        return sources is { Count: 1 } ? sources[0].Url : null;
    }

    /// <summary>The project's manifest, or null when there is none or it doesn't parse — same
    /// reasoning as <see cref="InferHost"/>: <c>login</c> never fails because of the project it
    /// happens to be run from.</summary>
    private static Core.Manifest.ProjectManifest? TryReadManifest(string projectDirectory)
    {
        try
        {
            var store = new Core.Workflows.ProjectFileStore(new SystemFileSystem());
            return store.HasManifest(projectDirectory) ? store.ReadManifestOrThrow(projectDirectory).Manifest : null;
        }
        catch (Exception ex) when (ex is Core.Workflows.WorkflowException or Core.Manifest.ManifestParseException)
        {
            return null;
        }
    }

    private static int ListLogins(bool json)
    {
        var entries = CredentialFactory.CreateStore().List();
        var storePath = CredentialFactory.StorePath();

        if (json)
        {
            Console.WriteLine(DotquayJson.Write(JsonEnvelope<LoginJson>.Success(CommandName, new LoginJson
            {
                Credentials = entries
                    .Select(entry => new CredentialEntryJson { Scope = entry.Scope, Username = entry.Username })
                    .ToList(),
                StorePath = storePath,
            })));
            return 0;
        }

        if (entries.Count == 0)
        {
            Console.WriteLine("No stored logins. Add one with 'dotquay login <host>'.");
            return 0;
        }

        foreach (var entry in entries)
        {
            // Username only. A token — even a few characters of one — has no reason to be on a
            // screen that gets screenshotted into bug reports.
            Console.WriteLine($"{entry.Scope}  ({entry.Username})");
        }

        Console.WriteLine();
        Console.WriteLine($"Stored in {storePath}");
        return 0;
    }
}
