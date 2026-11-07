using Dotquay.Cli.Diagnostics;
using Dotquay.Core.Diagnostics;
using Xunit;

namespace Dotquay.Tests.Cli;

public sealed class StderrDiagnosticSinkTests {
    [Fact]
    public void Lines_carry_an_aligned_timestamp_and_category() {
        var text = StderrDiagnosticSink.Format(
            new DiagnosticEvent(DiagnosticLevel.Verbose, "git", "$ git fetch"),
            TimeSpan.FromMilliseconds(1234.5));

        Assert.Equal("[   1.235s] git      | $ git fetch", text);
    }

    [Fact]
    public void Continuation_lines_sit_under_the_first() {
        var text = StderrDiagnosticSink.Format(
            new DiagnosticEvent(DiagnosticLevel.Trace, "git", "stdout| a\r\nstdout| b\n"),
            TimeSpan.Zero);

        var lines = text.Split(Environment.NewLine);
        Assert.Equal(2, lines.Length);
        Assert.Equal("[   0.000s] git      | stdout| a", lines[0]);
        Assert.Equal("                     | stdout| b", lines[1]);
        Assert.Equal(lines[0].IndexOf('|'), lines[1].IndexOf('|'));
    }

    [Fact]
    public void Long_categories_are_cut_to_keep_the_columns() {
        var text = StderrDiagnosticSink.Format(
            new DiagnosticEvent(DiagnosticLevel.Verbose, "averyverylongcategory", "x"),
            TimeSpan.Zero);

        Assert.Equal("[   0.000s] averyver | x", text);
    }

    [Fact]
    public void Write_goes_to_the_writer_and_pauses_progress_around_each_line() {
        var writer = new StringWriter();
        var pauses = 0;
        var sink = new StderrDiagnosticSink(
            DiagnosticLevel.Verbose,
            writer,
            color: false,
            pauseProgress: () => {
                pauses++;
                return new Scope();
            },
            clock: () => TimeSpan.FromSeconds(2));

        sink.Write(new DiagnosticEvent(DiagnosticLevel.Verbose, "http", "GET x -> 200"));
        sink.Write(new DiagnosticEvent(DiagnosticLevel.Verbose, "http", "GET y -> 404"));

        Assert.Equal(
            "[   2.000s] http     | GET x -> 200" + Environment.NewLine
            + "[   2.000s] http     | GET y -> 404" + Environment.NewLine,
            writer.ToString());
        Assert.Equal(2, pauses);
    }

    [Fact]
    public void Off_creates_the_null_sink() {
        Assert.Same(NullDiagnosticSink.Instance, StderrDiagnosticSink.Create(DiagnosticLevel.Off));
    }

    private sealed class Scope : IDisposable {
        public void Dispose() {
        }
    }
}
