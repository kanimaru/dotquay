namespace Dotquay.Cli.Commands;

/// <summary>Shared, honest response for commands whose CLI shape exists (so `dotquay --help` shows
/// the full v1 surface from SPEC.md §6) but whose implementation hasn't landed yet. Exits non-zero
/// instead of throwing, so scripting against the CLI fails cleanly rather than with a stack trace.
/// See docs/SPRINT-01.md for what's tracked where.</summary>
internal static class NotYetImplemented {
    public static int Report(string commandName, string requirementIds) {
        ConsoleOutput.WriteLine(
            ConsoleColor.Yellow,
            $"`{commandName}` isn't implemented yet (tracked as {requirementIds} — see "
            + $"docs/SPRINT-01.md).");
        return 1;
    }
}
