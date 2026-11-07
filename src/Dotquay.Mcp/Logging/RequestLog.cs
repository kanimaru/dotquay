using Dotquay.Core.Diagnostics;

namespace Dotquay.Mcp.Logging;

/// <summary>
/// The local, plain-text request trace <c>--log &lt;file&gt;</c> writes (design doc §6): one line per
/// request with its id, method, duration, and — on failure — the first line of the exception.
/// </summary>
/// <remarks>
/// This trace is the server's only log output. It goes to the <c>--log</c> file or, without one, to
/// stderr — never to the client over MCP: the protocol's Logging feature (<c>logging/setLevel</c>,
/// <c>notifications/message</c>) is deprecated as of specification version 2026-07-28 (SEP-2577),
/// whose migration note is "log to stderr (stdio)". A failed request still reaches the client, as the
/// error result of that request itself.
/// </remarks>
public sealed class RequestLog(TextWriter writer, bool ownsWriter) : IDisposable {
    private readonly object _lock = new();

    public void WriteSuccess(string requestId, string method, TimeSpan duration) =>
        WriteLine($"{DateTimeOffset.UtcNow:O} id={requestId} method={method} duration_ms={duration.TotalMilliseconds:F1} ok");

    public void WriteFailure(string requestId, string method, TimeSpan duration, Exception exception) =>
        WriteLine(
            $"{DateTimeOffset.UtcNow:O} id={requestId} method={method} duration_ms={duration.TotalMilliseconds:F1} "
            + $"error={FirstLine(exception.Message)}");

    /// <summary>Disposes the underlying writer only when this <see cref="RequestLog"/> owns it — the
    /// stderr fallback (no <c>--log</c> file given) must never close <see cref="Console.Error"/> for
    /// the rest of the process.</summary>
    public void Dispose() {
        if (ownsWriter) {
            writer.Dispose();
        }
    }

    private void WriteLine(string line) {
        lock (_lock) {
            writer.WriteLine(line);
            writer.Flush();
        }
    }

    /// <summary>The first line of an exception message, credentials masked: the trace is often
    /// shared in a bug report, and a git or registry error can carry a token in its URL.</summary>
    private static string FirstLine(string text) => SecretRedactor.Redact(text.Split('\n', 2)[0].TrimEnd('\r'));
}
