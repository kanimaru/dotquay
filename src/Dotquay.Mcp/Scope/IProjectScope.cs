namespace Dotquay.Mcp.Scope;

/// <summary>
/// Resolves a tool call's <c>project</c> argument to an absolute path this MCP server is allowed to
/// touch.
/// </summary>
/// <remarks>
/// Exists because an MCP server is a long-running process a client drives entirely through structured
/// arguments — unlike the CLI's <c>--project</c> (see <c>Dotquay.Cli.Commands.GlobalOptions</c>),
/// there is no shell session's working directory backing an implicit default, and no human reading
/// the command before it runs. An agent that got a path wrong (a typo, a hallucinated directory, a
/// project from a different conversation) should get a clear "outside your configured roots" failure,
/// not a `dotquay.toml` read from wherever that path happened to resolve on disk.
/// </remarks>
public interface IProjectScope {
    /// <summary>Returns the resolved absolute path, or throws <see cref="ScopeException"/> when
    /// <paramref name="requestedPath"/> — or the default root, if null — falls outside every
    /// configured root.</summary>
    string Resolve(string? requestedPath);

    /// <summary>The roots this scope allows — the server's own <c>--project</c> roots, or its
    /// working directory when none was given — for the tool descriptions, error messages, and
    /// <c>resources/list</c>. Fixed for the life of the process: the MCP <c>roots</c> capability that
    /// once widened it is deprecated as of specification version 2026-07-28 (SEP-2577), whose
    /// migration note is to pass directories "via tool parameters ... or server configuration" —
    /// exactly the <c>project</c> argument and <c>--project</c>.</summary>
    IReadOnlyList<string> Roots { get; }
}
