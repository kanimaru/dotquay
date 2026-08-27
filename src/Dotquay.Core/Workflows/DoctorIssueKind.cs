namespace Dotquay.Core.Workflows;

/// <summary>The four sanity checks <c>dotquay doctor</c> runs (SPEC.md §6, requirement P1-4).</summary>
public enum DoctorIssueKind
{
    /// <summary>The <c>git</c> binary isn't on PATH — every git-sourced command is broken.</summary>
    GitNotOnPath,

    /// <summary>A lockfile entry (addon or dotnet) whose name is no longer declared in the manifest
    /// — likely a hand-edit to <c>dotquay.toml</c> that skipped <c>dotquay remove</c>.</summary>
    DanglingLockfileEntry,

    /// <summary>An installed addon's own <c>plugin.cfg</c> reports a different version than what
    /// dotquay has locked — a sign the folder's contents and the lockfile have drifted apart.</summary>
    PluginCfgVersionDrift,

    /// <summary>A folder under <c>addons/</c> that no locked addon's name accounts for.</summary>
    OrphanedAddonFolder,
}
