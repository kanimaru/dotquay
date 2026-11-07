using Dotquay.Cli;
using Dotquay.Core.Progress;
using Xunit;

namespace Dotquay.Tests.Cli;

/// <summary>
/// Covers the spinner standing down while something else needs the terminal.
/// </summary>
/// <remarks>
/// The bug this exists for, found by hand on a real console (v0.20): a prompt raised mid-resolve
/// was written straight onto the line the spinner owned, so the question read
/// <c>| checking where 'SproutyDialogs' lives in its repository'SproutyDialogs' isn't at …</c>, and
/// the frames kept repainting over it while the user was reading. Invisible to every other test in
/// this suite, because a spinner that draws needs a terminal and a test host has none — so the
/// reporter gets a seam that draws on demand against a substituted <see cref="Console.Error"/>.
/// <para>
/// <see cref="Console.SetError"/> is process-global, so this runs in a non-parallel collection and
/// puts it back.
/// </para>
/// </remarks>
[Collection(nameof(ConsoleProgressReporterTests))]
[CollectionDefinition(nameof(ConsoleProgressReporterTests), DisableParallelization = true)]
public class ConsoleProgressReporterTests {
    private static void WithCapturedError(Action<ConsoleProgressReporter, StringWriter> body) {
        var original = Console.Error;
        var captured = new StringWriter();

        using var reporter = ConsoleProgressReporter.CreateForTests();

        try {
            Console.SetError(captured);
            body(reporter, captured);
        } finally {
            Console.SetError(original);
        }
    }

    [Fact]
    public void Report_DrawsTheStep() => WithCapturedError((reporter, captured) => {
        reporter.Report(new ProgressUpdate("fetching vbeami"));

        Assert.Contains("fetching vbeami", captured.ToString());
    });

    [Fact]
    public void Pause_ClearsTheLineAndStopsDrawing() => WithCapturedError((reporter, captured) => {
        reporter.Report(new ProgressUpdate("checking where 'SproutyDialogs' lives"));

        using (ConsoleProgressReporter.Pause()) {
            var afterPause = captured.ToString().Length;

            // The line is not merely abandoned, it is wiped: a question printed over a half-erased
            // spinner frame is the bug this guards, one character shorter.
            Assert.EndsWith("\r", captured.ToString());

            reporter.Report(new ProgressUpdate("resolving the dependency graph"));
            Assert.Equal(afterPause, captured.ToString().Length);
        }
    });

    [Fact]
    public void Resume_PicksUpTheCurrentStep() => WithCapturedError((reporter, captured) => {
        reporter.Report(new ProgressUpdate("checking where 'SproutyDialogs' lives"));

        using (ConsoleProgressReporter.Pause()) {
            // Recorded while quiet, so what comes back is where the work actually is rather than
            // where it was when the question was asked.
            reporter.Report(new ProgressUpdate("resolving the dependency graph"));
        }

        var drawn = captured.ToString();
        Assert.Contains("resolving the dependency graph", drawn);
        Assert.DoesNotContain(
            "checking where 'SproutyDialogs' lives",
            drawn[drawn.LastIndexOf("resolving the dependency graph", StringComparison.Ordinal)..]);
    });

    [Fact]
    public void Pause_Nested_StaysQuietUntilTheOuterScopeCloses() =>
        WithCapturedError((reporter, captured) => {
            reporter.Report(new ProgressUpdate("fetching"));

            using (ConsoleProgressReporter.Pause()) {
                using (ConsoleProgressReporter.Pause()) {
                }

                var afterInner = captured.ToString().Length;
                reporter.Report(new ProgressUpdate("still working"));

                Assert.Equal(afterInner, captured.ToString().Length);
            }
        });

    [Fact]
    public void Pause_WithNothingDrawing_IsSafe() {
        // --json, CI, redirected stderr: Create hands back the null reporter, so there is no active
        // one to find. Every prompt opens a scope unconditionally, so this is the common path.
        using var scope = ConsoleProgressReporter.Pause();
    }

    [Fact]
    public void Pause_AfterDisposal_IsSafe() {
        var reporter = ConsoleProgressReporter.CreateForTests();
        reporter.Dispose();

        using var scope = ConsoleProgressReporter.Pause();
    }

    [Fact]
    public void Create_ForJson_DrawsNothing() =>
        Assert.Same(NullProgressReporter.Instance, ConsoleProgressReporter.Create(json: true));
}
