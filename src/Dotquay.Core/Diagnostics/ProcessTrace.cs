using System.Diagnostics;
using System.Text;

namespace Dotquay.Core.Diagnostics;

/// <summary>
/// The one shape every subprocess dotquay starts is logged in: the command line before it runs,
/// then its exit code and duration, then (at <see cref="DiagnosticLevel.Trace"/>) what it printed.
/// </summary>
/// <remarks>
/// <para>
/// One type rather than a few log lines pasted into each <c>Process.Start</c> site, because the
/// sites (<c>ProcessGitClient</c>, <c>ProcessPatchTool</c>, <c>ProcessDotnetCliRunner</c>,
/// <c>SystemFileSystem</c>'s junction, <c>ProcessExecutableProbe</c>) were each written separately and
/// would drift: one forgetting to redact, another logging the duration in seconds.
/// </para>
/// <para>
/// The command line is written the way a person would retype it into a shell - arguments with
/// spaces quoted - because the most common use of a verbose log is copying the failing git command
/// out of it and running it by hand.
/// </para>
/// </remarks>
public sealed class ProcessTrace {
    /// <summary>Per stream. A <c>git ls-remote</c> against a busy repo prints thousands of refs; the
    /// head of it is what diagnoses a problem and the rest buries every other line.</summary>
    internal const int MaxCapturedCharacters = 4000;

    private static readonly ProcessTrace Disabled = new(string.Empty, enabled: false);

    private readonly string _category;
    private readonly bool _enabled;
    private readonly long _started;

    private ProcessTrace(string category, bool enabled) {
        _category = category;
        _enabled = enabled;
        _started = Stopwatch.GetTimestamp();
    }

    /// <summary>Logs the command about to run and starts its clock.</summary>
    /// <param name="fileName">The executable as passed to <see cref="ProcessStartInfo"/>.</param>
    /// <param name="arguments">The <see cref="ProcessStartInfo.ArgumentList"/>, redacted here.</param>
    /// <param name="workingDirectory">Null when the child inherits dotquay's own.</param>
    /// <param name="category">Defaults to the executable's file name (<c>git</c>, <c>dotnet</c>).</param>
    public static ProcessTrace Start(
        string fileName, IEnumerable<string> arguments, string? workingDirectory, string? category = null) {
        if (!DiagnosticLog.IsEnabled(DiagnosticLevel.Verbose)) {
            return Disabled;
        }

        var trace = new ProcessTrace(category ?? CategoryFor(fileName), enabled: true);
        var where = string.IsNullOrEmpty(workingDirectory) ? string.Empty : $"  (in {workingDirectory})";
        DiagnosticLog.Write(DiagnosticLevel.Verbose, trace._category, $"$ {FormatCommandLine(fileName, arguments)}{where}");
        return trace;
    }

    /// <summary>Logs how the process ended and, at trace level, what it printed.</summary>
    public void Completed(int exitCode, string? standardOutput = null, string? standardError = null) {
        if (!_enabled) {
            return;
        }

        var elapsed = DiagnosticLog.FormatDuration(Stopwatch.GetElapsedTime(_started));
        DiagnosticLog.Write(DiagnosticLevel.Verbose, _category, $"exit {exitCode} after {elapsed}");

        WriteStream("stdout", standardOutput);
        WriteStream("stderr", standardError);
    }

    /// <summary>Logs a process that never started, or was abandoned (timeout, cancellation).</summary>
    public void Failed(Exception exception) {
        if (!_enabled) {
            return;
        }

        var elapsed = DiagnosticLog.FormatDuration(Stopwatch.GetElapsedTime(_started));
        DiagnosticLog.Write(
            DiagnosticLevel.Verbose,
            _category,
            $"failed after {elapsed}: {exception.GetType().Name}: {SecretRedactor.Redact(exception.Message)}");
    }

    /// <summary>A shell-pasteable rendering of the command, with secrets masked.</summary>
    internal static string FormatCommandLine(string fileName, IEnumerable<string> arguments) {
        var builder = new StringBuilder(Quote(fileName));

        foreach (var argument in SecretRedactor.RedactArguments(arguments)) {
            builder.Append(' ').Append(Quote(argument));
        }

        return builder.ToString();
    }

    /// <summary>Indents a captured stream under its process and caps it at
    /// <see cref="MaxCapturedCharacters"/>.</summary>
    internal static string? FormatCapturedStream(string label, string? content) {
        if (string.IsNullOrWhiteSpace(content)) {
            return null;
        }

        var text = content.TrimEnd();
        var truncated = text.Length > MaxCapturedCharacters;
        if (truncated) {
            text = text[..MaxCapturedCharacters];
        }

        var builder = new StringBuilder();
        foreach (var line in SecretRedactor.Redact(text).ReplaceLineEndings("\n").Split('\n')) {
            if (builder.Length > 0) {
                builder.Append('\n');
            }

            builder.Append(label).Append("| ").Append(line);
        }

        if (truncated) {
            builder.Append('\n').Append(label).Append("| ... (")
                .Append(content.Length - MaxCapturedCharacters).Append(" more characters not shown)");
        }

        return builder.ToString();
    }

    private void WriteStream(string label, string? content) {
        if (!DiagnosticLog.IsEnabled(DiagnosticLevel.Trace)) {
            return;
        }

        var formatted = FormatCapturedStream(label, content);
        if (formatted is not null) {
            DiagnosticLog.Trace(_category, formatted);
        }
    }

    private static string CategoryFor(string fileName) {
        // Split on both separators by hand: Path treats a backslash as an ordinary character off
        // Windows, and an executable path from a Windows-flavoured config should name the same
        // category wherever dotquay runs.
        var name = Path.GetFileNameWithoutExtension(fileName[(fileName.LastIndexOfAny(['/', '\\']) + 1)..]);
        return string.IsNullOrEmpty(name) ? "process" : name.ToLowerInvariant();
    }

    private static string Quote(string argument) {
        if (argument.Length == 0) {
            return "\"\"";
        }

        return argument.Any(c => char.IsWhiteSpace(c) || c == '"')
            ? "\"" + argument.Replace("\"", "\\\"") + "\""
            : argument;
    }
}
