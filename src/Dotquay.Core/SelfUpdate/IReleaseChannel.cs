namespace Dotquay.Core.SelfUpdate;

/// <summary>
/// Where dotquay's own releases come from (docs/DESIGN-v0.17-SELF-UPDATE.md).
/// </summary>
/// <remarks>
/// <para>The shipping implementation talks to a private GitLab project, because that is where
/// dotquay is published this year. That is a fact about the deployment, not about the design: a
/// public mirror, a plain static host, or an in-memory fake all satisfy this interface, and
/// <see cref="SelfUpdateWorkflow"/> — which owns every decision that can destroy a working
/// installation — is testable against the fake precisely because nothing above this line knows what
/// a GitLab release looks like.</para>
/// <para>Three methods rather than one "get everything": <c>self check</c> must be able to answer
/// without downloading 11 MB, and <c>--to</c> must be able to name a version without walking the
/// release list.</para>
/// </remarks>
public interface IReleaseChannel
{
    /// <summary>Human-readable channel identity for messages ("gitlab.kemomi.de/…"). Never a
    /// credential, and never the raw API URL with a token in it — there is no such URL in dotquay,
    /// and this is the property most likely to end up in a bug report.</summary>
    string Describe { get; }

    /// <summary>The newest published release, or null when the channel has published none.</summary>
    Task<ReleaseManifest?> GetLatestAsync(CancellationToken cancellationToken = default);

    /// <summary>One specific release, or null when there is no such version.</summary>
    Task<ReleaseManifest?> GetVersionAsync(string version, CancellationToken cancellationToken = default);

    /// <summary>The asset's bytes. Held in memory rather than streamed to disk: the binary is about
    /// 11 MB, and hashing before anything touches the install directory is worth more than the
    /// allocation.</summary>
    Task<byte[]> DownloadAsync(ReleaseAsset asset, CancellationToken cancellationToken = default);
}
