using Dotquay.Core.Diagnostics;
using Dotquay.Tests.Support;
using Xunit;

namespace Dotquay.Tests.Diagnostics;

public sealed class DiagnosticLogTests {
    [Fact]
    public void Off_by_default() {
        Assert.Same(NullDiagnosticSink.Instance, DiagnosticLog.Current);
        Assert.False(DiagnosticLog.IsEnabled(DiagnosticLevel.Verbose));
        Assert.False(DiagnosticLog.IsEnabled(DiagnosticLevel.Trace));
    }

    [Fact]
    public void Off_is_never_enabled_even_for_a_sink_that_wants_everything() {
        using var scope = DiagnosticLog.Use(new RecordingDiagnosticSink(DiagnosticLevel.Trace));

        Assert.False(DiagnosticLog.IsEnabled(DiagnosticLevel.Off));
    }

    [Fact]
    public void Use_routes_to_the_sink_and_disposing_restores_the_previous_one() {
        var outer = new RecordingDiagnosticSink();
        var inner = new RecordingDiagnosticSink();

        using (DiagnosticLog.Use(outer)) {
            using (DiagnosticLog.Use(inner)) {
                DiagnosticLog.Verbose("t", "to inner");
            }

            DiagnosticLog.Verbose("t", "to outer");
        }

        DiagnosticLog.Verbose("t", "to nobody");

        Assert.Equal(["to inner"], inner.Messages("t"));
        Assert.Equal(["to outer"], outer.Messages("t"));
        Assert.Same(NullDiagnosticSink.Instance, DiagnosticLog.Current);
    }

    [Fact]
    public void Disposing_a_scope_twice_does_not_clobber_a_later_scope() {
        var first = DiagnosticLog.Use(new RecordingDiagnosticSink());
        first.Dispose();

        var second = new RecordingDiagnosticSink();
        using (DiagnosticLog.Use(second)) {
            first.Dispose();
            DiagnosticLog.Verbose("t", "still here");
        }

        Assert.Single(second.Events);
    }

    [Fact]
    public void A_verbose_sink_drops_trace_lines() {
        var sink = RecordingDiagnosticSink.Install(out var scope, DiagnosticLevel.Verbose);
        using (scope) {
            DiagnosticLog.Verbose("t", "kept");
            DiagnosticLog.Trace("t", "dropped");
        }

        Assert.Equal(["kept"], sink.Messages("t"));
        Assert.Equal(DiagnosticLevel.Verbose, sink.Events[0].Level);
    }

    [Fact]
    public void Interpolated_holes_are_not_evaluated_when_the_level_is_off() {
        var evaluations = 0;
        string Expensive() {
            evaluations++;
            return "x";
        }

        DiagnosticLog.Write(DiagnosticLevel.Verbose, "t", $"value {Expensive()}");

        using (DiagnosticLog.Use(new RecordingDiagnosticSink(DiagnosticLevel.Verbose))) {
            DiagnosticLog.Write(DiagnosticLevel.Trace, "t", $"value {Expensive()}");
        }

        Assert.Equal(0, evaluations);
    }

    [Fact]
    public void Interpolated_messages_are_formatted_when_enabled() {
        var sink = RecordingDiagnosticSink.Install(out var scope);
        using (scope) {
            var count = 3;
            DiagnosticLog.Write(DiagnosticLevel.Verbose, "t", $"{count} addon(s), {1.5:0.00} s, [{"x",3}]");
        }

        Assert.Equal(["3 addon(s), 1.50 s, [  x]"], sink.Messages("t"));
    }

    [Fact]
    public async Task The_sink_flows_into_awaited_and_spawned_work() {
        var sink = RecordingDiagnosticSink.Install(out var scope);
        using (scope) {
            await Task.Yield();
            DiagnosticLog.Verbose("t", "after await");
            await Task.Run(() => DiagnosticLog.Verbose("t", "in Task.Run"));
        }

        Assert.Equal(["after await", "in Task.Run"], sink.Messages("t"));
    }

    [Fact]
    public async Task Concurrent_flows_each_keep_their_own_sink() {
        async Task<RecordingDiagnosticSink> RunAsync(string name) {
            var sink = RecordingDiagnosticSink.Install(out var scope);
            using (scope) {
                for (var i = 0; i < 20; i++) {
                    DiagnosticLog.Verbose("t", name);
                    await Task.Yield();
                }
            }

            return sink;
        }

        var results = await Task.WhenAll(Task.Run(() => RunAsync("a")), Task.Run(() => RunAsync("b")));

        Assert.All(results[0].Messages("t"), m => Assert.Equal("a", m));
        Assert.All(results[1].Messages("t"), m => Assert.Equal("b", m));
        Assert.Equal(20, results[0].Events.Count);
        Assert.Equal(20, results[1].Events.Count);
    }

    [Fact]
    public void Measure_logs_the_label_with_a_duration_once() {
        var sink = RecordingDiagnosticSink.Install(out var scope);
        using (scope) {
            var measurement = DiagnosticLog.Measure(DiagnosticLevel.Verbose, "t", "resolved graph");
            measurement.Dispose();
            measurement.Dispose();
        }

        var message = Assert.Single(sink.Messages("t"));
        Assert.Matches(@"^resolved graph \(\d+ ms\)$", message);
    }

    [Fact]
    public void Measure_is_a_shared_no_op_when_off() {
        Assert.Same(
            DiagnosticLog.Measure(DiagnosticLevel.Verbose, "t", "a"),
            DiagnosticLog.Measure(DiagnosticLevel.Trace, "t", "b"));
    }

    [Theory]
    [InlineData(0.4, "0 ms")]
    [InlineData(12.6, "13 ms")]
    [InlineData(2500, "2500 ms")]
    public void Durations_are_whole_milliseconds(double milliseconds, string expected) {
        Assert.Equal(expected, DiagnosticLog.FormatDuration(TimeSpan.FromMilliseconds(milliseconds)));
    }
}
