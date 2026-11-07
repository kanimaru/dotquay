using Dotquay.Core.Abstractions;
using Dotquay.Core.Dotnet;
using Dotquay.Core.Dotnet.SourceBuild;
using Dotquay.Core.Lockfile;
using Dotquay.Core.Workflows;

namespace Dotquay.Core.Doctor;

/// <summary>
/// <c>doctor</c>'s half of docs/DESIGN-v0.26-SOURCE-BUILT-PACKAGES.md §C: a locked package the
/// <c>.csproj</c> builds from source while also referencing it as a package, and a source build whose
/// version is not the one the lockfile hands to everyone else.
/// </summary>
/// <remarks>
/// Pure and local, like the other checkers here: it reads two kinds of project file and the lockfile,
/// and reports. It fixes nothing - <c>install</c> already removes an unconditional duplicate, and a
/// conditional one or a version difference is a decision only the project's author can make.
/// Only packages the lockfile records are checked, so <c>doctor</c> keeps to what dotquay manages.
/// </remarks>
public static class SourceBuildChecker {
    public static IReadOnlyList<DoctorIssue> Check(IFileSystem fileSystem, string csprojPath, DotquayLockfile lockfile) {
        var references = SourceProvidedPackages.ReadAll(fileSystem, csprojPath);
        if (references.Count == 0) {
            return [];
        }

        var issues = new List<DoctorIssue>();

        foreach (var locked in lockfile.Dotnet) {
            var providers = references.Where(r => r.Builds(locked.Name)).ToList();
            if (providers.Count == 0) {
                continue;
            }

            var unconditional = providers.FirstOrDefault(p => p.IsUnconditional);

            if (CsprojPackageReferenceWriter.HasReference(fileSystem, csprojPath, locked.Name)) {
                issues.Add(unconditional is not null
                    ? AlsoReferenced(locked.Name, unconditional, csprojPath)
                    : AlsoReferencedConditionally(locked.Name, providers[0], csprojPath));
            }

            if (unconditional?.Identity.Version is { } sourceVersion && !SameVersion(sourceVersion, locked.Resolved)) {
                issues.Add(VersionMismatch(locked.Name, unconditional, sourceVersion, locked.Resolved));
            }
        }

        return issues;
    }

    private static DoctorIssue AlsoReferenced(string packageId, SourceReference provider, string csprojPath) =>
        new(
            DoctorIssueKind.PackageAlsoBuiltFromSource,
            $"'{packageId}' is built from source by {provider.Include} and also referenced as a package in "
            + $"{csprojPath}, so the published package can shadow the source build. Run 'dotquay install' to "
            + "remove the PackageReference.");

    private static DoctorIssue AlsoReferencedConditionally(string packageId, SourceReference provider, string csprojPath) =>
        new(
            DoctorIssueKind.PackageAlsoBuiltFromSource,
            $"'{packageId}' is built from source by {provider.Include} when {provider.Condition}, and also "
            + $"referenced as a package in {csprojPath}. Under that condition the two can shadow each other. "
            + "dotquay leaves this one to you, because outside the condition the package is what builds: give "
            + "the PackageReference the opposite condition, or drop one of the two.");

    private static DoctorIssue VersionMismatch(string packageId, SourceReference provider, string sourceVersion, string locked) =>
        new(
            DoctorIssueKind.SourceBuildVersionMismatch,
            $"'{packageId}' compiles from source at {sourceVersion} ({provider.Include}), but the lockfile "
            + $"resolves {locked} from the feed. Anyone installing this project's addons gets {locked}, which is "
            + "not what this project builds and tests against. Publish the source version, or align its <Version> "
            + "with the lockfile.");

    /// <summary>
    /// Equal as NuGet versions: a leading <c>v</c>, build metadata and a zero fourth part make no
    /// difference, so <c>0.7.20</c> and <c>0.7.20.0</c> are one version. Prerelease labels compare
    /// case-insensitively, as NuGet compares them.
    /// </summary>
    internal static bool SameVersion(string a, string b) =>
        string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string version) {
        var text = version.Trim().TrimStart('v', 'V');

        var plus = text.IndexOf('+');
        if (plus >= 0) {
            text = text[..plus];
        }

        var dash = text.IndexOf('-');
        var release = dash >= 0 ? text[..dash] : text;
        var prerelease = dash >= 0 ? text[dash..] : string.Empty;

        var parts = release.Split('.').ToList();
        while (parts.Count > 3 && parts[^1] == "0") {
            parts.RemoveAt(parts.Count - 1);
        }

        while (parts.Count < 3) {
            parts.Add("0");
        }

        return string.Join('.', parts.Select(p => p.TrimStart('0') is { Length: > 0 } trimmed ? trimmed : "0")) + prerelease;
    }
}
