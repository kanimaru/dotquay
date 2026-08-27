using Dotquay.Core.Abstractions;
using Dotquay.Core.Manifest;

namespace Dotquay.Core.Workflows;

/// <summary>
/// Where a project's directories are. Carries <see cref="ProjectDirectory"/> and
/// <see cref="PatchesDirectory"/> together because they always travel together, and the second is
/// configurable (SPEC.md §10) — passing only the first would force every consumer to re-derive the
/// patches path and guess at the manifest setting.
/// </summary>
public sealed record ProjectLayout(string ProjectDirectory, string PatchesDirectory)
{
    /// <summary>Where dotquay keeps everything of its own. Mostly ephemeral (cache, staging), which
    /// is why patches inside it need their own <c>.gitignore</c> rule to stay committed.</summary>
    public const string DotquayDirectory = ".dotquay";

    /// <summary>Default since v0.6: out of the way, next to the rest of dotquay's state.</summary>
    public const string DefaultPatchesDirectory = $"{DotquayDirectory}/patches";

    /// <summary>Where patches lived before v0.6, and still do for any project that has one.</summary>
    public const string LegacyPatchesDirectory = "patches";

    /// <summary>
    /// Resolves the layout for a project.
    /// </summary>
    /// <remarks>
    /// The fallback to <see cref="LegacyPatchesDirectory"/> is not tidiness — it is the difference
    /// between a silent regression and none. Patches are re-applied on every install and update
    /// (SPEC.md §10 step 3); if changing the default made dotquay stop looking where an existing
    /// project's patches actually are, those projects would quietly start installing unpatched code,
    /// and <c>status</c> would call the result correct. So an existing <c>patches/</c> folder keeps
    /// winning until its owner moves it or sets <c>patches_dir</c> explicitly.
    /// </remarks>
    public static ProjectLayout For(IFileSystem fileSystem, string projectDirectory, ProjectManifest? manifest)
    {
        if (manifest?.PatchesDirectory is { Length: > 0 } configured)
        {
            return new ProjectLayout(projectDirectory, configured);
        }

        var legacy = fileSystem.CombinePath(projectDirectory, LegacyPatchesDirectory);
        return fileSystem.DirectoryExists(legacy)
            ? new ProjectLayout(projectDirectory, LegacyPatchesDirectory)
            : new ProjectLayout(projectDirectory, DefaultPatchesDirectory);
    }

    /// <summary>A layout for callers that never touch patches, so they don't have to read a manifest
    /// just to name a directory they won't use.</summary>
    public static ProjectLayout WithoutPatches(string projectDirectory) =>
        new(projectDirectory, DefaultPatchesDirectory);

    /// <summary>Split into segments before combining so a configured value written with forward
    /// slashes (the natural way to write it in TOML) doesn't produce a mixed-separator path in
    /// messages the user reads.</summary>
    public string PatchesPath(IFileSystem fileSystem) =>
        fileSystem.CombinePath(
            [ProjectDirectory, .. PatchesDirectory.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries)]);

    public string StagingPath(IFileSystem fileSystem, string addonName) =>
        fileSystem.CombinePath(ProjectDirectory, DotquayDirectory, "staging", addonName);

    /// <summary>
    /// Where a stalled patch conflict records the commit its staged tree came from.
    /// </summary>
    /// <remarks>
    /// An update that stops on a patch conflict never writes the lockfile, so the lockfile still names
    /// the <em>old</em> commit. Without this note, <c>patch --resolve</c> would recapture the resolved
    /// tree against that old base and produce a patch that conflicts against the new upstream on every
    /// future update — resolve would appear to work once and then wedge the addon permanently.
    /// </remarks>
    public string ConflictMarkerPath(IFileSystem fileSystem, string addonName) =>
        fileSystem.CombinePath(ProjectDirectory, DotquayDirectory, "staging", $"{addonName}.conflict");

    public string PatchFilePath(IFileSystem fileSystem, string addonName) =>
        fileSystem.CombinePath(PatchesPath(fileSystem), $"{addonName}.patch");

    /// <summary>True when patches live under <see cref="DotquayDirectory"/> and therefore need the
    /// nested <c>.gitignore</c> that keeps them tracked while cache and staging aren't.</summary>
    public bool PatchesAreInsideDotquay =>
        PatchesDirectory.Replace('\\', '/').StartsWith(DotquayDirectory + "/", StringComparison.Ordinal);
}
