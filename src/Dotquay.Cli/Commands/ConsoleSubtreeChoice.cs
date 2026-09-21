using Dotquay.Core.Abstractions;

namespace Dotquay.Cli.Commands;

/// <summary>
/// Asks the user, at the terminal, whether dotquay guessed the right folder inside an addon's own
/// repository (docs/DESIGN-v0.20-SUBTREE-GUESS.md §4).
/// </summary>
/// <remarks>
/// Only ever constructed for an interactive, non-<c>--json</c> run. Everything else — CI, the editor
/// plugin, a piped script — uses <see cref="DeclineSubtreeChoice"/> and gets an error naming the
/// flags instead, so a run that cannot ask a human never installs from a folder nobody named.
/// </remarks>
internal sealed class ConsoleSubtreeChoice : ISubtreeChoice
{
    /// <summary>The prompt, or <see cref="DeclineSubtreeChoice"/> when there is no one to ask —
    /// <c>--json</c>, or redirected input, which covers CI and the plugin.</summary>
    public static ISubtreeChoice For(bool json) =>
        json || Console.IsInputRedirected ? DeclineSubtreeChoice.Instance : new ConsoleSubtreeChoice();

    public bool Accept(SubtreeChoiceRequest request)
    {
        ConsoleOutput.WriteLine(
            ConsoleColor.Yellow,
            $"'{request.AddonName}' isn't at {request.Finding.GuessedPath} in its repository.");

        Console.WriteLine($"  Found:   {request.Suggestion.Path}");
        Console.WriteLine($"  Because: {request.Suggestion.Explain(request.AddonName)}");
        Console.WriteLine($"  Installs into: addons/{request.Suggestion.Folder}");

        // The folder is not a detail of the answer, it is half of it: a Godot addon's own
        // res://addons/<folder>/ references are case- and word-sensitive strings, so agreeing to the
        // subtree without agreeing to the folder would install content that points at a path Godot
        // cannot find (SPEC.md §7.1.1).
        Console.WriteLine(
            "  Both are written to dotquay.toml as path/install_as, so this is asked once.");

        // Default yes, unlike the NuGet feed prompt's default no, and for a reason worth naming: that
        // question grants a third party the right to serve code into this project, while this one
        // only picks which folder of a repository the user has just named is the addon. Getting it
        // wrong costs a `dotquay remove`, and a default-no would leave the commonest answer behind an
        // extra keystroke.
        Console.Write("Use it? [Y/n] ");

        var answer = Console.ReadLine()?.Trim();

        // Null is end-of-input, not a bare Enter, and the two must not read the same: a console
        // whose input has closed answered nothing, and taking silence for a yes is how the
        // "nowhere that can't ask adopts on the user's behalf" rule gets quietly lost on the one
        // path that is allowed to ask.
        return answer is not null
            && (answer.Length == 0
                || string.Equals(answer, "y", StringComparison.OrdinalIgnoreCase)
                || string.Equals(answer, "yes", StringComparison.OrdinalIgnoreCase));
    }
}
