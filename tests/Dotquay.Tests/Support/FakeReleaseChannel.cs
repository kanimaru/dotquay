using System.Security.Cryptography;
using Dotquay.Core.SelfUpdate;

namespace Dotquay.Tests.Support;

/// <summary>
/// <see cref="IReleaseChannel"/> test double.
/// </summary>
/// <remarks>
/// The whole reason <see cref="IReleaseChannel"/> exists as a seam. The failures worth testing in
/// <see cref="SelfUpdateWorkflow"/> — a checksum that does not match what was downloaded, a release
/// with no build for this platform, a release published without checksums at all — are each one
/// line here and are unreachable from a test that needs a real GitLab project to reproduce them.
/// </remarks>
public sealed class FakeReleaseChannel : IReleaseChannel
{
    private readonly Dictionary<string, ReleaseManifest> _releases = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, byte[]> _payloads = new(StringComparer.Ordinal);

    public string Describe { get; set; } = "releases.example";

    /// <summary>Set by <see cref="Publish"/>; what <c>GetLatestAsync</c> answers.</summary>
    public string? LatestVersion { get; set; }

    public int LatestQueries { get; private set; }

    public int Downloads { get; private set; }

    /// <summary>Publishes a release whose asset really does hash to the checksum it advertises —
    /// the honest case, so a test that wants a mismatch has to ask for one
    /// (<see cref="Corrupt"/>).</summary>
    public FakeReleaseChannel Publish(string version, string rid = "linux-x64", string? contents = null)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(contents ?? $"dotquay {version} for {rid}");
        var url = $"https://releases.example/{version}/{rid}";

        _payloads[url] = bytes;
        _releases[version] = new ReleaseManifest(
            version,
            DateTimeOffset.UnixEpoch,
            [new ReleaseAsset(rid, $"dotquay-{rid}", url, Sha256(bytes), bytes.LongLength)]);

        if (LatestVersion is null || string.CompareOrdinal(version, LatestVersion) > 0)
        {
            LatestVersion = version;
        }

        return this;
    }

    /// <summary>Publishes a release the way a pipeline older than v0.17 did: binaries, no
    /// checksums.</summary>
    public FakeReleaseChannel PublishWithoutChecksum(string version, string rid = "linux-x64")
    {
        Publish(version, rid);
        var release = _releases[version];
        _releases[version] = release with
        {
            Assets = release.Assets.Select(asset => asset with { Sha256 = null }).ToList(),
        };

        return this;
    }

    /// <summary>Makes the bytes served for a release disagree with the checksum it advertises,
    /// without touching the manifest — which is exactly the shape of a corrupted transfer or a
    /// tampered mirror.</summary>
    public FakeReleaseChannel Corrupt(string version)
    {
        foreach (var asset in _releases[version].Assets)
        {
            // Same length, different content. A shorter payload would trip the size check first and
            // the checksum would never be reached - which is correct behaviour, and not the
            // behaviour a checksum test is trying to assert.
            var tampered = _payloads[asset.Url].ToArray();
            tampered[0] ^= 0xFF;
            _payloads[asset.Url] = tampered;
        }

        return this;
    }

    public Task<ReleaseManifest?> GetLatestAsync(CancellationToken cancellationToken = default)
    {
        LatestQueries++;
        return Task.FromResult(LatestVersion is null ? null : _releases.GetValueOrDefault(LatestVersion));
    }

    public Task<ReleaseManifest?> GetVersionAsync(string version, CancellationToken cancellationToken = default) =>
        Task.FromResult(_releases.GetValueOrDefault(version.TrimStart('v')));

    public Task<byte[]> DownloadAsync(ReleaseAsset asset, CancellationToken cancellationToken = default)
    {
        Downloads++;
        return Task.FromResult(_payloads[asset.Url]);
    }

    public static string Sha256(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}

/// <summary>
/// <see cref="IExecutableProbe"/> test double: answers what a binary "reports" without starting
/// anything.
/// </summary>
/// <remarks>
/// Defaults to deriving the version from the fake payload's own text, so the honest path needs no
/// setup and the two dishonest ones — a binary that will not start, and one that reports a version
/// other than the release it came from — are each a single explicit registration.
/// </remarks>
public sealed class FakeExecutableProbe : IExecutableProbe
{
    private readonly Dictionary<string, string?> _answers = new(StringComparer.Ordinal);

    public List<string> Probed { get; } = [];

    /// <summary>Null means "this binary does not start".</summary>
    public FakeExecutableProbe Reports(string path, string? version)
    {
        _answers[path.Replace('\\', '/').TrimStart('/')] = version;
        return this;
    }

    public Task<string?> ReadVersionAsync(string path, CancellationToken cancellationToken = default)
    {
        var key = path.Replace('\\', '/').TrimStart('/');
        Probed.Add(key);

        if (_answers.TryGetValue(key, out var answer))
        {
            return Task.FromResult(answer);
        }

        // "dotquay 0.18.0 for linux-x64" -> "0.18.0": the fake payload text carries the version it
        // was published as, so an un-stubbed binary tells the truth about itself.
        var name = key.Split('/').Last();
        var infix = name.Contains(".new-", StringComparison.Ordinal) ? ".new-"
            : name.Contains(".old-", StringComparison.Ordinal) ? ".old-"
            : null;

        if (infix is null)
        {
            return Task.FromResult<string?>(null);
        }

        var version = name[(name.IndexOf(infix, StringComparison.Ordinal) + infix.Length)..];
        return Task.FromResult<string?>(version.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? version[..^4]
            : version);
    }
}
