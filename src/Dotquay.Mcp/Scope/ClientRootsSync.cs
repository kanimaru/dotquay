using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Dotquay.Mcp.Scope;

/// <summary>
/// Requests the client's <c>roots</c> (design doc §4.3) once per session and folds them into
/// <see cref="IProjectScope"/>, re-syncing whenever the client says its roots changed.
/// </summary>
/// <remarks>
/// <para>
/// The SDK has no dedicated "connection established" hook to call this from — confirmed by reflection
/// against <c>ModelContextProtocol</c> 2.2.0: there is no <c>Initializ*</c>/<c>Handshake*</c>/
/// <c>Connect*</c>/<c>Started*</c> extension point on <c>IMcpServerBuilder</c>. The integration point
/// used here instead is the first tool call of the session: every call already funnels through
/// <see cref="Tools.McpToolBase.CallAsync"/> with a <c>RequestContext</c> carrying <c>.Server</c>, and
/// a <see cref="SemaphoreSlim"/>-guarded lazy sync makes "on first call" behave the same as "on
/// connect" for every practical purpose here — the first project-scoped decision this server makes is
/// the first one an agent asks it to make.
/// </para>
/// <para>
/// One instance per server process (constructed once in <c>McpServerBuilder.Configure</c> and shared
/// by every tool), not one per call — the whole point is to ask the client exactly once, then again
/// only when it says something changed.
/// </para>
/// </remarks>
public sealed class ClientRootsSync(IProjectScope scope)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _subscribed;
    private bool _syncedOnce;

    /// <summary>Called from every tool call before it resolves <c>project</c>. A no-op after the
    /// first successful (or first attempted-and-failed) sync, and a no-op forever when the client
    /// never advertised the capability at all.</summary>
    public async ValueTask EnsureSyncedAsync(McpServer server, CancellationToken cancellationToken)
    {
        if (server.ClientCapabilities?.Roots is null)
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!_subscribed)
            {
                // Registered from inside the same gate that guards the first sync, so a
                // notification racing the very first call can never be lost or double-applied.
                _ = server.RegisterNotificationHandler(
                    NotificationMethods.RootsListChangedNotification,
                    (_, notificationCancellationToken) => ResyncAsync(server, notificationCancellationToken));
                _subscribed = true;
            }

            if (!_syncedOnce)
            {
                await SyncAsync(server, cancellationToken);
                _syncedOnce = true;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async ValueTask ResyncAsync(McpServer server, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await SyncAsync(server, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async ValueTask SyncAsync(McpServer server, CancellationToken cancellationToken)
    {
        ListRootsResult result;

        try
        {
            result = await server.RequestRootsAsync(new ListRootsRequestParams(), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A client that advertised the capability but then fails or refuses the actual request
            // leaves the scope exactly as it was — the server's own --project roots still apply, so
            // this is a client-side deficiency to shrug off, not a reason to fail whichever tool call
            // triggered the sync.
            return;
        }

        var paths = result.Roots
            .Select(root => TryToLocalPath(root.Uri))
            .Where(path => path is not null)
            .Select(path => path!);

        scope.AddRoots(paths);
    }

    /// <summary>A root's <c>uri</c> is spec'd as a URI, almost always <c>file://</c> for a client
    /// spawning a local process; anything else names a place this filesystem-based scope has no way
    /// to allow, so it is skipped rather than guessed at.</summary>
    private static string? TryToLocalPath(string uri) =>
        Uri.TryCreate(uri, UriKind.Absolute, out var parsed) && parsed.IsFile ? parsed.LocalPath : null;
}
