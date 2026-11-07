using System.Runtime.InteropServices;

namespace Dotquay.Core.SelfUpdate;

/// <summary>
/// One published release, as any channel describes it (docs/DESIGN-v0.17-SELF-UPDATE.md).
/// </summary>
/// <remarks>
/// Deliberately not a GitLab shape. Everything above <see cref="IReleaseChannel"/> deals in this
/// type, so swapping the channel for a public mirror, a static file host, or a test double is a
/// constructor argument rather than a rewrite of the updater.
/// </remarks>
public sealed record ReleaseManifest(
    string Version,
    DateTimeOffset? ReleasedAt,
    IReadOnlyList<ReleaseAsset> Assets) {
    /// <summary>The asset for a runtime identifier, or null when this release shipped nothing for
    /// it. Callers report the RIDs that *are* present rather than a bare "not found" — a release
    /// missing win-x64 is a CI problem, and naming what did ship is what makes that visible.</summary>
    public ReleaseAsset? AssetFor(string rid) =>
        Assets.FirstOrDefault(asset => string.Equals(asset.Rid, rid, StringComparison.OrdinalIgnoreCase));
}

/// <param name="Rid">The .NET runtime identifier this binary is for, e.g. <c>win-x64</c>.</param>
/// <param name="FileName">The published file's name, used to name the staged download.</param>
/// <param name="Url">Where to fetch it. Absolute, and on the channel's own host.</param>
/// <param name="Sha256">Lowercase hex digest. Null means the release published none, which
/// <see cref="SelfUpdateWorkflow"/> treats as a refusal rather than a warning: an unverified
/// download is exactly as risky as fetching the binary by hand, which is the thing being replaced.</param>
/// <param name="Size">Bytes, for a progress line and for a cheap sanity check before hashing.</param>
public sealed record ReleaseAsset(string Rid, string FileName, string Url, string? Sha256, long Size);

/// <summary>Which binary this process would replace itself with.</summary>
public static class RuntimeIdentifiers {
    /// <summary>
    /// The RID dotquay publishes under for the running machine, or null when this platform has no
    /// published build.
    /// </summary>
    /// <remarks>
    /// Computed from <see cref="RuntimeInformation"/> rather than read from
    /// <c>RuntimeInformation.RuntimeIdentifier</c>, which under NativeAOT reports the RID the binary
    /// was *published* for. That is the same answer today and would silently become the wrong one
    /// the first time a linux-x64 build is run under emulation.
    /// </remarks>
    public static string? Current {
        get {
            var os = OperatingSystem.IsWindows() ? "win"
                : OperatingSystem.IsLinux() ? "linux"
                : OperatingSystem.IsMacOS() ? "osx"
                : null;

            var architecture = RuntimeInformation.OSArchitecture switch {
                Architecture.X64 => "x64",
                Architecture.Arm64 => "arm64",
                Architecture.X86 => "x86",
                _ => null,
            };

            return os is null || architecture is null ? null : $"{os}-{architecture}";
        }
    }
}
