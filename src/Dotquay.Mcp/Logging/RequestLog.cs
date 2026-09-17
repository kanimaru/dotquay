using System.Text.Json;
using Dotquay.Mcp.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Dotquay.Mcp.Logging;

/// <summary>
/// The local, plain-text request trace <c>--log &lt;file&gt;</c> writes (design doc §6): one line per
/// request with its id, method, duration, and — on failure — the first line of the exception; plus,
/// when the client asked for it via <c>logging/setLevel</c>, the matching <c>notifications/message</c>
/// push.
/// </summary>
/// <remarks>
/// Two destinations sharing one class rather than two: the local trace and the client-facing
/// notification are triggered by the exact same event (a request that failed), so keeping them
/// together means <see cref="RequestLoggingFilters"/> has exactly one thing to call on failure,
/// instead of remembering to call both.
/// </remarks>
public sealed class RequestLog(TextWriter writer, bool ownsWriter, LoggingLevelState levelState) : IDisposable
{
    private readonly object _lock = new();

    public void WriteSuccess(string requestId, string method, TimeSpan duration) =>
        WriteLine($"{DateTimeOffset.UtcNow:O} id={requestId} method={method} duration_ms={duration.TotalMilliseconds:F1} ok");

    public void WriteFailure(string requestId, string method, TimeSpan duration, Exception exception) =>
        WriteLine(
            $"{DateTimeOffset.UtcNow:O} id={requestId} method={method} duration_ms={duration.TotalMilliseconds:F1} "
            + $"error={FirstLine(exception.Message)}");

    /// <summary>Pushes the failure to the client as <c>notifications/message</c> — only when it
    /// already asked to see events at this severity via <c>logging/setLevel</c>; otherwise a no-op,
    /// matching <c>McpProgressReporter</c>'s "pay nothing unless asked" rule.</summary>
    public async Task NotifyClientAsync(McpServer server, Exception exception, CancellationToken cancellationToken)
    {
        if (!levelState.ShouldForward(LoggingLevel.Error))
        {
            return;
        }

        try
        {
            await server.SendNotificationAsync(
                NotificationMethods.LoggingMessageNotification,
                new LoggingMessageNotificationParams
                {
                    Level = LoggingLevel.Error,
                    Logger = "dotquay",
                    // Source-generated, not JsonSerializer.SerializeToElement<string>(...) — that
                    // overload is reflection-based and is exactly what the trimmer breaks under
                    // NativeAOT (see DotquayMcpJsonContext's remarks on why `string` is registered
                    // there).
                    Data = JsonSerializer.SerializeToElement(FirstLine(exception.Message), DotquayMcpJsonContext.Default.String),
                },
                McpJsonUtilities.DefaultOptions,
                cancellationToken);
        }
        catch
        {
            // A lost log notification is never a reason to fail the request it was describing — the
            // same rule McpProgressReporter applies to a lost progress notification.
        }
    }

    private void WriteLine(string line)
    {
        lock (_lock)
        {
            writer.WriteLine(line);
            writer.Flush();
        }
    }

    private static string FirstLine(string text) => text.Split('\n', 2)[0].TrimEnd('\r');

    /// <summary>Disposes the underlying writer only when this <see cref="RequestLog"/> owns it — the
    /// stderr fallback (no <c>--log</c> file given) must never close <see cref="Console.Error"/> for
    /// the rest of the process.</summary>
    public void Dispose()
    {
        if (ownsWriter)
        {
            writer.Dispose();
        }
    }
}
