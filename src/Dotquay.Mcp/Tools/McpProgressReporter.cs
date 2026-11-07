using Dotquay.Core.Progress;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Dotquay.Mcp.Tools;

/// <summary>
/// Forwards <c>Dotquay.Core</c>'s synchronous progress callback to the MCP client via
/// <c>notifications/progress</c>, for the tool calls that do slow, fetch-driven work
/// (<c>dotquay_outdated</c>, and <c>dotquay_status</c>/<c>dotquay_graph</c> when asked to
/// explain/resolve).
/// </summary>
/// <remarks>
/// <para>
/// Silently does nothing when the call carried no <see cref="ProgressToken"/> — most MCP clients
/// only attach one when they intend to render it, so a caller that never asked for progress pays
/// nothing for this reporter existing.
/// </para>
/// <para>
/// Fire-and-forget, for the same reason <c>Dotquay.Cli.UpdateNotice</c>'s own background refresh
/// is: <see cref="IProgressReporter.Report"/> is a synchronous call from deep inside a resolve
/// loop, while the SDK's notification send is async — blocking that loop on a round trip to the
/// client would make reporting progress slower than not reporting it at all. Every failure is
/// swallowed for the same reason <c>UpdateNotice</c> swallows its own: a progress update that
/// couldn't be delivered (the client hung up, the transport is mid-teardown) is not a reason to
/// fail the tool call it was describing.
/// </para>
/// </remarks>
internal sealed class McpProgressReporter(McpServer server, ProgressToken? progressToken)
    : IProgressReporter {
    public void Report(ProgressUpdate update) {
        if (progressToken is not { } token) {
            return;
        }

        var value = new ProgressNotificationValue {
            Progress = update.Completed ?? 0,
            Total = update.Total,
            Message = update.Message,
        };

        _ = FireAndForgetAsync(token, value);
    }

    private async Task FireAndForgetAsync(ProgressToken token, ProgressNotificationValue value) {
        try {
            await server.NotifyProgressAsync(token, value,
                cancellationToken: CancellationToken.None);
        } catch {
            // See the type's remarks: a lost progress notification is never a reason to fail the
            // tool call it was describing.
        }
    }
}
