using Dotquay.Core.Lockfile;

namespace Dotquay.Core.Doctor;

/// <summary>
/// Detects a lockfile entry whose install folder disagrees with the <c>install_as</c> the manifest
/// that declares it asks for.
/// </summary>
/// <remarks>
/// <para>
/// This drift is a normal consequence of the pipeline, not a corruption: <c>install</c> replays the
/// lockfile and deliberately never re-reads the manifest (SPEC.md §5), so adding, changing, or
/// removing <c>install_as</c> changes nothing on disk until an <c>update</c> re-resolves. In between,
/// the manifest says one folder and the addon sits in another — and every symptom of that shows up
/// somewhere else entirely: <c>preload("res://addons/…")</c> paths that don't resolve, an addon that
/// looks installed twice, a <c>doctor</c> orphan report for a folder that is in fact the live one.
/// </para>
/// <para>
/// Same reasoning as <see cref="Workflows.DoctorIssueKind.PackageAssetMetadataDrift"/>: two files
/// already on disk are being compared, no network is involved, and the check is what makes "the
/// manifest decides the folder" a claim rather than a hope.
/// </para>
/// </remarks>
public static class InstallFolderDriftChecker {
    /// <param name="Folder">The folder the declaration asks the addon to be installed into.</param>
    /// <param name="IsExplicit">Whether an <c>install_as</c> was actually stated, as opposed to
    /// <paramref name="Folder"/> being the default of the addon's own name. The distinction is the
    /// whole reason this isn't a plain string comparison — see <see cref="Check"/>.</param>
    /// <param name="Source">Where the declaration was read from, for the message. A folder can be
    /// asked for by the project's own manifest, by an override, or by the package manifest of an addon
    /// that depends on this one.</param>
    public sealed record FolderDeclaration(string Folder, bool IsExplicit, string Source);

    /// <param name="Actual">Where the lockfile says the addon is installed.</param>
    /// <param name="Expected">Where its declaration asks for it.</param>
    public sealed record Drift(string Name, string Actual, string Expected, string Source);

    /// <summary>
    /// Returns the drift between where <paramref name="locked"/> is installed and where
    /// <paramref name="declaration"/> asks for it, or null when they agree.
    /// </summary>
    /// <remarks>
    /// A case-only difference against a declaration that states no <c>install_as</c> is deliberately
    /// not drift. That is <see cref="Sync.AddonInstaller"/>'s folder-case correction doing its job: the
    /// addon's own repository, not the manifest key, is the authority on the casing of its folder, so
    /// an addon keyed <c>Gut</c> and correctly installed to <c>addons/gut</c> is healthy. Reporting it
    /// would fire on every addon that correction has ever helped, and the advice would be to undo the
    /// fix. An <em>explicit</em> <c>install_as</c> is compared ordinally, because there the user has
    /// stated the casing and the correction never overrides them.
    /// </remarks>
    public static Drift? Check(LockedAddon locked, FolderDeclaration declaration) {
        var actual = locked.EffectiveFolder;

        if (string.Equals(actual, declaration.Folder, StringComparison.Ordinal)) {
            return null;
        }

        if (!declaration.IsExplicit
            && string.Equals(actual, declaration.Folder, StringComparison.OrdinalIgnoreCase)) {
            return null;
        }

        return new Drift(locked.Name, actual, declaration.Folder, declaration.Source);
    }
}
