namespace Dependi.Cli.Commands;

/// <summary>Minimal colored console output with zero extra dependencies. Deliberately not using
/// Spectre.Console here: the CLI framework it shipped with (Spectre.Console.Cli) proved unsafe
/// under NativeAOT (see docs/SPRINT-01.md), so this project sticks to APIs the BCL itself
/// guarantees are AOT-safe rather than re-introducing a related dependency without re-verifying it.</summary>
internal static class ConsoleOutput
{
    public static void WriteLine(ConsoleColor color, string message)
    {
        var previous = Console.ForegroundColor;
        Console.ForegroundColor = color;
        Console.WriteLine(message);
        Console.ForegroundColor = previous;
    }
}
