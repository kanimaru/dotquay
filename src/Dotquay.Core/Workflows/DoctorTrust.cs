namespace Dotquay.Core.Workflows;

/// <summary>
/// What the project currently trusts, and which feeds arrived because of it (SPEC.md §11.3).
/// </summary>
/// <remarks>
/// Reported separately from <see cref="DoctorIssue"/> on purpose. <c>trusted_hosts</c> is a real
/// security control that gets copy-pasted between projects and then forgotten, so it needs to be
/// <em>visible</em> — but it is not a fault, and reporting it as one would mark every project using
/// the feature unhealthy and fail CI, which is exactly how a checker teaches people to ignore it.
/// So this rides alongside the issue list and never affects <see cref="DoctorResult.IsHealthy"/>.
/// </remarks>
/// <param name="TrustedHosts">Hosts whose addon-declared feeds are adopted without asking.</param>
/// <param name="AdoptedSources">Feeds that came from an addon rather than from the project.</param>
public sealed record DoctorTrust(
    IReadOnlyList<string> TrustedHosts,
    IReadOnlyList<DoctorAdoptedSource> AdoptedSources) {
    public static readonly DoctorTrust Empty = new([], []);

    /// <summary>Whether there is anything to show. Nothing is printed for a project that never opted
    /// in, so the common case gains no noise.</summary>
    public bool IsEmpty => TrustedHosts.Count == 0 && AdoptedSources.Count == 0;
}

/// <summary>A feed adopted from an addon, and which addon asked for it — the answer to "why is this
/// source in my manifest?".</summary>
public sealed record DoctorAdoptedSource(string Name, string Url, string DeclaredByAddon);
