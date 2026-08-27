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
        new(CurrentSchema, ToolName, DotquayVersion.Current, command, Ok: true, data, Error: null, warnings ?? []);

    public static JsonEnvelope<T> Failure(string command, JsonError error, IReadOnlyList<string>? warnings = null) =>
        new(CurrentSchema, ToolName, DotquayVersion.Current, command, Ok: false, Data: null, error, warnings ?? []);
}

/// <summary><paramref name="Kind"/> is a stable machine-readable category, not free text — a UI can
/// react to <c>"no_manifest"</c> by offering an "Initialise project" button, which it can't do with a
/// message string.</summary>
public sealed record JsonError(string Kind, string Message);

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

    /// <summary>A git invocation failed (network, auth, missing binary).</summary>
    public const string Git = "git";

    /// <summary>Anything unanticipated. A consumer should show the message and stop, not retry.</summary>
    public const string Unexpected = "unexpected";
}
