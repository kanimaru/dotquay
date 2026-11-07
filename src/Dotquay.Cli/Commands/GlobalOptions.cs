using System.CommandLine;

namespace Dotquay.Cli.Commands;

/// <summary>
/// Options every command accepts.
/// </summary>
/// <remarks>
/// <c>--project</c> exists because the Godot editor plugin (SPEC.md §18) cannot set a working
/// directory: Godot's <c>OS.execute</c> inherits the editor's own, which is never the Godot project
/// root. Before v0.5 every command called <c>Directory.GetCurrentDirectory()</c> directly, so the
/// only way to run dotquay against a specific project was to <c>cd</c> first — a shell feature, not
/// something a GUI can rely on portably. Defaulting to the current directory keeps every existing
/// shell invocation byte-identical in behaviour.
/// </remarks>
internal static class GlobalOptions {
    public static readonly Option<string?> ProjectDirectory = new("--project", "-C") {
        Description = "Project directory to operate on. Defaults to the current directory.",
        Recursive = true,
    };

    /// <summary>
    /// Logs every external command, HTTP request, pipeline step and destructive file operation to
    /// stderr (docs/DESIGN-v0.24-VERBOSE.md).
    /// </summary>
    /// <remarks>
    /// Read once, in <c>Program.cs</c>, before any command runs - so it covers argument parsing
    /// and the background update check too, and no command has to remember to honour it. stderr
    /// only: <c>--json</c> output and <c>dotquay mcp</c>'s stdout stay byte-identical with or
    /// without it.
    /// </remarks>
    public static readonly Option<bool> Verbose = new("--verbose", "-v") {
        Description = "Log what dotquay is doing (git/dotnet commands, HTTP requests, steps, file "
            + "operations) to stderr for debugging. Also: DOTQUAY_VERBOSE=1.",
        Recursive = true,
    };

    /// <summary><see cref="Verbose"/> plus each subprocess's captured output and every file
    /// write.</summary>
    public static readonly Option<bool> Trace = new("--trace") {
        Description = "Like --verbose, plus the output of every git/dotnet command and every file "
            + "write. Also: DOTQUAY_VERBOSE=trace.",
        Recursive = true,
    };

    /// <summary>Always returns an absolute path: a relative <c>--project</c> resolved against a
    /// caller's unknown working directory is exactly the ambiguity this option exists to
    /// remove.</summary>
    public static string Resolve(ParseResult parseResult) {
        var value = parseResult.GetValue(ProjectDirectory);
        return string.IsNullOrWhiteSpace(value)
            ? Directory.GetCurrentDirectory()
            : Path.GetFullPath(value);
    }
}
