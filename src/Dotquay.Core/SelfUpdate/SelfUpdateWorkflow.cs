using System.Security.Cryptography;
using Dotquay.Core.Abstractions;
using Dotquay.Core.Workflows;

namespace Dotquay.Core.SelfUpdate;

/// <param name="ToVersion">Install this exact version instead of the newest. Older is allowed and
/// said out loud — pinning back to a known-good build is the reason this exists.</param>
/// <param name="Force">Skip the "already current" short-circuit and the build-output guard. Never
/// skips checksum or start-up verification: those are not opinions about what the user wants.</param>
/// <param name="DryRun">Report what would happen and write nothing, not even the download.</param>
public sealed record SelfUpdateOptions(string? ToVersion = null, bool Force = false, bool DryRun = false);

public sealed record SelfCheckResult(
    string CurrentVersion,
    string? LatestVersion,
    bool UpdateAvailable,
    string Channel,
    string ExecutablePath,
    DateTimeOffset? ReleasedAt,
    IReadOnlyList<string> AvailableRids);

public sealed record SelfUpdateResult(
    string FromVersion,
    string ToVersion,
    bool Changed,
    string ExecutablePath,
    string? PreviousBinary,
    IReadOnlyList<string> Messages);

/// <summary>
/// Everything <c>dotquay self</c> does, above the channel and above the filesystem
/// (docs/DESIGN-v0.17-SELF-UPDATE.md).
/// </summary>
/// <remarks>
/// This type owns every decision that can leave a machine without a working dotquay, which is why it
/// takes an <see cref="IReleaseChannel"/>, an <see cref="IExecutableProbe"/> and an
/// <see cref="IFileSystem"/> rather than reaching for HTTP, processes or disk itself: the failure
/// modes worth testing here — a checksum that does not match, a binary that will not start, a rename
/// that fails halfway — are all unreachable from a test that needs a real network and a real
/// install to reproduce them.
/// </remarks>
public sealed class SelfUpdateWorkflow {
    private readonly IFileSystem _fileSystem;
    private readonly IReleaseChannel _channel;
    private readonly IExecutableProbe _probe;
    private readonly ExecutableSwapper _swapper;
    private readonly string _executablePath;
    private readonly string _currentVersion;

    public SelfUpdateWorkflow(
        IFileSystem fileSystem,
        IReleaseChannel channel,
        IExecutableProbe probe,
        string executablePath,
        string currentVersion) {
        _fileSystem = fileSystem;
        _channel = channel;
        _probe = probe;
        _swapper = new ExecutableSwapper(fileSystem);
        _executablePath = executablePath;
        _currentVersion = currentVersion;
    }

    /// <summary>Overridable so a test can pin the platform. Production reads the machine.</summary>
    public string? RuntimeIdentifier { get; init; } = RuntimeIdentifiers.Current;

    public async Task<SelfCheckResult> CheckAsync(CancellationToken cancellationToken = default) {
        var release = await _channel.GetLatestAsync(cancellationToken);

        return new SelfCheckResult(
            _currentVersion,
            release?.Version,
            release is not null && VersionOrder.IsNewer(release.Version, _currentVersion),
            _channel.Describe,
            _executablePath,
            release?.ReleasedAt,
            release?.Assets.Select(asset => asset.Rid).ToList() ?? []);
    }

