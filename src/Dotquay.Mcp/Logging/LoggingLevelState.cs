using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Dotquay.Mcp.Logging;

/// <summary>
/// Tracks the logging level a client last requested via <c>logging/setLevel</c> (design doc §6).
/// </summary>
/// <remarks>
/// <c>McpServer.LoggingLevel</c> is read-only from application code — confirmed by reflection against
/// <c>ModelContextProtocol</c> 2.2.0: the property has a getter only, no setter — so a
/// <c>WithSetLoggingLevelHandler</c> handler has nowhere else to record what the client asked for.
/// This is that record, shared between the handler (which writes it) and <see cref="RequestLog"/>
/// (which reads it to decide whether an event is worth pushing to the client as
/// <c>notifications/message</c>).
/// </remarks>
public sealed class LoggingLevelState
{
    private readonly object _lock = new();
    private LoggingLevel? _current;

    /// <summary>The <c>logging/setLevel</c> handler itself — wired directly into
    /// <c>WithSetLoggingLevelHandler</c> in <c>McpServerBuilder</c>.</summary>
    public ValueTask<EmptyResult> HandleSetLevelAsync(RequestContext<SetLevelRequestParams> context, CancellationToken cancellationToken)
    {
        if (context.Params is { } parameters)
        {
            lock (_lock)
            {
                _current = parameters.Level;
            }
        }

        return ValueTask.FromResult(new EmptyResult());
    }

    /// <summary>True once the client has asked (via <c>logging/setLevel</c>) for <paramref
    /// name="level"/> or anything less severe — the MCP spec's "at or above the requested level" rule.
    /// <see cref="LoggingLevel"/>'s declared order (Debug .. Emergency) is already the syslog severity
    /// order the spec uses, so a plain enum comparison is correct here without a separate ranking
    /// table.</summary>
    public bool ShouldForward(LoggingLevel level)
    {
        lock (_lock)
        {
            return _current is { } threshold && level >= threshold;
        }
    }
}
