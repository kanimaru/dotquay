using System.Collections.Concurrent;
using Dotquay.Core.Diagnostics;

namespace Dotquay.Tests.Support;

/// <summary>
/// Collects diagnostic events in memory. Install it with <see cref="Install"/>, which scopes it to the
/// calling test's logical flow - parallel tests each see only their own lines (see
/// <see cref="DiagnosticLog"/>'s remarks).
/// </summary>
public sealed class RecordingDiagnosticSink : IDiagnosticSink
{
    private readonly ConcurrentQueue<DiagnosticEvent> _events = new();

    public RecordingDiagnosticSink(DiagnosticLevel level = DiagnosticLevel.Trace)
    {
        Level = level;
    }

    public DiagnosticLevel Level { get; }

    public IReadOnlyList<DiagnosticEvent> Events => _events.ToArray();

    public IEnumerable<string> Messages(string category) =>
        Events.Where(e => e.Category == category).Select(e => e.Message);

    public string AllText => string.Join("\n", Events.Select(e => $"{e.Category}: {e.Message}"));

    public void Write(DiagnosticEvent diagnostic) => _events.Enqueue(diagnostic);

    /// <summary>Creates a sink and makes it the ambient one until <paramref name="scope"/> is disposed.</summary>
    public static RecordingDiagnosticSink Install(out IDisposable scope, DiagnosticLevel level = DiagnosticLevel.Trace)
    {
        var sink = new RecordingDiagnosticSink(level);
        scope = DiagnosticLog.Use(sink);
        return sink;
    }
}
