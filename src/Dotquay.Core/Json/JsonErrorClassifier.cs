using Dotquay.Core.Auth;
using Dotquay.Core.Diagnostics;
using Dotquay.Core.Dotnet;
using Dotquay.Core.Git;
using Dotquay.Core.Local;
using Dotquay.Core.Lockfile;
using Dotquay.Core.Manifest;
using Dotquay.Core.Resolve;
using Dotquay.Core.Workflows;

namespace Dotquay.Core.Json;

/// <summary>
/// Classifies an exception into a stable <see cref="JsonErrorKind"/> and the message a consumer
/// shows for it.
/// </summary>
/// <remarks>
/// Extracted from <c>Dotquay.Cli.Commands.JsonOutput</c> (v0.18) when the MCP server became a second
/// caller that needs the same classification without depending on <c>Dotquay.Cli</c> — a tool's
/// structured failure uses the identical <see cref="JsonErrorKind"/> values the CLI's <c>--json</c>
/// failures already use, so an agent and a shell script recognize the same shape. Kept in one place so
/// every command, and now every MCP tool, reports the same category for the same failure.
/// </remarks>
public static class JsonErrorClassifier
{
    /// <summary>
    /// The category and the message to show, with credentials masked.
    /// </summary>
    /// <remarks>
    /// Masked here as well as in <see cref="JsonError"/> because the plain (non-<c>--json</c>) CLI
    /// output prints this message directly, without ever building a <see cref="JsonError"/>.
    /// </remarks>
    public static (string Kind, string Message) Classify(Exception exception)
    {
        var (kind, message) = ClassifyUnredacted(exception);
        return (kind, SecretRedactor.Redact(message));
    }

    /// <summary>
    /// Whether the exception, or the one a <see cref="WorkflowException"/> wraps, says one of the
    /// project's own files can't be read.
    /// </summary>
    /// <remarks>
    /// Public because the CLI's last-resort handler uses it to decide which escaped exceptions get a
    /// clean one-line message instead of a stack trace without <c>--json</c>: these are the user's
    /// own files, and what they need is where the mistake is, not where dotquay noticed it.
    /// </remarks>
    public static bool IsInvalidProjectFile(Exception exception) =>
        IsParseFailure(exception) || exception is WorkflowException { InnerException: { } inner } && IsParseFailure(inner);

    private static bool IsParseFailure(Exception exception) =>
        exception is LockfileParseException or ManifestParseException or LocalLinksParseException;

    private static (string Kind, string Message) ClassifyUnredacted(Exception exception) => exception switch
    {
        WorkflowException when exception.Message.Contains("Run 'dotquay init' first", StringComparison.Ordinal) =>
            (JsonErrorKind.NoManifest, exception.Message),

        // Before every other WorkflowException case: the workflow wraps an unreadable
        // dotquay.local.toml in one, and reporting that as a plain workflow failure would hide that
        // the fix is editing a file, not retrying. The message already names the file and, for a
        // syntax error, the parser's (line,column).
        _ when IsInvalidProjectFile(exception) => (JsonErrorKind.InvalidProjectFile, exception.Message),

        WorkflowException { InnerException: AddonResolutionException } =>
            (JsonErrorKind.Conflict, exception.Message),

        // Auth is matched before the plain git/NuGet cases, and the ordering is the whole point: a
        // credential failure is a git failure, and reporting it as one would leave a UI unable to
        // tell "log in" from "the network is down" (SPEC.md §17).
        WorkflowException { InnerException: GitCommandException { IsAuthenticationFailure: true } } =>
            (JsonErrorKind.Auth, exception.Message),
        WorkflowException { InnerException: NuGetAuthenticationException } =>
            (JsonErrorKind.Auth, exception.Message),
        WorkflowException { InnerException: CredentialException } =>
            (JsonErrorKind.Auth, exception.Message),

        // A feed that is misconfigured, unreachable, or invisible to the current credential. Not
        // "auth" — the credential may be perfectly fine and the URL simply wrong — but an expected,
        // explained failure rather than a crash.
        WorkflowException { InnerException: NuGetSourceException } =>
            (JsonErrorKind.Workflow, exception.Message),

        WorkflowException { InnerException: GitCommandException } =>
            (JsonErrorKind.Git, exception.Message),
        WorkflowException => (JsonErrorKind.Workflow, exception.Message),
        AddonResolutionException => (JsonErrorKind.Conflict, exception.Message),
        GitCommandException { IsAuthenticationFailure: true } => (JsonErrorKind.Auth, exception.Message),
        GitCommandException => (JsonErrorKind.Git, exception.Message),
        NuGetAuthenticationException => (JsonErrorKind.Auth, exception.Message),
        CredentialException => (JsonErrorKind.Auth, exception.Message),
        NuGetSourceException => (JsonErrorKind.Workflow, exception.Message),

        // Backstop. Anything HTTP that still reaches here is a feed problem, and the one thing it
        // must not do is escape as an unhandled exception: that prints a stack trace and, under
        // --json, emits no envelope at all, which breaks the plugin's contract (SPEC.md §17). The
        // same reasoning applies to an MCP tool result.
        System.Net.Http.HttpRequestException =>
            (JsonErrorKind.Workflow, $"A NuGet source could not be reached: {exception.Message}"),

        _ => (JsonErrorKind.Unexpected, exception.Message),
    };
}
