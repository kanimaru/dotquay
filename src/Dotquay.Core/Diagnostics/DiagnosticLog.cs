using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace Dotquay.Core.Diagnostics;

/// <summary>
/// The ambient diagnostic channel every part of <c>Dotquay.Core</c> writes to.
/// </summary>
/// <remarks>
/// <para>
/// Ambient (an <see cref="AsyncLocal{T}"/>) rather than a constructor dependency, deliberately. The
/// things worth logging live at the leaves - <c>ProcessGitClient</c>, <c>ProcessPatchTool</c>,
/// <c>ProcessDotnetCliRunner</c>, the HTTP clients, <c>SystemFileSystem</c> - and those are built in
/// a dozen places across Core, the CLI and <c>Dotquay.Mcp</c>, several of them deep inside
/// <c>DependencyWorkflow</c>. Threading a sink through every one of those constructors would touch
/// most of the codebase for a feature that is off by default, and the first new
/// <c>new ProcessGitClient()</c> that forgot the argument would silently go dark.
/// </para>
/// <para>
/// <see cref="AsyncLocal{T}"/> rather than a plain static so the setting flows with the logical call,
/// not the process: parallel xUnit tests each <see cref="Use"/> their own recording sink without
/// seeing each other's lines, and the value follows every <c>await</c>, <c>Task.Run</c> and
/// <c>HttpClient</c> handler chain started underneath the scope.
/// </para>
/// <para>
/// Messages are built through <see cref="DiagnosticMessageHandler"/>, an interpolated-string
/// handler, so <c>DiagnosticLog.Write(DiagnosticLevel.Verbose, "git", $"... {expensive}")</c> costs
/// one level comparison and evaluates nothing when diagnostics are off - which is always, unless
/// someone asked.
/// </para>
/// </remarks>
public static class DiagnosticLog
{
    private static readonly AsyncLocal<IDiagnosticSink?> Ambient = new();

    /// <summary>The sink for the current logical call. Never null.</summary>
    public static IDiagnosticSink Current => Ambient.Value ?? NullDiagnosticSink.Instance;

    /// <summary>Whether a line at <paramref name="level"/> would be shown right now.</summary>
    public static bool IsEnabled(DiagnosticLevel level) =>
        level != DiagnosticLevel.Off && Current.Level >= level;

    /// <summary>
    /// Routes diagnostics to <paramref name="sink"/> until the returned scope is disposed, then
    /// restores whatever was there before.
    /// </summary>
    /// <remarks>
    /// Synchronous on purpose: an <see cref="AsyncLocal{T}"/> assigned inside an <c>async</c> method
    /// does not flow back out to its caller, so a <c>UseAsync</c> would appear to work and do
    /// nothing.
    /// </remarks>
    public static IDisposable Use(IDiagnosticSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);

        var previous = Ambient.Value;
        Ambient.Value = sink;
        return new Restoration(previous);
    }

    /// <summary>Writes a pre-built message. Prefer the interpolated overload for anything that formats.</summary>
    public static void Write(DiagnosticLevel level, string category, string message)
    {
        if (IsEnabled(level))
        {
            Current.Write(new DiagnosticEvent(level, category, message));
        }
    }

    /// <summary>Writes an interpolated message, formatting it only when the level is enabled.</summary>
    public static void Write(
        DiagnosticLevel level,
        string category,
        [InterpolatedStringHandlerArgument("level")] ref DiagnosticMessageHandler message)
    {
        if (message.Enabled)
        {
            Current.Write(new DiagnosticEvent(level, category, message.ToStringAndClear()));
        }
    }

    /// <summary>Shorthand for <see cref="DiagnosticLevel.Verbose"/>.</summary>
    public static void Verbose(string category, string message) =>
        Write(DiagnosticLevel.Verbose, category, message);

    /// <summary>Shorthand for <see cref="DiagnosticLevel.Trace"/>.</summary>
    public static void Trace(string category, string message) =>
        Write(DiagnosticLevel.Trace, category, message);

    /// <summary>
    /// Times a block: logs <paramref name="label"/> when disposed, with how long it took.
    /// </summary>
    /// <remarks>Returns a shared no-op when the level is off, so an unconditional <c>using</c> costs
    /// nothing in the normal case.</remarks>
    public static IDisposable Measure(DiagnosticLevel level, string category, string label) =>
        IsEnabled(level) ? new Measurement(level, category, label) : NoOpScope.Instance;

    /// <summary>Milliseconds, rounded, as every duration in the log is written - one unit throughout so
    /// a reader can compare lines without converting.</summary>
    public static string FormatDuration(TimeSpan elapsed) =>
        string.Create(CultureInfo.InvariantCulture, $"{Math.Round(elapsed.TotalMilliseconds):0} ms");

    private sealed class Restoration(IDiagnosticSink? previous) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Ambient.Value = previous;
        }
    }

    private sealed class Measurement(DiagnosticLevel level, string category, string label) : IDisposable
    {
        private readonly long _started = Stopwatch.GetTimestamp();
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Write(level, category, $"{label} ({FormatDuration(Stopwatch.GetElapsedTime(_started))})");
        }
    }

    private sealed class NoOpScope : IDisposable
    {
        public static readonly NoOpScope Instance = new();

        public void Dispose()
        {
        }
    }
}

/// <summary>
/// Interpolated-string handler behind <see cref="DiagnosticLog.Write(DiagnosticLevel, string, ref DiagnosticMessageHandler)"/>:
/// the compiler asks it whether to format at all before evaluating a single hole.
/// </summary>
[InterpolatedStringHandler]
public ref struct DiagnosticMessageHandler
{
    private DefaultInterpolatedStringHandler _inner;

    public DiagnosticMessageHandler(int literalLength, int formattedCount, DiagnosticLevel level, out bool shouldAppend)
    {
        Enabled = DiagnosticLog.IsEnabled(level);
        shouldAppend = Enabled;
        // Invariant, not the current culture: a log is pasted into issues and diffed between machines,
        // and "1,5 s" from a German Windows next to "1.5 s" from CI is noise. The shipped binary is
        // InvariantGlobalization anyway; this makes the test host and every other host agree.
        _inner = Enabled
            ? new DefaultInterpolatedStringHandler(literalLength, formattedCount, CultureInfo.InvariantCulture)
            : default;
    }

    internal bool Enabled { get; }

    public void AppendLiteral(string value) => _inner.AppendLiteral(value);

    public void AppendFormatted<T>(T value) => _inner.AppendFormatted(value);

    public void AppendFormatted<T>(T value, string? format) => _inner.AppendFormatted(value, format);

    public void AppendFormatted<T>(T value, int alignment) => _inner.AppendFormatted(value, alignment);

    public void AppendFormatted<T>(T value, int alignment, string? format) =>
        _inner.AppendFormatted(value, alignment, format);

    public void AppendFormatted(string? value) => _inner.AppendFormatted(value);

    internal string ToStringAndClear() => Enabled ? _inner.ToStringAndClear() : string.Empty;
}
