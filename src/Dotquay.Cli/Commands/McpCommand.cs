using System.CommandLine;
using Dotquay.Mcp;

namespace Dotquay.Cli.Commands;

/// <summary>
/// <c>dotquay mcp</c> (v0.18, Phase 1) — runs dotquay as a Model Context Protocol server over stdio,
/// exposing the eight read tools and the manifest/lockfile/local-links/patches resources
/// (docs/DESIGN-v0.18-MCP.md).
/// </summary>
/// <remarks>
/// Scoped by the same <c>--project</c>/<c>-C</c> every other command already accepts
/// (<see cref="GlobalOptions"/>), not a separate flag: a second, differently-behaving <c>--project</c>
/// on one command would be its own kind of bug, and <see cref="Dotquay.Mcp.Scope.ProjectScope"/> is
/// built to take more than one root should a future <c>--project-root</c> (repeatable) ever need it —
/// nothing here forecloses that, it's simply not what Phase 1 needs. All 8 tools resolve their own
/// <c>project</c> argument against this one root, matching every other command's implicit "current
/// directory unless told otherwise" default (<see cref="GlobalOptions.Resolve"/>).
/// </remarks>
internal static class McpCommand
{
    public static Command Build()
    {
        var command = new Command(
            "mcp",
            "Run dotquay as a Model Context Protocol server over stdio: read-only tools and resources for the "
                + "project at --project (or the current directory). Blocks until the client disconnects.");

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var projectDirectory = GlobalOptions.Resolve(parseResult);
            await McpServerBuilder.RunStdioAsync([projectDirectory], cancellationToken);
            return 0;
        });

        return command;
    }
}
