using Dotquay.Core.Progress;

namespace Dotquay.Core.Diagnostics;

/// <summary>
/// Copies every progress step into the diagnostic log, then hands it on unchanged.
/// </summary>
/// <remarks>
/// The progress messages already name each pipeline step in plain words ("fetching mtoon-shader",
/// "[2/5] installing core") - exactly the skeleton a verbose log needs to make the git and HTTP
/// lines under it readable. Reusing them means no second set of stage announcements to keep in sync
/// with the first. And unlike the spinner, which redraws one line and vanishes under <c>--json</c>,
/// the log keeps every step, in order, with a timestamp.
/// </remarks>
public sealed class DiagnosticProgressReporter : IProgressReporter {
    private readonly IProgressReporter _inner;

    public DiagnosticProgressReporter(IProgressReporter inner) {
        _inner = inner;
    }

    /// <summary>Wraps <paramref name="inner"/> only when diagnostics are on, so the normal path
    /// keeps the exact reporter it had.</summary>
    public static IProgressReporter Wrap(IProgressReporter inner) =>
        DiagnosticLog.IsEnabled(DiagnosticLevel.Verbose) ? new DiagnosticProgressReporter(inner)
        : inner;

    public void Report(ProgressUpdate update) {
        DiagnosticLog.Write(DiagnosticLevel.Verbose, "step", $"{Counter(update)}{update.Message}");
        _inner.Report(update);
    }

    private static string Counter(ProgressUpdate update) =>
        update is { Completed: int completed, Total: int total } && total > 0
            ? $"[{completed}/{total}] "
            : string.Empty;
}
