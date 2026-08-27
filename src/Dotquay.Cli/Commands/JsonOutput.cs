using System.CommandLine;
using Dotquay.Core.Git;
using Dotquay.Core.Json;
using Dotquay.Core.Resolve;
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
            ConsoleOutput.WriteLine(ConsoleColor.Red, message);
        }

        return 1;
    }

    /// <summary>Classifies an exception into a stable <see cref="JsonErrorKind"/>. Kept here rather
    /// than at each catch site so every command reports the same category for the same failure.</summary>
    public static int Fail(bool json, string command, Exception exception) => exception switch
    {
        WorkflowException when exception.Message.Contains("Run 'dotquay init' first", StringComparison.Ordinal) =>
            Fail(json, command, JsonErrorKind.NoManifest, exception.Message),
        WorkflowException { InnerException: AddonResolutionException } =>
            Fail(json, command, JsonErrorKind.Conflict, exception.Message),
        WorkflowException { InnerException: GitCommandException } =>
            Fail(json, command, JsonErrorKind.Git, exception.Message),
        WorkflowException => Fail(json, command, JsonErrorKind.Workflow, exception.Message),
        AddonResolutionException => Fail(json, command, JsonErrorKind.Conflict, exception.Message),
        GitCommandException => Fail(json, command, JsonErrorKind.Git, exception.Message),
        _ => Fail(json, command, JsonErrorKind.Unexpected, exception.Message),
    };

    /// <summary>Success path for mutating commands, which all share
    /// <see cref="OperationJson"/>.</summary>
    public static void WriteOperation(string command, OperationJson payload, IReadOnlyList<string>? warnings = null) =>
        Console.WriteLine(DotquayJson.Write(JsonEnvelope<OperationJson>.Success(command, payload, warnings)));
}
