using System.CommandLine;
using Dotquay.Mcp;

namespace Dotquay.Cli.Commands;

/// <summary>
/// <c>dotquay mcp</c> — runs dotquay as a Model Context Protocol server over stdio: the read tools
/// (Phase 1), the guarded write tools (Phase 2), and prompts/request logging (Phase 3)
/// (docs/DESIGN-v0.18-MCP.md).
/// </summary>
/// <remarks>
/// Scoped by the same <c>--project</c>/<c>-C</c> every other command already accepts
/// (<see cref="GlobalOptions"/>), not a separate flag: a second, differently-behaving <c>--project</c>
/// on one command would be its own kind of bug, and <see cref="Dotquay.Mcp.Scope.ProjectScope"/> is
/// built to take more than one root should a future <c>--project-root</c> (repeatable) ever need it —
/// nothing here forecloses that, it's simply not what this command exposes yet. Every tool resolves
/// its own <c>project</c> argument against this one root, matching every other command's implicit
/// "current directory unless told otherwise" default (<see cref="GlobalOptions.Resolve"/>).
/// </remarks>
internal static class McpCommand
{
    public static Command Build()
    {
        var logOption = new Option<string?>("--log")
        {
            Description = "Append a plain-text request trace (id, method, duration, and the first line "
                + "of any exception) to this file. Without it, the same lines go to stderr instead.",
        };

        var command = new Command(
            "mcp",
            "Run dotquay as a Model Context Protocol server over stdio: tools, resources, and prompts for the "
                + "project at --project (or the current directory). Blocks until the client disconnects.")
        {
            logOption,
        };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            // TTY guard (design doc §12, OQ-2): refuse before anything else runs when stdin is a real
            // console, not the pipe an MCP client spawns this with — otherwise a human who ran this by
            // accident just sees a process that never prints anything, indistinguishable from a hang.
            if (TtyGuard.ShouldRefuse(Console.IsInputRedirected))
            {
                Console.Error.WriteLine(TtyGuard.Message);
                return TtyGuard.ExitCode;
            }

            var projectDirectory = GlobalOptions.Resolve(parseResult);
            var logFile = parseResult.GetValue(logOption);
            await McpServerBuilder.RunStdioAsync([projectDirectory], logFile, cancellationToken);
            return 0;
        });

        return command;
    }
}
