namespace Dotquay.Core.Workflows;

/// <summary>The sanity checks <c>dotquay doctor</c> runs (SPEC.md §6, requirement P1-4; the
/// credential checks are v0.7, SPEC.md §11.2).</summary>
/// <remarks>
/// This is a wire contract, not an internal detail: each member is mapped to a fixed string in
/// <c>JsonMapper</c> and read by the Godot editor plugin, so adding a member is a JSON contract
/// change and renaming one would break a consumer silently.
/// </remarks>
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

    /// <summary>An installed addon ships a <c>dotquay.toml</c> that isn't a valid package manifest —
    /// usually a missing <c>[package]</c> header (SPEC.md §7.2). The addon still installs fine, but
    /// every dependency it declares is being silently ignored. Purely local, so this catches the
    /// problem without a network round-trip.</summary>
    UnreadablePackageManifest,

    /// <summary>
    /// The credential file is readable by more than its owner (Unix), or sits somewhere without
    /// user-only protection.
    /// </summary>
    /// <remarks>
    /// Checked rather than assumed. <c>TomlCredentialStore</c> sets <c>0600</c> when it writes, but a
    /// file can be created by hand, copied from another machine, or restored from a backup that
    /// flattened permissions — and a world-readable file of plaintext tokens is the one failure this
    /// design's honesty about plaintext storage depends on not happening quietly.
    /// </remarks>
    CredentialFilePermissions,

    /// <summary>
    /// <c>dotquay.toml</c> contains something shaped like a secret — a <c>glpat-</c>/<c>ghp_</c>
    /// token, or a URL with an embedded <c>user:password@</c> authority.
    /// </summary>
    /// <remarks>
    /// Cheap, and it catches the exact mistake the whole credential design exists to make
    /// unnecessary. The manifest is committed, so a token here is a token in git history.
    /// </remarks>
    SecretInManifest,

    /// <summary>
    /// A <c>[nuget] trusted_hosts</c> entry that can't do what it reads as (SPEC.md §11.3): a
    /// wildcard, a typo, a host carrying a path, or a plain-HTTP host.
    /// </summary>
    /// <remarks>
    /// Every one of these fails silently. A wildcard or typo matches nothing, so feeds are never
    /// adopted and the user is prompted forever with no clue why; an entry with a path trusts the
    /// whole host regardless. The mere <em>presence</em> of trusted hosts is deliberately not an
    /// issue — that would make every project using the feature permanently unhealthy, which is how
    /// people learn to ignore <c>doctor</c>. Trust is reported informationally instead, via
    /// <see cref="DoctorResult.Trust"/>.
    /// </remarks>
    TrustedHostUnusable,
}
