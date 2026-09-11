using System.CommandLine;
using Dotquay.Core.Auth;
using Dotquay.Core.Dotnet;
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

        // Auth is matched before the plain git/NuGet cases, and the ordering is the whole point: a
        // credential failure is a git failure, and reporting it as one would leave a UI unable to
        // tell "log in" from "the network is down" (SPEC.md §17).
        WorkflowException { InnerException: GitCommandException { IsAuthenticationFailure: true } } =>
            Fail(json, command, JsonErrorKind.Auth, exception.Message),
        WorkflowException { InnerException: NuGetAuthenticationException } =>
            Fail(json, command, JsonErrorKind.Auth, exception.Message),
        WorkflowException { InnerException: CredentialException } =>
            Fail(json, command, JsonErrorKind.Auth, exception.Message),

        // A feed that is misconfigured, unreachable, or invisible to the current credential. Not
        // "auth" — the credential may be perfectly fine and the URL simply wrong — but an expected,
        // explained failure rather than a crash.
        WorkflowException { InnerException: NuGetSourceException } =>
            Fail(json, command, JsonErrorKind.Workflow, exception.Message),

        WorkflowException { InnerException: GitCommandException } =>
            Fail(json, command, JsonErrorKind.Git, exception.Message),
        WorkflowException => Fail(json, command, JsonErrorKind.Workflow, exception.Message),
        AddonResolutionException => Fail(json, command, JsonErrorKind.Conflict, exception.Message),
        GitCommandException { IsAuthenticationFailure: true } =>
            Fail(json, command, JsonErrorKind.Auth, exception.Message),
        GitCommandException => Fail(json, command, JsonErrorKind.Git, exception.Message),
        NuGetAuthenticationException => Fail(json, command, JsonErrorKind.Auth, exception.Message),
        CredentialException => Fail(json, command, JsonErrorKind.Auth, exception.Message),
        NuGetSourceException => Fail(json, command, JsonErrorKind.Workflow, exception.Message),

        // Backstop. Anything HTTP that still reaches here is a feed problem, and the one thing it
        // must not do is escape as an unhandled exception: that prints a stack trace and, under
        // --json, emits no envelope at all, which breaks the plugin's contract (SPEC.md §17).
        System.Net.Http.HttpRequestException =>
            Fail(json, command, JsonErrorKind.Workflow, $"A NuGet source could not be reached: {exception.Message}"),

        _ => Fail(json, command, JsonErrorKind.Unexpected, exception.Message),
    };

    /// <summary>Success path for mutating commands, which all share
    /// <see cref="OperationJson"/>.</summary>
    public static void WriteOperation(string command, OperationJson payload, IReadOnlyList<string>? warnings = null) =>
        Console.WriteLine(DotquayJson.Write(JsonEnvelope<OperationJson>.Success(command, payload, warnings)));

    /// <summary>Success path for <c>source list</c> (v0.14), whose payload has no before/after and so
    /// does not fit <see cref="OperationJson"/>'s change shape.</summary>
    public static void WriteSourceList(string command, SourceListJson payload) =>
        Console.WriteLine(DotquayJson.Write(JsonEnvelope<SourceListJson>.Success(command, payload)));
}
