using Dotquay.Core.Diagnostics;
using Dotquay.Tests.Support;
using Xunit;

namespace Dotquay.Tests.Diagnostics;

public sealed class ProcessTraceTests
{
    [Fact]
    public void Command_line_is_shell_pasteable_and_redacted()
    {
        var line = ProcessTrace.FormatCommandLine(
            "git", ["-C", @"C:\My Projects\game", "fetch", "https://u:pw@h/r.git", "--token", "abc", "say \"hi\"", ""]);

        Assert.Equal(
            "git -C \"C:\\My Projects\\game\" fetch https://u:***@h/r.git --token *** \"say \\\"hi\\\"\" \"\"",
            line);
    }

    [Fact]
    public void Start_and_Completed_log_command_exit_code_and_duration_at_verbose()
    {
        var sink = RecordingDiagnosticSink.Install(out var scope, DiagnosticLevel.Verbose);
        using (scope)
        {
            var trace = ProcessTrace.Start("git", ["ls-remote", "origin"], @"C:\work");
            trace.Completed(128, "refs", "fatal: nope");
        }

        var lines = sink.Messages("git").ToList();
        Assert.Equal(2, lines.Count);
        Assert.Equal(@"$ git ls-remote origin  (in C:\work)", lines[0]);
        Assert.Matches(@"^exit 128 after \d+ ms$", lines[1]);
        Assert.DoesNotContain("fatal", sink.AllText); // captured output is trace-only
    }

    [Fact]
    public void Trace_level_adds_the_captured_streams_redacted_and_labelled()
    {
        var sink = RecordingDiagnosticSink.Install(out var scope);
        using (scope)
        {
            var trace = ProcessTrace.Start("git", ["fetch"], workingDirectory: null);
            trace.Completed(0, "line one\r\nline two\n", "remote: https://u:pw@h/r.git\n");
        }

        var lines = sink.Messages("git").ToList();
        Assert.Equal("$ git fetch", lines[0]);
        Assert.Contains("stdout| line one\nstdout| line two", lines);
        Assert.Contains("stderr| remote: https://u:***@h/r.git", lines);
    }

    [Fact]
    public void Empty_streams_are_not_logged()
    {
        var sink = RecordingDiagnosticSink.Install(out var scope);
        using (scope)
        {
            ProcessTrace.Start("git", ["status"], null).Completed(0, "", "  \n");
        }

        Assert.Equal(2, sink.Events.Count);
    }

    [Fact]
    public void Long_output_is_capped_and_says_how_much_was_cut()
    {
        var content = new string('x', ProcessTrace.MaxCapturedCharacters + 123);

        var formatted = ProcessTrace.FormatCapturedStream("stdout", content)!;

        Assert.EndsWith("stdout| ... (123 more characters not shown)", formatted);
        Assert.True(formatted.Length < content.Length);
    }

    [Fact]
    public void Category_defaults_to_the_executable_name_and_can_be_overridden()
    {
        var sink = RecordingDiagnosticSink.Install(out var scope, DiagnosticLevel.Verbose);
        using (scope)
        {
            ProcessTrace.Start(@"C:\tools\Dotnet.EXE", ["restore"], null).Completed(0);
            ProcessTrace.Start("git", ["apply"], null, category: "patch").Completed(0);
        }

        Assert.Equal(2, sink.Messages("dotnet").Count());
        Assert.Equal(2, sink.Messages("patch").Count());
    }

    [Fact]
    public void Failed_logs_the_exception_type_and_redacted_message()
    {
        var sink = RecordingDiagnosticSink.Install(out var scope, DiagnosticLevel.Verbose);
        using (scope)
        {
            ProcessTrace.Start("git", ["x"], null).Failed(new InvalidOperationException("bad https://u:pw@h"));
        }

        Assert.Matches(@"^failed after \d+ ms: InvalidOperationException: bad https://u:\*\*\*@h$", sink.Messages("git").Last());
    }

    [Fact]
    public void Nothing_is_logged_or_timed_when_off()
    {
        var trace = ProcessTrace.Start("git", ["fetch"], null);
        trace.Completed(0, "out", "err");
        trace.Failed(new Exception());

        Assert.Same(trace, ProcessTrace.Start("dotnet", [], null)); // the shared disabled instance
    }
}
