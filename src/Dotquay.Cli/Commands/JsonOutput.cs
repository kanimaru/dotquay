using System.CommandLine;
using Dotquay.Core.Diagnostics;
using Dotquay.Core.Json;
using Dotquay.Core.Workflows;

namespace Dotquay.Cli.Commands;

/// <summary>
/// The <c>--json</c> switch and the one place a command turns a failure into a machine-readable
/// envelope (SPEC.md §17). Every command builds its option from here so the flag's name, description,
/// and semantics can't drift between commands — a consumer that has to remember which commands spell
/// it differently is a consumer that will get it wrong.
/// </summary>
internal static class JsonOutput
{
    /// <summary>When set, the command writes exactly one JSON document to stdout and nothing else:
    /// no colored status lines, no hints, no progress. Anything a human would have been told goes
    /// into the envelope's <c>warnings</c> or <c>data.messages</c> instead of being lost.</summary>
    public static Option<bool> CreateOption() => new("--json")
    {
        Description = "Emit a single machine-readable JSON document on stdout instead of human output. "
            + "Used by the Godot editor plugin (SPEC.md §17).",
    };

    /// <summary>
    /// The failures a command catches and reports under its own envelope name: a
    /// <see cref="WorkflowException"/>, or one of the project's own files that can't be read.
    /// </summary>
    /// <remarks>
    /// A malformed <c>dotquay.lock</c> or <c>dotquay.toml</c> throws its parser's exception straight
    /// out of the workflow, and every command caught only <see cref="WorkflowException"/>, so under
    /// <c>--json</c> it used to escape as a stack trace with nothing on stdout. Anything else that
    /// escapes still reaches <see cref="UnhandledFailure"/>, which keeps the envelope promise too, but
    /// can only guess the command's envelope name from its path.
    /// </remarks>
    public static bool IsExpectedFailure(Exception exception) =>
        exception is WorkflowException || JsonErrorClassifier.IsInvalidProjectFile(exception);

    /// <summary>
    /// Emits a failure envelope (or the plain red line, without <c>--json</c>) and returns exit code
    /// 1.
    /// </summary>
    /// <remarks>
    /// Deliberately serializes through <see cref="OperationJson"/> regardless of which payload the
    /// command would have produced on success: a failure envelope always carries <c>data: null</c>,
    /// so the generic argument is unobservable in the output, and pinning it to one type avoids a
    /// generic dispatch that NativeAOT's source-generated serializer would need a separate registered
    /// closed generic for (see <see cref="DotquayJsonContext"/>). The exit code stays meaningful
    /// either way, so a caller that ignores JSON entirely still behaves correctly.
    /// </remarks>
    public static int Fail(bool json, string command, string kind, string message)
    {
        if (json)
        {
            Console.WriteLine(DotquayJson.Write(JsonEnvelope<OperationJson>.Failure(command, new JsonError(kind, message))));
        }
        else
        {
            ConsoleOutput.WriteRedacted(ConsoleColor.Red, message);
        }

        return 1;
    }

    /// <summary>Classifies the exception via <see cref="JsonErrorClassifier"/> and reports it. Kept as
    /// a thin wrapper here — rather than inlining the switch — because the MCP server (Phase 1) needs
    /// the identical classification without depending on <c>Dotquay.Cli</c>; <see cref="JsonErrorClassifier"/>
    /// lives in <c>Dotquay.Core</c> so both callers get the same category for the same failure.</summary>
    public static int Fail(bool json, string command, Exception exception)
    {
        var (kind, message) = JsonErrorClassifier.Classify(exception);

        // The user-facing line is one sentence by design; the log gets the whole chain - type,
        // inner exceptions, stack - because "which call threw" is the first thing a debugger needs.
        DiagnosticLog.Write(
            DiagnosticLevel.Verbose,
            "error",
            $"{command} failed ({kind}):\n{SecretRedactor.Redact(exception.ToString())}");

        return Fail(json, command, kind, message);
    }

    /// <summary>Success path for mutating commands, which all share
    /// <see cref="OperationJson"/>.</summary>
    public static void WriteOperation(string command, OperationJson payload, IReadOnlyList<string>? warnings = null) =>
        Console.WriteLine(DotquayJson.Write(JsonEnvelope<OperationJson>.Success(command, payload, warnings)));

    /// <summary>Success path for <c>source list</c> (v0.14), whose payload has no before/after and so
    /// does not fit <see cref="OperationJson"/>'s change shape.</summary>
    public static void WriteSourceList(string command, SourceListJson payload) =>
        Console.WriteLine(DotquayJson.Write(JsonEnvelope<SourceListJson>.Success(command, payload)));
}
