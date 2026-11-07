using Dotquay.Core.Diagnostics;

namespace Dotquay.Cli.Commands;

/// <summary>Minimal colored console output with zero extra dependencies. Deliberately not using
/// Spectre.Console here: the CLI framework it shipped with (Spectre.Console.Cli) proved unsafe
/// under NativeAOT (see docs/SPRINT-01.md), so this project sticks to APIs the BCL itself
/// guarantees are AOT-safe rather than re-introducing a related dependency without re-verifying
/// it.</summary>
internal static class ConsoleOutput {
    public static void WriteLine(ConsoleColor color, string message) {
        var previous = Console.ForegroundColor;
        Console.ForegroundColor = color;
        Console.WriteLine(message);
        Console.ForegroundColor = previous;
    }

    /// <summary>
    /// <see cref="WriteLine"/> for text dotquay did not compose itself - errors and warnings built
    /// from exception messages and git output - with credentials masked (see
    /// <see cref="SecretRedactor"/>).
    /// </summary>
    /// <remarks>
    /// A separate method rather than redacting every line: <c>list</c> and <c>graph</c> print the
    /// manifest's own URLs as data the user wrote, and silently rewriting those is a different
    /// decision from not echoing a token back inside an error.
    /// </remarks>
    public static void WriteRedacted(ConsoleColor color, string message) =>
        WriteLine(color, SecretRedactor.Redact(message));
}
