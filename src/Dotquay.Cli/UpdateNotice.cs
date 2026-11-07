using Dotquay.Cli.Commands;
using Dotquay.Core.Json;
using Dotquay.Core.SelfUpdate;

namespace Dotquay.Cli;

/// <summary>
/// The one line that tells someone a newer dotquay exists (docs/DESIGN-v0.17-SELF-UPDATE.md).
/// </summary>
/// <remarks>
/// <para>This is the actual feature. "Updating is hard" was never the complaint — "I did not know
/// there was anything to update to" was, and a release cut on every green push to <c>main</c> is
/// what makes that the normal state rather than an occasional one.</para>
/// <para>Four rules make it safe to have on by default, and each of them is load-bearing:</para>
/// <list type="bullet">
/// <item><b>stderr, never stdout.</b> stdout is a contract — <c>list --json</c> is parsed by the
/// editor plugin and <c>source list</c> by shell scripts. Suppressing this under <c>--json</c>
/// alone would not be enough: a plain <c>dotquay list | grep</c> is just as parseable, and just as
/// broken by a stray human sentence.</item>
/// <item><b>Rendered from the cache, never from a request.</b> No command's latency depends on a
/// network the user did not ask this command to touch.</item>
/// <item><b>The refresh is detached and given 500 ms at exit.</b> If it does not finish, nothing is
/// printed and nothing is written; the next invocation tries again. A notice is never worth making
/// someone wait for.</item>
/// <item><b>Every path swallows its own failures.</b> A tool that failed a command because it could
/// not check for its own updates would be a worse bug than the one this fixes.</item>
/// </list>
/// </remarks>
internal sealed class UpdateNotice {
    private static readonly TimeSpan ExitGrace = TimeSpan.FromMilliseconds(500);

    private readonly UpdateSettings _settings;
    private readonly UpdateCheckCache _cache;
    private readonly Task<string?>? _refresh;
    private readonly CancellationTokenSource? _cancellation;

    private UpdateNotice(UpdateSettings settings, UpdateCheckCache cache, Task<string?>? refresh,
        CancellationTokenSource? cancellation) {
        _settings = settings;
        _cache = cache;
        _refresh = refresh;
        _cancellation = cancellation;
    }

    /// <summary>Starts a refresh when the cached answer is stale, or returns null when this
    /// invocation is one that must stay silent.</summary>
    public static UpdateNotice? Start(string[] args) {
        try {
            if (!IsEligible(args)) {
                return null;
            }

            var settings = SelfUpdateFactory.Settings();
            if (!settings.NoticeEnabled) {
                return null;
            }

            var cache = SelfUpdateFactory.Cache();
            if (!cache.IsStale(settings.ChannelUrl, settings.Interval)) {
                return new UpdateNotice(settings, cache, refresh: null, cancellation: null);
            }

            var cancellation = new CancellationTokenSource();
            var refresh = Task.Run(
                async () => {
                    try {
                        var release = await SelfUpdateFactory.Channel(settings)
                            .GetLatestAsync(cancellation.Token);
                        return release?.Version;
                    } catch {
                        // Offline, logged out, channel moved. All of them mean "no notice this
                        // time".
                        return null;
                    }
                },
                cancellation.Token);

            return new UpdateNotice(settings, cache, refresh, cancellation);
        } catch {
            return null;
        }
    }

    /// <summary>Prints the notice if there is one to print, then gets out of the way. Called after
    /// the command has produced all of its own output, so the line never lands
    /// mid-result.</summary>
    public void Report() {
        try {
            var latest = Settle();

            if (latest is null || !VersionOrder.IsNewer(latest, DotquayVersion.Current)) {
                return;
            }

            Console.Error.WriteLine(
                $"dotquay {latest} is available (you have {DotquayVersion.Current}) - run 'dotquay "
                + $"self update'");
            Console.Error.WriteLine("  silence this with 'dotquay self notify off'");
        } catch {
        }
    }

    /// <summary>The freshly-fetched version if the refresh finished inside its grace period, else
    /// whatever was cached. A refresh that is still running is abandoned rather than waited on —
    /// and deliberately not written from a continuation either, because a process about to exit is
    /// not a place to be starting file writes.</summary>
    private string? Settle() {
        if (_refresh is not null) {
            try {
                if (_refresh.Wait(ExitGrace)) {
                    var fetched = _refresh.Result;
                    _cache.Write(_settings.ChannelUrl, fetched);
                    return fetched;
                }

                _cancellation?.Cancel();
            } catch {
            }
        }

        return _cache.Read(_settings.ChannelUrl)?.LatestVersion;
    }

    /// <summary>
    /// Whether this invocation may print at all.
    /// </summary>
    /// <remarks>
    /// <c>--json</c> is checked against the raw arguments rather than the parse result because the
    /// notice starts before the command runs, and because a command that failed to parse still must
    /// not have a sentence appended to a machine-readable stream. The <c>self</c> group is excluded
    /// because it says the same thing better, in the command the user actually asked for.
    /// <c>mcp</c> (v0.18) is excluded for a different reason: <c>Report()</c> only ever runs after
    /// <c>rootCommand.Invoke()</c> returns, and for a server command that blocks until its client
    /// disconnects, that could be hours away — <see cref="Settle"/>'s 500&#160;ms exit grace would
    /// never be the thing that decides anything, so <see cref="Start"/>'s background fetch would
    /// just be pointless network and file I/O kicked off, and left running, every time an editor
    /// spins the server up.
    /// </remarks>
    private static bool IsEligible(string[] args) {
        if (Console.IsErrorRedirected) {
            return false;
        }

        if (Environment.GetEnvironmentVariable("CI") is { Length: > 0 }) {
            return false;
        }

        foreach (var arg in args) {
            if (arg.Equals("--json", StringComparison.Ordinal)
                || arg.Equals("self", StringComparison.Ordinal)
                || arg.Equals("credential", StringComparison.Ordinal)
                || arg.Equals("mcp", StringComparison.Ordinal)) {
                return false;
            }
        }

        return true;
    }
}
