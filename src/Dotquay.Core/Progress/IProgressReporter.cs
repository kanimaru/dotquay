namespace Dotquay.Core.Progress;

/// <summary>One thing dotquay is currently doing.</summary>
/// <param name="Message">Human-readable, present tense, naming the addon — e.g.
/// <c>"fetching mtoon-shader"</c>.</param>
/// <param name="Completed">How many units of the current phase are finished, or null when the work
/// isn't countable.</param>
/// <param name="Total">How many units there are in total, or null.</param>
public readonly record struct ProgressUpdate(string Message, int? Completed = null,
    int? Total = null);

/// <summary>
/// Where <c>Dotquay.Core</c> announces what it is doing, so a long <c>update</c> doesn't look
/// frozen.
/// </summary>
/// <remarks>
/// An interface rather than direct console writing for the usual reason — Core must stay usable
/// without a console (the Godot plugin drives it through JSON, tests drive it headlessly) — and for
/// one specific one: progress must never reach stdout, because <c>--json</c> guarantees stdout
/// holds exactly one document (SPEC.md §17). Keeping the decision at the edge means Core physically
/// cannot break that guarantee.
/// </remarks>
public interface IProgressReporter {
    void Report(ProgressUpdate update);
}

/// <summary>Discards everything. The default, so no caller has to pass a reporter it doesn't want
/// and no code path has to null-check one.</summary>
public sealed class NullProgressReporter : IProgressReporter {
    public static readonly NullProgressReporter Instance = new();

    private NullProgressReporter() {
    }

    public void Report(ProgressUpdate update) {
    }
}

/// <summary>Convenience wrappers so call sites read as one short line.</summary>
public static class ProgressReporterExtensions {
    public static void Report(this IProgressReporter reporter, string message) =>
        reporter.Report(new ProgressUpdate(message));

    public static void Report(this IProgressReporter reporter, string message, int completed,
        int total) =>
        reporter.Report(new ProgressUpdate(message, completed, total));
}