    public async Task<SelfUpdateResult> UpdateAsync(SelfUpdateOptions options, CancellationToken cancellationToken = default) {
        var release = options.ToVersion is { Length: > 0 } wanted
            ? await _channel.GetVersionAsync(wanted, cancellationToken)
                ?? throw new WorkflowException(
                    $"The update channel ({_channel.Describe}) has no release {wanted}.")
            : await _channel.GetLatestAsync(cancellationToken)
                ?? throw new WorkflowException(
                    $"The update channel ({_channel.Describe}) has published no releases yet.");

        var messages = new List<string>();

        if (!options.Force && options.ToVersion is null && !VersionOrder.IsNewer(release.Version, _currentVersion)) {
            messages.Add(VersionOrder.Compare(_currentVersion, release.Version) > 0
                ? $"You are on {_currentVersion}, which is ahead of the newest release ({release.Version}). Nothing to do."
                : $"Already on {release.Version} - the newest release. Nothing to do.");

            return new SelfUpdateResult(_currentVersion, _currentVersion, Changed: false, _executablePath, null, messages);
        }

        if (options.ToVersion is not null && VersionOrder.Compare(release.Version, _currentVersion) < 0) {
            messages.Add($"Downgrading from {_currentVersion} to {release.Version}.");
        }

        var asset = SelectAsset(release);

        if (asset.Sha256 is null) {
            throw new WorkflowException(
                $"Release {release.Version} publishes no checksum for {asset.FileName}, so there is no way to tell "
                + "whether what arrives is what was published. Refusing to install it. This release was built by a "
                + "pipeline older than v0.17; download it by hand this once, or update to a newer release.");
        }

        if (!options.Force) {
            RefuseIfBuildOutput();
        }

        RefuseIfNotWritable();

        // A staged download an earlier, failed attempt could not delete (see
        // ExecutableSwapper.SweepStaged) is collected here rather than left to accumulate.
        _swapper.SweepStaged(_executablePath);

        if (options.DryRun) {
            messages.Add(
                $"Would download {asset.FileName} ({Describe(asset.Size)}) from {_channel.Describe} and replace "
                + $"{_executablePath} ({_currentVersion} -> {release.Version}).");

            return new SelfUpdateResult(_currentVersion, release.Version, Changed: false, _executablePath, null, messages);
        }

        var bytes = await _channel.DownloadAsync(asset, cancellationToken);
        VerifyChecksum(asset, bytes);

        var stagedPath = ExecutablePaths.Staged(_executablePath, release.Version);
        _fileSystem.WriteAllBytes(stagedPath, bytes);
        _fileSystem.SetExecutablePermissions(stagedPath);

        await VerifyStartsAsync(stagedPath, release.Version, cancellationToken);

        string retiredPath;
        try {
            retiredPath = _swapper.Swap(_executablePath, stagedPath, _currentVersion);
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            await TryDeleteAsync(stagedPath);
            throw new WorkflowException(
                $"Could not replace {_executablePath}: {ex.Message} The previous binary is untouched. If another "
                + "dotquay is running, or a virus scanner is holding the file, try again once it has let go.", ex);
        }

        _swapper.Sweep(_executablePath);

        messages.Add($"Updated {_currentVersion} -> {release.Version} ({_executablePath}).");
        messages.Add($"The previous binary is kept at {retiredPath} - 'dotquay self rollback' puts it back.");

        return new SelfUpdateResult(_currentVersion, release.Version, Changed: true, _executablePath, retiredPath, messages);
    }

    public async Task<SelfUpdateResult> RollbackAsync(CancellationToken cancellationToken = default) {
        var previous = _swapper.Retired(_executablePath).FirstOrDefault()
            ?? throw new WorkflowException(
                $"There is no previous dotquay beside {_executablePath} to roll back to. One is kept only after "
                + "'dotquay self update' has replaced a binary at least once.");

        if (string.Equals(previous.Version, _currentVersion, StringComparison.OrdinalIgnoreCase)) {
            throw new WorkflowException(
                $"The only binary kept beside {_executablePath} is {previous.Version}, which is what is already "
                + "running. Rolling back to it would change nothing.");
        }

        // Re-checked rather than trusted: a retired binary has been sitting on disk since the last
        // update, and one that no longer starts is not a rollback target, it is a way to lose the
        // working dotquay that is asking the question.
        var reported = await _probe.ReadVersionAsync(previous.Path, cancellationToken);
        if (reported is null) {
            throw new WorkflowException(
                $"{previous.Path} does not start, so rolling back to it would leave this machine without a working "
                + $"dotquay. Leaving {_currentVersion} in place.");
        }

        RefuseIfNotWritable();

        var retiredPath = _swapper.Swap(_executablePath, previous.Path, _currentVersion);
        _swapper.Sweep(_executablePath);

        return new SelfUpdateResult(
            _currentVersion,
            previous.Version,
            Changed: true,
            _executablePath,
            retiredPath,
            [
                $"Rolled back {_currentVersion} -> {previous.Version} ({_executablePath}).",
                $"{_currentVersion} is kept at {retiredPath} - 'dotquay self update' installs the newest release again.",
            ]);
    }

    private ReleaseAsset SelectAsset(ReleaseManifest release) {
        var rid = RuntimeIdentifier
            ?? throw new WorkflowException(
                "dotquay could not work out what platform this is, so it cannot pick a release build. Download the "
                + $"right binary by hand from {_channel.Describe}.");

        if (release.AssetFor(rid) is { } asset) {
            return asset;
        }

        var available = release.Assets.Count == 0
            ? "it publishes no binaries at all - the release exists but its assets are missing"
            : $"it publishes {string.Join(", ", release.Assets.Select(a => a.Rid))}";

        throw new WorkflowException(
            $"Release {release.Version} has no build for {rid}: {available}.");
    }

    private void VerifyChecksum(ReleaseAsset asset, byte[] bytes) {
        if (asset.Size > 0 && bytes.LongLength != asset.Size) {
            throw new WorkflowException(
                $"{asset.FileName} arrived as {bytes.LongLength} bytes but the release says it is {asset.Size}. "
                + "Refusing to install a truncated download.");
        }

        var actual = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if (!string.Equals(actual, asset.Sha256, StringComparison.OrdinalIgnoreCase)) {
            throw new WorkflowException(
                $"{asset.FileName} does not match the checksum the release published.{Environment.NewLine}"
                + $"  expected {asset.Sha256}{Environment.NewLine}"
                + $"  got      {actual}{Environment.NewLine}"
                + "Nothing was installed. A repeatable mismatch is worth reporting rather than retrying.");
        }
    }

