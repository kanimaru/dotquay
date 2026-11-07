using System.Diagnostics;
using ModelContextProtocol.Server;

namespace Dotquay.Mcp.Logging;

/// <summary>
/// Builds the one <see cref="McpRequestFilter{TParams, TResult}"/> shape every request kind uses
/// (design doc §6): time the call and log its outcome.
/// </summary>
/// <remarks>
/// One generic factory rather than one hand-written filter per request kind: every
/// <c>Add*Filter</c> registration in <c>McpServerBuilder</c> differs only in which two type
/// parameters and which method name it passes here, so writing it six times would be the exact
/// duplication <see cref="Tools.McpToolBase"/> already exists to avoid on the tool side.
/// </remarks>
internal static class RequestLoggingFilters
{
    public static McpRequestFilter<TParams, TResult> Create<TParams, TResult>(string method, RequestLog log) =>
        next => async (context, cancellationToken) =>
        {
            var stopwatch = Stopwatch.StartNew();
            var requestId = context.JsonRpcRequest?.Id.ToString() ?? "?";

            try
            {
                var result = await next(context, cancellationToken);
                log.WriteSuccess(requestId, method, stopwatch.Elapsed);
                return result;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.WriteFailure(requestId, method, stopwatch.Elapsed, ex);
                throw;
            }
        };
}
