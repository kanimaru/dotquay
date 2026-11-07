using System.CommandLine;
using System.CommandLine.Parsing;
using Dotquay.Core.Diagnostics;
using Dotquay.Core.Json;

namespace Dotquay.Cli.Commands;

/// <summary>
/// The last resort for an exception no command caught: <c>Program</c> runs every command with
/// System.CommandLine's default exception handler off and hands anything that escapes to
/// <see cref="Report"/>.
/// </summary>
/// <remarks>
/// The default handler printed <c>Unhandled exception:</c> and a stack trace on stderr and nothing on
/// stdout, which under <c>--json</c> breaks the one promise the flag makes (SPEC.md §17): a caller
/// always gets exactly one envelope. Found with a malformed <c>dotquay.lock</c>, which made
/// <c>status --json</c> and <c>doctor --json</c> crash. Commands still catch their expected failures
/// themselves (<see cref="JsonOutput.IsExpectedFailure"/>), because only they know their envelope's
/// command name; here it can only be guessed from the command's path.
/// </remarks>
internal static class UnhandledFailure
{
    private const string JsonOptionName = "--json";

    /// <summary>Reports the exception and returns the exit code: 1, as the default handler did.</summary>
    public static int Report(ParseResult parseResult, Exception exception)
    {
        // Not an error to report: the default handler printed nothing for a cancellation either.
        if (exception is OperationCanceledException)
        {
            return 1;
        }

        var json = WantsJson(parseResult);

        if (json || JsonOutput.IsUnreadableFile(exception))
        {
            return JsonOutput.Fail(json, EnvelopeCommandName(parseResult), exception);
        }

        // Anything else is a bug, and its stack trace is the most useful thing to show: the same
        // output the default handler gave, with credentials masked.
        var previous = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.Red;
        Console.Error.WriteLine($"Unhandled exception: {SecretRedactor.Redact(exception.ToString())}");
        Console.ForegroundColor = previous;
        return 1;
    }

    /// <summary>Whether the command that ran has a <c>--json</c> option and it was given.</summary>
    private static bool WantsJson(ParseResult parseResult) =>
        parseResult.CommandResult.Command.Options
            .OfType<Option<bool>>()
            .FirstOrDefault(option => option.Name == JsonOptionName) is { } jsonOption
        && parseResult.GetValue(jsonOption);

    /// <summary>The subcommand path joined by <c>-</c>, the convention the commands' own names follow
    /// (<c>status</c>, <c>source-list</c>, <c>self-check</c>).</summary>
    internal static string EnvelopeCommandName(ParseResult parseResult)
    {
        var names = new List<string>();

        // The root command's own name is the executable's, never part of an envelope name.
        for (var current = parseResult.CommandResult; current.Parent is CommandResult parent; current = parent)
        {
            names.Add(current.Command.Name);
        }

        names.Reverse();
        return names.Count == 0 ? parseResult.CommandResult.Command.Name : string.Join('-', names);
    }
}
