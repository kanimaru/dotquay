using Dotquay.Core.Progress;

namespace Dotquay.Cli;

/// <summary>
/// Draws a one-line spinner and step description on <b>stderr</b>, redrawn on a timer so a long
/// <c>git fetch</c> visibly ticks instead of looking hung.
/// </summary>
/// <remarks>
/// <para>
/// stderr, not stdout, and this is not a style choice: <c>--json</c> guarantees stdout carries
/// exactly one document (SPEC.md §17), and a spinner there would corrupt every parse. Writing to
/// stderr also means <c>dotquay graph --json | jq</c> shows progress in the terminal while the pipe
/// stays clean.
/// </para>
/// <para>
/// Disables itself whenever the output isn't a terminal a human is watching — redirected stderr, CI,
/// <c>TERM=dumb</c>, <c>NO_COLOR</c>, or <c>--json</c>. Carriage-return redraws turn into thousands of
/// junk lines in a log file, which is worse than no progress at all.
/// </para>
/// </remarks>
internal sealed class ConsoleProgressReporter : IProgressReporter, IDisposable
{
    // Plain ASCII. Braille spinners look better but render as boxes in the Windows console host that
    // still ships as the default for .bat launches and a lot of CI images.
    private static readonly char[] Frames = ['|', '/', '-', '\\'];

    private readonly Timer _timer;
    private readonly Lock _gate = new();

    private string _message = string.Empty;
    private string _counter = string.Empty;
    private int _frame;
    private int _lastRenderedWidth;
    private bool _disposed;

    private ConsoleProgressReporter()
    {
        _timer = new Timer(_ => Tick(), null, TimeSpan.FromMilliseconds(120), TimeSpan.FromMilliseconds(120));
    }

    /// <summary>Returns a live reporter, or the null one when nothing should be drawn.</summary>
    public static IProgressReporter Create(bool json)
    {
        return ShouldDraw(json) ? new ConsoleProgressReporter() : NullProgressReporter.Instance;
    }

    private static bool ShouldDraw(bool json)
    {
        if (json || Console.IsErrorRedirected)
        {
            return false;
        }

        // CI is set by GitHub Actions, GitLab CI, and most others. A build log doesn't need a spinner
        // and is actively harmed by one.
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CI"))
            || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NO_COLOR")))
        {
            return false;
        }

        var term = Environment.GetEnvironmentVariable("TERM");
        return !string.Equals(term, "dumb", StringComparison.OrdinalIgnoreCase);
    }

    public void Report(ProgressUpdate update)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _message = update.Message;
            _counter = update is { Completed: int completed, Total: int total } && total > 0
                ? $"[{completed}/{total}] "
                : string.Empty;
        }

        // Redraw immediately so a new step appears the instant it starts, rather than up to one
        // timer tick later.
        Tick();
    }

    private void Tick()
    {
        lock (_gate)
        {
            if (_disposed || _message.Length == 0)
            {
                return;
            }

            var text = $"{Frames[_frame]} {_counter}{_message}";
            _frame = (_frame + 1) % Frames.Length;

            // Trim to the window so a long addon name can't wrap — a wrapped line makes the next
            // carriage return overwrite only the tail, leaving debris on screen.
            var width = SafeWindowWidth();
            if (text.Length > width - 1)
            {
                text = text[..Math.Max(0, width - 2)] + "…";
            }

            Write(text.PadRight(_lastRenderedWidth));
            _lastRenderedWidth = text.Length;
        }
    }

    private static int SafeWindowWidth()
    {
        try
        {
            var width = Console.WindowWidth;
            return width > 10 ? width : 80;
        }
        catch (IOException)
        {
            // No real console attached (some launchers, some test hosts). 80 is a safe assumption and
            // the alternative is crashing over cosmetics.
            return 80;
        }
    }

    private static void Write(string text) => Console.Error.Write("\r" + text);

    /// <summary>Clears the line so the command's actual output starts on a clean row.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _timer.Dispose();

            if (_lastRenderedWidth > 0)
            {
                Console.Error.Write("\r" + new string(' ', _lastRenderedWidth) + "\r");
            }
        }
    }
}
