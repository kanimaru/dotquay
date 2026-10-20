using Dotquay.Core.Diagnostics;

namespace Dotquay.Core.Json;

/// <summary>
/// The single shape every <c>--json</c> response takes (SPEC.md §17). One envelope for success and
/// failure alike means a consumer writes one parse path and one error check, instead of guessing
/// whether stdout holds a result or a message — which matters most for the Godot editor plugin, whose
/// only channel to dotquay is a captured stdout buffer (SPEC.md §18).
/// </summary>
/// <param name="Schema">Bumped only on a breaking change to the payload shape. The plugin refuses a
/// schema it doesn't know rather than mis-parsing it.</param>
/// <param name="Tool">Always <c>"dotquay"</c> — lets a consumer reject output it captured from the
/// wrong executable (a mis-configured CLI path is the single most likely setup mistake).</param>
/// <param name="Version">The CLI's own version, for diagnostics and "your dotquay is older than this
/// plugin" messages.</param>
/// <param name="Command">The command that produced this, e.g. <c>"graph"</c>.</param>
/// <param name="Ok">False exactly when <paramref name="Error"/> is non-null.</param>
/// <param name="Data">Null on failure.</param>
/// <param name="Warnings">Non-fatal notes (e.g. an untagged repo pinned by SHA) that would otherwise
/// have gone to the console as colored text and been lost.</param>
public sealed record JsonEnvelope<T>(
    int Schema,
    string Tool,
    string Version,
    string Command,
    bool Ok,
    T? Data,
    JsonError? Error,
    IReadOnlyList<string> Warnings)
    where T : class
{
    /// <summary>The current payload schema version. See <see cref="Schema"/>.</summary>
    public const int CurrentSchema = 1;

    public const string ToolName = "dotquay";

    public static JsonEnvelope<T> Success(string command, T data, IReadOnlyList<string>? warnings = null) =>
        new(CurrentSchema, ToolName, DotquayVersion.Current, command, Ok: true, data, Error: null, Redact(warnings));

    public static JsonEnvelope<T> Failure(string command, JsonError error, IReadOnlyList<string>? warnings = null) =>
        new(CurrentSchema, ToolName, DotquayVersion.Current, command, Ok: false, Data: null, error, Redact(warnings));

    /// <summary>
    /// Warnings are prose built from exception messages and git output, both of which can quote a
    /// remote URL with its credential in it. Masked here, at the one place every envelope is built,
    /// so no command has to remember to (see <see cref="SecretRedactor"/>).
    /// </summary>
    /// <remarks>
    /// Only the free-text fields are masked: <see cref="Error"/> and <see cref="Warnings"/>.
    /// <see cref="Data"/> carries structured facts the caller owns - the git URL an addon was
    /// declared with, for instance - and rewriting those would make the payload lie about the
    /// project it describes.
    /// </remarks>
    private static IReadOnlyList<string> Redact(IReadOnlyList<string>? warnings) =>
        warnings is null or { Count: 0 } ? [] : warnings.Select(SecretRedactor.Redact).ToList();
}

/// <summary><paramref name="Kind"/> is a stable machine-readable category, not free text — a UI can
/// react to <c>"no_manifest"</c> by offering an "Initialise project" button, which it can't do with a
/// message string.</summary>
/// <remarks>
/// <see cref="Message"/> is redacted on construction, whoever builds the error: the CLI's
/// <c>--json</c> failures, every MCP tool failure and write-guard refusal, and anything added later.
/// Error messages routinely quote the git URL that failed, and a URL can carry a token
/// (<c>https://oauth2:glpat-...@host</c>) that would otherwise be printed straight back to the
/// terminal, the editor plugin, or an agent's context (docs/DESIGN-v0.24-VERBOSE.md).
/// </remarks>
public sealed record JsonError(string Kind, string Message)
{
    private readonly string _message = SecretRedactor.Redact(Message);

    // An explicit init accessor, not an initializer alone: `with { Message = ... }` assigns through
    // init and would otherwise skip the redaction the constructor applies.
    public string Message
    {
        get => _message;
        init => _message = SecretRedactor.Redact(value);
    }
}

/// <summary>Error categories carried by <see cref="JsonError.Kind"/>. Strings rather than an enum
/// because this is a wire contract that outlives any one C# type.</summary>
public static class JsonErrorKind
{
    /// <summary>A <see cref="Workflows.WorkflowException"/> — an expected, explained failure.</summary>
    public const string Workflow = "workflow";

    /// <summary>No <c>dotquay.toml</c> in the project directory.</summary>
    public const string NoManifest = "no_manifest";

    /// <summary>The dependency graph has unresolvable constraints (SPEC.md §8). The graph payload
    /// carries the details; this is the flag that says "don't treat the graph as installable".</summary>
    public const string Conflict = "conflict";

    /// <summary>A git invocation failed (network, missing binary). Credential failures report
    /// <see cref="Auth"/> instead.</summary>
    public const string Git = "git";

    /// <summary>A remote refused dotquay's credentials, or wanted some and had none (SPEC.md §11.2).
    /// Distinct from <see cref="Git"/> so a UI can offer a login prompt instead of showing a
    /// network error, and so a script can tell "fix your token" from "the server is down".</summary>
    public const string Auth = "auth";

    /// <summary>Anything unanticipated. A consumer should show the message and stop, not retry.</summary>
    public const string Unexpected = "unexpected";
}
