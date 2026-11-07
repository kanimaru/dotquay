using System.Collections.Concurrent;

namespace Dotquay.Mcp.Scope;

/// <summary>
/// Serializes tool calls against the same resolved project directory.
/// </summary>
/// <remarks>
/// <para>
/// An MCP client can — and Claude Code in particular sometimes does — issue several tool calls
/// concurrently. Every Phase 1 tool is read-only, so nothing here is protecting against a corrupted
/// write; what it protects against is two calls against the same project racing inside
/// <c>Dotquay.Core</c>'s own collaborators, several of which are not designed to be reentered
/// concurrently against the same working directory (the shared <c>~/.dotquay/cache</c> repo
/// checkout in particular — <c>RepoCache</c> assumes one caller at a time per cache entry). Two
/// calls against <em>different</em> projects still run fully in parallel; only same-project calls
/// queue.
/// </para>
/// <para>
/// This is also exactly where Phase 2's write tools will need the same serialization for a much
/// more concrete reason (two concurrent <c>add</c>s corrupting the lockfile), so the seam is
/// introduced now rather than retrofitted once a write tool needs it under pressure.
/// </para>
/// </remarks>
public sealed class ProjectGate {
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks;

    public ProjectGate() {
        _locks = new ConcurrentDictionary<string, SemaphoreSlim>(
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal);
    }

    public async ValueTask<T> RunAsync<T>(string projectDirectory, Func<ValueTask<T>> operation) {
        var gate = _locks.GetOrAdd(projectDirectory, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync().ConfigureAwait(false);

        try {
            return await operation().ConfigureAwait(false);
        } finally {
            gate.Release();
        }
    }
}
