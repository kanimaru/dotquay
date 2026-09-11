using Dotquay.Core.Abstractions;

namespace Dotquay.Cli.Commands;

/// <summary>
/// Asks, at the terminal, whether a source generator being added should be kept private to this
/// project (docs/DESIGN-v0.14-FEED-COMMANDS.md).
/// </summary>
/// <remarks>
/// <para>The questions are deliberately not about NuGet. An earlier draft of this feature printed a
/// hint naming <c>private_assets</c> and the six-tag <c>include_assets</c> incantation and left the
/// user to decide; the person it was written for had authored the generator, owned the feed, and still
/// could not tell what the options did. A prompt that assumes the reader can reason about NuGet asset
/// flow is a prompt for someone who did not need it.</para>
/// <para>So: two questions in the project's own terms, each answerable without knowing the word
/// "assets", and a default on both that is the existing behaviour. The first re-asks what
/// <see cref="Core.Dotnet.CsprojPackable"/> already guessed, because that guess is a heuristic and the
/// user is the authority on whether their project is published.</para>
/// </remarks>
internal sealed class ConsoleGeneratorAssetDecision : IGeneratorAssetDecision
{
    /// <summary>The prompt, or "share" when there is no one to ask — <c>--json</c>, or redirected
    /// input, which covers CI and the editor plugin. Both of those get the pre-v0.14 behaviour.</summary>
    public static IGeneratorAssetDecision For(bool json) =>
        json || Console.IsInputRedirected ? ShareGeneratorDecision.Instance : new ConsoleGeneratorAssetDecision();

    public bool KeepPrivate(GeneratorAssetRequest request)
    {
        ConsoleOutput.WriteLine(
            ConsoleColor.Yellow,
            $"{request.PackageId} {request.Version} is a source generator - it ships compiler plugins "
            + "rather than library code.");

        // The evidence is named because the probe can be wrong, and an unexplained question about
        // whether your game is "published as a package" reads as a non-sequitur.
        Console.Write($"This project sets {request.PackableEvidence}. Is it published as a package that other "
            + "projects depend on? [y/N] ");

        if (!ReadYes(defaultAnswer: false))
        {
            // Nothing to configure, and saying so is the point: silence here would leave the user
            // wondering whether a decision was made for them.
            Console.WriteLine("  Nothing to configure then - a generator only matters to whatever compiles with it.");
            return false;
        }

        Console.Write("Should projects that depend on yours also get this generator? [Y/n] ");

        // Default yes, because that is what NuGet does with no declaration at all. A default that
        // silently differed from the no-flag behaviour would make the prompt itself a behaviour change.
        if (ReadYes(defaultAnswer: true))
        {
            Console.WriteLine("  Leaving it shared - that is NuGet's default, so nothing is written.");
            return false;
        }

        return true;
    }

    private static bool ReadYes(bool defaultAnswer)
    {
        var answer = Console.ReadLine()?.Trim();

        if (string.IsNullOrEmpty(answer))
        {
            return defaultAnswer;
        }

        return answer.StartsWith("y", StringComparison.OrdinalIgnoreCase);
    }
}
