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

    /// <summary>
    /// Project code loads something inside <c>addons/</c> by a bare <c>uid://</c> — the one reference
    /// shape that breaks when an addon's UIDs change.
    /// </summary>
    /// <remarks>
    /// Godot's scene format records a <c>uid</c> and a <c>path</c> together, so a scene whose uid stops
    /// resolving silently falls back to the path and repairs itself on the next save. A <c>uid://</c>
    /// string handed to <c>load()</c> has no such fallback: if the addon's uid changes, that call
    /// simply fails at runtime.
    /// <para>
    /// dotquay already avoids causing this — <see cref="Dotquay.Core.Sync.SyncEngine"/> carries
    /// generated <c>.uid</c> files across an update. But an addon that <em>starts</em> shipping its own
    /// uids replaces the local ones legitimately, and then only this reference shape breaks. Reported
    /// rather than rewritten: rewriting means dotquay editing the project's own scenes and scripts,
    /// which is a boundary the CLI otherwise never crosses (SPEC.md §18).
    /// </para>
    /// </remarks>
    FragileUidReference,

    /// <summary>
    /// An addon is installed in a folder whose casing its own <c>res://addons/…</c> references
    /// disagree with.
    /// </summary>
    /// <remarks>
    /// Broken in a way that looks like it works. The filesystem finds the files on Windows, so the
    /// addon loads; but Godot's <c>res://</c> paths are case-sensitive strings, so it repairs every
    /// reference to the real casing and re-saves the scenes and resources involved — leaving the
    /// addon permanently "modified" and the editor full of errors on each open. On a case-sensitive
    /// filesystem the same install simply fails.
    /// </remarks>
    AddonFolderCaseMismatch,

    /// <summary>
    /// A package's <c>.csproj</c> reference does not carry the asset metadata declared for it
    /// (v0.10 — <c>IncludeAssets</c>/<c>ExcludeAssets</c>/<c>PrivateAssets</c>).
    /// </summary>
    /// <remarks>
    /// This check is what makes "the manifest owns these elements where it declares them" a claim
    /// rather than a hope. It catches the two ways they drift apart: a hand-edit to the
    /// <c>.csproj</c>, and a manifest edit picked up by nothing because <c>install</c> deliberately
    /// replays the lockfile instead of re-reading the manifest. Reported rather than silently
    /// reconciled, because the <c>.csproj</c> is a file the user also owns.
    /// </remarks>
    PackageAssetMetadataDrift,

    /// <summary>
    /// An addon is installed in a folder other than the one the manifest declaring it asks for —
    /// an <c>install_as</c> added, changed, or removed since the lockfile was written.
    /// </summary>
    /// <remarks>
    /// Not corruption but a normal consequence of the pipeline: <c>install</c> replays the lockfile and
    /// never re-reads the manifest (SPEC.md §5), so the manifest can say one folder while the addon
    /// sits in another until an <c>update</c> re-resolves. Worth its own check because every symptom
    /// appears somewhere else — unresolved <c>preload("res://addons/…")</c> paths, an addon that looks
    /// installed twice, or an orphan report for the folder that is actually live.
    /// </remarks>
    InstallFolderDrift,

    /// <summary>
    /// A <c>[link.dotnet.*]</c> entry points at a <c>.csproj</c> that is no longer there (v0.11).
    /// </summary>
    /// <remarks>
    /// The overlay still names it, so every build fails with an MSBuild error about a missing project
    /// rather than anything mentioning dotquay. Usually the linked checkout was moved or deleted and
    /// the link outlived it.
    /// </remarks>
    LinkedProjectMissing,

    /// <summary>
    /// <c>addons/&lt;folder&gt;</c> is a directory link that disagrees with what
    /// <c>dotquay.local.toml</c> declares — a different target, or no declaration at all (v0.16).
    /// </summary>
    /// <remarks>
    /// The junction and the declaration are two independent statements about one folder, kept by
    /// different acts at different times, and they drift routinely: the checkout is moved, the link
    /// is made from one project while the declaration lives in another, or the gitignored local file
    /// is dropped by a fresh clone while the junction survives. Silent until an install reaches that
    /// folder and has to choose between writing a package into somebody else's repository and
    /// replacing their live link with a frozen copy. dotquay refuses both, so this reports the
    /// disagreement while it is still cheap. See docs/DESIGN-v0.16-LINK-SAFETY.md.
    /// </remarks>
    AddonLinkMismatch,

    /// <summary>
    /// <c>dotquay.local.targets</c> exists but the <c>.csproj</c> does not import it (v0.11).
    /// </summary>
    /// <remarks>
    /// A link that silently does nothing: the build quietly keeps using the published package, which
    /// is the exact failure the overlay design exists to prevent. Happens if the
    /// <c>&lt;Import&gt;</c> line is removed by hand or lost in a merge.
    /// </remarks>
    LocalOverlayNotImported,

    /// <summary>
    /// A locked addon is fetched over ssh from a host this machine has no trusted host key for --
    /// or whose key known_hosts marks as revoked (v0.15).
    /// </summary>
    /// <remarks>
    /// The check is offline: it reads known_hosts rather than connecting, so it costs nothing and
    /// works without a network. It exists because the failure it predicts is one of the worst this
    /// tool can hand somebody. On a machine that has never talked to the host, the fetch fails deep
    /// inside git with "Host key verification failed" followed by git's generic "make sure you have
    /// the correct access rights", which sends people to look for a credential problem that is not
    /// there. Nothing in the project is wrong, so nothing in the project can be edited to fix it --
    /// and the person hitting it is, by definition, the one least equipped to know that.
    /// <para>
    /// Revocation shares this kind rather than getting its own: it is rare enough that a second wire
    /// value, which the editor plugin and every other JSON consumer would have to learn, costs more
    /// than saying so in the message.
    /// </para>
    /// </remarks>
    SshHostNotTrusted,
}