    private async Task VerifyStartsAsync(string stagedPath, string expectedVersion, CancellationToken cancellationToken) {
        var reported = await _probe.ReadVersionAsync(stagedPath, cancellationToken);

        if (reported is null) {
            await TryDeleteAsync(stagedPath);
            throw new WorkflowException(
                $"The downloaded dotquay {expectedVersion} hashed correctly but does not start on this machine. "
                + "Nothing was replaced. This is a broken publish, not a broken download - worth reporting.");
        }

        if (!reported.StartsWith(expectedVersion, StringComparison.OrdinalIgnoreCase)) {
            await TryDeleteAsync(stagedPath);
            throw new WorkflowException(
                $"The downloaded binary reports version '{reported}' but the release says it is {expectedVersion}. "
                + "Nothing was replaced.");
        }
    }

    /// <summary>
    /// Refuses to overwrite a build output.
    /// </summary>
    /// <remarks>
    /// <para>Nobody means "replace my <c>bin/Release/.../dotquay.exe</c> with a downloaded release",
    /// and a dev build reports the csproj's version, which is usually behind every published one — so
    /// without this guard the single most likely first use of <c>self update</c>, run from a
    /// checkout, would clobber the thing being developed with an older binary.</para>
    /// <para>A bare <c>bin</c> segment is deliberately NOT a marker. <c>~/.local/bin</c> is where the
    /// install scripts put dotquay and where most people on Unix keep their tools; flagging it would
    /// make this guard fire on the single most common correct installation. What actually marks a
    /// build output is <c>obj</c>, a <c>bin</c> followed by a configuration name, or a <c>.pdb</c> /
    /// <c>.deps.json</c> lying beside the binary — none of which appear in an install directory.</para>
    /// </remarks>
    private void RefuseIfBuildOutput() {
        var directory = ExecutablePaths.DirectoryOf(_executablePath);
        var segments = directory.Split('/', '\\');

        var looksLikeBuildPath =
            segments.Any(segment => segment.Equals("obj", StringComparison.OrdinalIgnoreCase))
            || segments
                .Select((segment, index) => (segment, index))
                .Any(entry => entry.segment.Equals("bin", StringComparison.OrdinalIgnoreCase)
                    && segments.Skip(entry.index + 1).Any(later =>
                        later.Equals("Debug", StringComparison.OrdinalIgnoreCase)
                        || later.Equals("Release", StringComparison.OrdinalIgnoreCase)));

        var hasBuildArtifacts = _fileSystem.DirectoryExists(directory)
            && _fileSystem.EnumerateFiles(directory, recursive: false).Any(file =>
                file.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase)
                || file.EndsWith(".deps.json", StringComparison.OrdinalIgnoreCase));

        if (looksLikeBuildPath || hasBuildArtifacts) {
            throw new WorkflowException(
                $"{_executablePath} looks like a build output, not an installed dotquay. Replacing it with a "
                + "published release would overwrite what you are working on, and the version a dev build reports "
                + "is usually behind every release. Run the installed dotquay instead, or pass --force if this "
                + "really is your installation.");
        }
    }

    private void RefuseIfNotWritable() {
        var directory = ExecutablePaths.DirectoryOf(_executablePath);
        var probePath = ExecutablePaths.DirectoryOf(_executablePath).Length == 0
            ? ".dotquay-write-probe"
            : _fileSystem.CombinePath(directory, ".dotquay-write-probe");

        try {
            _fileSystem.WriteAllBytes(probePath, []);
            _fileSystem.DeleteFile(probePath);
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            throw new WorkflowException(
                $"dotquay cannot write to {directory}, so it cannot replace itself there. Install it somewhere you "
                + "own - scripts/install.sh and scripts/install.ps1 put it in ~/.local/bin - or re-run this with "
                + "the privileges that directory needs.", ex);
        }
    }

    /// <summary>
    /// Deletes a file this workflow created, tolerating the one case Windows makes hard.
    /// </summary>
    /// <remarks>
    /// A staged binary has just been <em>run</em> - that is the point of it - and Windows keeps an
    /// executable's image mapped for a short while after the process exits, answering "access is
    /// denied" to a delete in that window. Retried briefly and then given up on, because the caller
    /// is already on its way to reporting a real failure (a bad checksum, a binary that will not
    /// start) and a cleanup error must never be the message that reaches the user instead of that
    /// one. Whatever is left behind is collected by the next update's SweepStaged.
    /// </remarks>
    private async Task TryDeleteAsync(string path) {
        for (var attempt = 0; attempt < 5; attempt++) {
            try {
                _fileSystem.DeleteFile(path);
                return;
            } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                await Task.Delay(TimeSpan.FromMilliseconds(100));
            }
        }
    }

    private static string Describe(long bytes) =>
        bytes <= 0 ? "unknown size" : $"{bytes / 1024.0 / 1024.0:0.#} MB";
}
