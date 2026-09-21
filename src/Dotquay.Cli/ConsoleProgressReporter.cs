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

    private static readonly TimeSpan RedrawInterval = TimeSpan.FromMilliseconds(120);

    /// <summary>
    /// The reporter currently drawing, if any.
    /// </summary>
    /// <remarks>
    /// Static because the thing it guards is static: there is one terminal, and whoever writes to it
    /// next has to know whether a spinner is sitting on the current line. Threading a reporter
    /// through to every prompt would model this as if several could be drawing at once, which no
    /// arrangement of this CLI produces — one command runs, with one reporter, and the prompts it may
    /// raise are nested inside that.
    /// </remarks>
    private static ConsoleProgressReporter? _active;

    private static readonly Lock ActiveGate = new();

    private readonly Timer _timer;
    private readonly Lock _gate = new();

    private string _message = string.Empty;
    private string _counter = string.Empty;
    private int _frame;
    private int _lastRenderedWidth;
    private bool _disposed;
    private bool _suspended;

    private ConsoleProgressReporter(TimeSpan interval)
    {
        _timer = new Timer(_ => Tick(), null, interval, interval);

        lock (ActiveGate)
        {
            _active = this;
        }
    }

    /// <summary>Returns a live reporter, or the null one when nothing should be drawn.</summary>
    public static IProgressReporter Create(bool json)
    {
        return ShouldDraw(json) ? new ConsoleProgressReporter(RedrawInterval) : NullProgressReporter.Instance;
    }

    /// <summary>A reporter that only ever draws when told to, for tests that drive it a frame at a
    /// time against a substituted <see cref="Console.Error"/> instead of a real terminal.</summary>
    internal static ConsoleProgressReporter CreateForTests() => new(Timeout.InfiniteTimeSpan);

    /// <summary>
    /// Clears the spinner and keeps it off the screen for as long as the returned scope lives — for
    /// anything that needs the terminal to itself, which in practice means asking the user a
    /// question.
    /// </summary>
    /// <remarks>
    /// Found the hard way (v0.20): a prompt raised mid-resolve was printed straight onto the line the
    /// spinner owned, so the question arrived as
    /// <c>| checking where 'SproutyDialogs' lives in its repository'SproutyDialogs' isn't at …</c> and
    /// the spinner carried on repainting over it while the user was reading. A question the user
    /// cannot read is a question they answer by guessing.
    /// <para>
    /// A no-op when nothing is drawing (<c>--json</c>, CI, redirected stderr) and when a scope is
    /// already open, so a caller never has to ask whether there is a spinner to pause — which is what
    /// lets every prompt open one unconditionally.
    /// </para>
    /// </remarks>
    public static IDisposable Pause()
    {
        ConsoleProgressReporter? active;

        lock (ActiveGate)
        {
            active = _active;
        }

        return active?.Suspend() ?? NullScope.Instance;
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
        // timer tick later. Suspended, this still records the step — it just doesn't draw it, so the
        // spinner resumes on whatever is current rather than on whatever was current when the
        // question was asked.
        Tick();
    }

    private void Tick()
    {
        lock (_gate)
        {
            if (_disposed || _suspended || _message.Length == 0)
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

    private IDisposable Suspend()
    {
        lock (_gate)
        {
            // Already quiet, for either reason. Handing back a real scope would let the inner `using`
            // resume drawing while the outer one still needs the terminal.
            if (_disposed || _suspended)
            {
                return NullScope.Instance;
            }

            _suspended = true;
            ClearLine();
        }

        return new Resumption(this);
    }

    private void Resume()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _suspended = false;
        }

        // Straight away, not at the next tick: the work carried on while the question was being
        // answered, and a second of apparent silence after pressing Enter reads as a hang.
        Tick();
    }

    /// <summary>Wipes the spinner's line and forgets it was ever there.</summary>
    /// <remarks>Callers hold <see cref="_gate"/>.</remarks>
    private void ClearLine()
    {
        if (_lastRenderedWidth > 0)
        {
            Console.Error.Write("\r" + new string(' ', _lastRenderedWidth) + "\r");
            _lastRenderedWidth = 0;
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
            ClearLine();
        }

        lock (ActiveGate)
        {
            // Only if it is still this one: a second command in the same process has already taken
            // over, and clearing unconditionally would leave its prompts unable to find it.
            if (ReferenceEquals(_active, this))
            {
                _active = null;
            }
        }
    }

    /// <summary>The open half of a <see cref="Pause"/>.</summary>
    private sealed class Resumption(ConsoleProgressReporter reporter) : IDisposable
    {
        private bool _resumed;

        public void Dispose()
        {
            if (_resumed)
            {
                return;
            }

            _resumed = true;
            reporter.Resume();
        }
    }

    /// <summary>What <see cref="Pause"/> hands back when there is nothing to pause.</summary>
    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();

        public void Dispose()
        {
        }
    }
}
