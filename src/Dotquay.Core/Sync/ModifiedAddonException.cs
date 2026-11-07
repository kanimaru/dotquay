namespace Dotquay.Core.Sync;

/// <summary>
/// The P0-6 refusal: an installed addon folder holds content the lockfile does not describe, and
/// the operation would discard it.
/// </summary>
/// <remarks>
/// Its own type so the workflow can name the files that differ. Working that out means rebuilding
/// the locked tree, which may fetch, and the sync layer only ever compares what is already on disk
/// — so it says which folder it refused over and leaves the explaining to
/// <see cref="Workflows.DependencyWorkflow"/>, which already knows how (<c>status --explain</c>).
/// </remarks>
public sealed class ModifiedAddonException : SyncException {
    /// <summary>The installed folder whose content differs, as <see cref="InstallPaths"/> spells
    /// it.</summary>
    public string ModifiedPath { get; }

    public ModifiedAddonException(string message, string modifiedPath)
        : base(message) {
        ModifiedPath = modifiedPath;
    }
}
