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
public interface IProjectScope
{
    /// <summary>Returns the resolved absolute path, or throws <see cref="ScopeException"/> when
    /// <paramref name="requestedPath"/> — or the default root, if null — falls outside every
    /// configured root.</summary>
    string Resolve(string? requestedPath);

    /// <summary>The roots this scope currently allows — the server's own <c>--project</c> roots plus
    /// any a client has supplied via the MCP <c>roots</c> capability (design doc §4.3, see
    /// <c>ClientRootsSync</c>) — for the tool descriptions, error messages, and <c>resources/list</c>
    /// that need to reflect the live set, not just what the process started with.</summary>
    IReadOnlyList<string> Roots { get; }

    /// <summary>Widens the allowed set with <paramref name="paths"/>, normalizing each one and
    /// silently skipping any already present. Never removes a root: the server's own
    /// <c>--project</c> roots are permanent for the life of the process, and a client's roots only
    /// ever grow what a call may name — narrowing scope mid-session is not a thing this server does,
    /// so <c>ClientRootsSync</c> never has to reconcile a shrinking set.</summary>
    void AddRoots(IEnumerable<string> paths);
}
