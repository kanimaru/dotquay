using Dotquay.Core.Diagnostics;
using Dotquay.Core.Git;
using Dotquay.Core.Manifest;

namespace Dotquay.Core.Resolve;

/// <summary>
/// Starts resolving addons in the background, a few at a time, so their remote lookups are already
/// done - or under way - by the time the graph walk asks for them one by one.
/// </summary>
/// <remarks>
/// <para>
/// The walk is breadth-first and sequential on purpose: each addon's package manifest decides what is
/// enqueued next, and the order decides discovery. That order is kept exactly. What is moved off the
/// critical path is only the waiting: every <c>git ls-remote</c> costs a full ssh handshake (~0.5 s to
/// gitlab.kemomi.de; Windows OpenSSH has no connection sharing), and four of them back to back were
/// most of a no-op <c>update</c>.
/// </para>
/// <para>
/// It is a pure warm-up and has no results of its own. It only works because the resolver it drives
/// sits on a <see cref="MemoizingGitClient"/>: the walk's own, later call for the same remote receives
/// the very task started here. A prefetch failure is therefore swallowed, not lost - the walk's call
/// observes the same remembered failure and reports it exactly where it always did.
/// </para>
/// <para>
/// Concurrency is capped (<see cref="DefaultMaxConcurrency"/>) because OpenSSH servers drop
/// unauthenticated connections beyond <c>MaxStartups</c> (10 by default) at random, and a project
/// whose addons all live on one host would otherwise open them all at once.
/// </para>
/// </remarks>
internal sealed class RefPrefetcher : IAsyncDisposable {
    internal const int DefaultMaxConcurrency = 4;

    private readonly AddonResolver _resolver;
    private readonly SemaphoreSlim _slots;
    private readonly CancellationToken _cancellationToken;
    private readonly HashSet<(string Git, AddonSourceKind Kind, string? Branch)> _started = [];
    private readonly List<Task> _inFlight = [];

    internal RefPrefetcher(AddonResolver resolver, CancellationToken cancellationToken, int maxConcurrency = DefaultMaxConcurrency) {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxConcurrency, 1);

        _resolver = resolver;
        _slots = new SemaphoreSlim(maxConcurrency, maxConcurrency);
        _cancellationToken = cancellationToken;
    }

    /// <summary>Starts resolving <paramref name="spec"/> in the background, unless it needs no remote
    /// lookup or the same lookup was already started. Never blocks and never throws.</summary>
    /// <remarks>Called only from the walk's own (single) logical thread, so the bookkeeping needs no lock.</remarks>
    internal void Warm(AddonSpec spec) {
        // A rev pin is answered from the manifest alone; there is nothing to warm.
        if (spec.SourceKind == AddonSourceKind.Rev) {
            return;
        }

        // Keyed on the remote query, not the spec: two version constraints on one repository share one
        // ls-remote, and warming it twice would only hold a second slot for a memo hit.
        var query = (spec.Git, spec.SourceKind, spec.SourceKind == AddonSourceKind.Branch ? spec.Branch : null);
        if (!_started.Add(query)) {
            return;
        }

        // Task.Run so starting git (Process.Start is synchronous and not free on Windows) happens off
        // the walk's thread. The ambient diagnostic sink is an AsyncLocal and flows into it.
        _inFlight.Add(Task.Run(() => WarmOneAsync(spec), CancellationToken.None));
    }

    private async Task WarmOneAsync(AddonSpec spec) {
        try {
            await _slots.WaitAsync(_cancellationToken).ConfigureAwait(false);
        } catch (OperationCanceledException) {
            return;
        }

        try {
            DiagnosticLog.Write(DiagnosticLevel.Trace, "resolve", $"prefetching {spec.Name} ({spec.Git})");
            await _resolver.ResolveAsync(spec, _cancellationToken).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Deliberately broad: see the class remarks - the walk re-observes the same outcome.
        catch (Exception)
#pragma warning restore CA1031
        {
        } finally {
            _slots.Release();
        }
    }

    /// <summary>Waits for every warm-up still running, so no git process started here outlives the
    /// resolution that asked for it. The warm-ups never throw, so neither does this.</summary>
    public async ValueTask DisposeAsync() {
        await Task.WhenAll(_inFlight).ConfigureAwait(false);
        _slots.Dispose();
    }
}
