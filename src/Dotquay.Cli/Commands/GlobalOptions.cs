using System.CommandLine;

namespace Dotquay.Cli.Commands;

/// <summary>
/// Options every command accepts.
/// </summary>
/// <remarks>
/// <c>--project</c> exists because the Godot editor plugin (SPEC.md §18) cannot set a working
/// directory: Godot's <c>OS.execute</c> inherits the editor's own, which is never the Godot project
/// root. Before v0.5 every command called <c>Directory.GetCurrentDirectory()</c> directly, so the only
/// way to run dotquay against a specific project was to <c>cd</c> first — a shell feature, not
/// something a GUI can rely on portably. Defaulting to the current directory keeps every existing
/// shell invocation byte-identical in behaviour.
/// </remarks>
internal static class GlobalOptions
{
    public static readonly Option<string?> ProjectDirectory = new("--project", "-C")
    {
        Description = "Project directory to operate on. Defaults to the current directory.",
        Recursive = true,
    };

    /// <summary>Always returns an absolute path: a relative <c>--project</c> resolved against a
    /// caller's unknown working directory is exactly the ambiguity this option exists to remove.</summary>
    public static string Resolve(ParseResult parseResult)
    {
        var value = parseResult.GetValue(ProjectDirectory);
        return string.IsNullOrWhiteSpace(value)
            ? Directory.GetCurrentDirectory()
            : Path.GetFullPath(value);
    }
}
