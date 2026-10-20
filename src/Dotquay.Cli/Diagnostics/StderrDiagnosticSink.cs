using System.Diagnostics;
using System.Globalization;
using System.Text;
using Dotquay.Core.Diagnostics;

namespace Dotquay.Cli.Diagnostics;

/// <summary>
/// Writes diagnostic lines to <b>stderr</b>, timestamped from process start and tagged by subsystem:
/// <code>
/// [   0.412s] git      | $ git ls-remote --tags https://gitlab.com/x/y.git
/// [   0.958s] git      | exit 0 after 546 ms
/// </code>
/// </summary>
/// <remarks>
/// <para>
/// stderr for the same reason progress is (see <see cref="ConsoleProgressReporter"/>): stdout belongs
/// to <c>--json</c>'s single document and to <c>dotquay mcp</c>'s JSON-RPC stream, and a diagnostic
/// line there would break exactly the callers someone is most likely to be debugging. This is what
/// makes <c>dotquay status --json --verbose | jq</c> work.
/// </para>
/// <para>
/// Pauses the spinner around each line, so log lines scroll above it instead of being painted over
/// by the next carriage-return redraw. The fixed-width prefix keeps a long log scannable: times line
/// up, categories line up, and continuation lines of a multi-line message (captured git output, an
/// exception chain) sit under the first one instead of starting at column zero.
/// </para>
/// </remarks>
internal sealed class StderrDiagnosticSink : IDiagnosticSink
{
    private const int CategoryWidth = 8;

    private readonly TextWriter _writer;
    private readonly bool _color;
    private readonly Func<IDisposable> _pauseProgress;
    private readonly Func<TimeSpan> _clock;
    private readonly Lock _gate = new();

    internal StderrDiagnosticSink(
        DiagnosticLevel level,
        TextWriter writer,
        bool color,
        Func<IDisposable> pauseProgress,
        Func<TimeSpan>? clock = null)
    {
        Level = level;
        _writer = writer;
        _color = color;
        _pauseProgress = pauseProgress;

        if (clock is null)
        {
            var started = Stopwatch.GetTimestamp();
            clock = () => Stopwatch.GetElapsedTime(started);
        }

        _clock = clock;
    }

    public DiagnosticLevel Level { get; }

    /// <summary>The real sink for this process, or the null one when diagnostics are off.</summary>
    public static IDiagnosticSink Create(DiagnosticLevel level) =>
        level == DiagnosticLevel.Off
            ? NullDiagnosticSink.Instance
            : new StderrDiagnosticSink(level, Console.Error, ShouldColor(), ConsoleProgressReporter.Pause);

    public void Write(DiagnosticEvent diagnostic)
    {
        var text = Format(diagnostic, _clock());

        lock (_gate)
        {
            using var _ = _pauseProgress();

            if (_color)
            {
                var previous = Console.ForegroundColor;
                Console.ForegroundColor = ConsoleColor.DarkGray;
                _writer.WriteLine(text);
                Console.ForegroundColor = previous;
            }
            else
            {
                _writer.WriteLine(text);
            }

            _writer.Flush();
        }
    }

    internal static string Format(DiagnosticEvent diagnostic, TimeSpan elapsed)
    {
        var prefix = string.Create(CultureInfo.InvariantCulture, $"[{elapsed.TotalSeconds,8:0.000}s] {Fit(diagnostic.Category)} | ");
        var continuation = new string(' ', prefix.Length - 2) + "| ";

        var builder = new StringBuilder();
        var first = true;

        foreach (var line in diagnostic.Message.ReplaceLineEndings("\n").TrimEnd('\n').Split('\n'))
        {
            if (!first)
            {
                builder.Append(Environment.NewLine);
            }

            builder.Append(first ? prefix : continuation).Append(line);
            first = false;
        }

        return builder.ToString();
    }

    private static string Fit(string category) =>
        category.Length > CategoryWidth ? category[..CategoryWidth] : category.PadRight(CategoryWidth);

    /// <summary>Same opt-outs as the spinner: redirected stderr (a log file), <c>NO_COLOR</c>,
    /// <c>TERM=dumb</c>. A log file full of ANSI escapes is worse than an uncoloured terminal.</summary>
    private static bool ShouldColor()
    {
        if (Console.IsErrorRedirected || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NO_COLOR")))
        {
            return false;
        }

        return !string.Equals(Environment.GetEnvironmentVariable("TERM"), "dumb", StringComparison.OrdinalIgnoreCase);
    }
}
