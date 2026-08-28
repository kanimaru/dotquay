namespace Dotquay.Core.Json;

// The --json payload DTOs (SPEC.md §17). Deliberately separate types from the domain models in
// Dotquay.Core.Graph/.Resolve/.Workflows, and deliberately all-strings where the domain uses enums:
// renaming a C# enum member must never silently change a wire contract the Godot plugin parses. The
// mapping lives in one place (JsonMapper), so the contract is auditable by reading one file.
//
// Property names are PascalCase here and emitted as snake_case by the serializer's naming policy
// (see DotquayJsonContext) — GDScript reads dictionary keys, and snake_case is what its own
// conventions expect.

/// <summary>
/// Payload of <c>dotquay login --json</c>, <c>login --list --json</c>, and <c>logout --json</c>
/// (SPEC.md §11.2).
/// </summary>
/// <remarks>
/// There is deliberately no token field, and no truncated-token field either. A <c>glpat-</c> prefix
/// plus a host is already useful to whoever gets hold of the output and useless to the person reading
/// it — and this payload is the one most likely to be pasted into a bug report.
/// </remarks>
public sealed record LoginJson
{
    /// <summary>Entries touched or listed — scope and username only.</summary>
    public IReadOnlyList<CredentialEntryJson> Credentials { get; init; } = [];

    /// <summary>Where the credential file lives, so a UI can point at it without guessing the
    /// platform's home directory.</summary>
    public required string StorePath { get; init; }

    /// <summary>Whether the credential was checked against the server before being stored.
    /// False after <c>--no-verify</c>, so a consumer can distinguish "known good" from "accepted as
    /// given".</summary>
    public bool Verified { get; init; }

    public IReadOnlyList<string> Messages { get; init; } = [];
}

public sealed record CredentialEntryJson
{
    /// <summary><c>host</c> or <c>host/path/prefix</c> — the key the credential is stored under.</summary>
    public required string Scope { get; init; }

    public required string Username { get; init; }
}

/// <summary>Payload of <c>dotquay graph --json</c>.</summary>
public sealed record GraphJson
{
    /// <summary><c>"lockfile"</c> (no network) or <c>"resolved"</c> (live walk).</summary>
    public required string Source { get; init; }

    public required string ProjectDirectory { get; init; }

    /// <summary>The project's own <c>[addons.*]</c> keys — the graph's roots.</summary>
    public IReadOnlyList<string> Roots { get; init; } = [];

    public IReadOnlyList<GraphNodeJson> Addons { get; init; } = [];

    public IReadOnlyList<GraphEdgeJson> Edges { get; init; } = [];

    public IReadOnlyList<DotnetJson> Dotnet { get; init; } = [];

    public IReadOnlyList<ConflictJson> Conflicts { get; init; } = [];

    public IReadOnlyList<string> Overrides { get; init; } = [];

    /// <summary>Convenience mirror of <c>conflicts.is_empty()</c> — a UI reads one boolean to decide
    /// whether to show a warning banner.</summary>
    public bool HasConflicts { get; init; }
}

public sealed record GraphNodeJson
{
    public required string Name { get; init; }

    public required string Git { get; init; }

    public string? Version { get; init; }

    public string? Resolved { get; init; }

    /// <summary>First 7 characters of <see cref="Resolved"/>, precomputed so every consumer displays
    /// the same abbreviation the CLI prints.</summary>
    public string? ShortSha { get; init; }

    public required string Path { get; init; }

    /// <summary>The folder under <c>addons/</c> this addon installs into. Equals <c>name</c> unless
    /// <c>install_as</c> redirects it.</summary>
    public required string Folder { get; init; }

    /// <summary><c>"root"</c>, <c>"transitive"</c>, or <c>"orphan"</c>.</summary>
    public required string Origin { get; init; }

    /// <summary><c>"version"</c>, <c>"branch"</c>, <c>"rev"</c>, or null when this addon has no
    /// manifest entry of its own.</summary>
    public string? SourceKind { get; init; }

    public string? Constraint { get; init; }

    public bool IsOverridden { get; init; }

    public bool IsPatched { get; init; }

    /// <summary><c>"clean"</c>, <c>"patched"</c>, <c>"modified"</c>, <c>"missing"</c>, or null when
    /// nothing is locked for this name yet.</summary>
    public string? Status { get; init; }

    public IReadOnlyList<string> Dependencies { get; init; } = [];

    public IReadOnlyList<RequirementJson> Requirements { get; init; } = [];
}

/// <summary><see cref="From"/> is <c>"&lt;project&gt;"</c> for a root edge.</summary>
public sealed record GraphEdgeJson(string From, string To);

public sealed record RequirementJson(string Requirer, string Kind, string Constraint, string Git);

/// <summary>A concrete choice that would settle a conflict — shaped so the plugin can hand it
/// straight back as <c>dotquay override</c> arguments without reinterpreting anything.</summary>
public sealed record CandidateJson
{
    /// <summary><c>"version"</c>, <c>"branch"</c>, or <c>"rev"</c>.</summary>
    public required string Kind { get; init; }

    /// <summary>The tag, branch name, or SHA to pin to.</summary>
    public required string Value { get; init; }

    public required string Git { get; init; }

    public string? Resolved { get; init; }

    /// <summary>Which requirers this choice would satisfy — the picker's whole justification for
    /// ranking one option above another.</summary>
    public IReadOnlyList<string> Satisfies { get; init; } = [];

    public bool SatisfiesAll { get; init; }

    /// <summary>Folder to install into, for a <c>disagreeing_install_folders</c> conflict. Null for
    /// every other conflict kind, meaning "leave the folder alone".</summary>
    public string? InstallAs { get; init; }
}

public sealed record ConflictJson
{
    public required string Name { get; init; }

    /// <summary><c>"different_git_urls"</c>, <c>"disjoint_pins"</c>, <c>"no_common_version"</c>, or
    /// <c>"invalid_constraint"</c>.</summary>
    public required string Kind { get; init; }

    public required string Message { get; init; }

    public IReadOnlyList<RequirementJson> Requirements { get; init; } = [];

    public IReadOnlyList<CandidateJson> Candidates { get; init; } = [];
}

public sealed record DotnetJson
{
    public required string Name { get; init; }

    public string? Constraint { get; init; }

    public string? Resolved { get; init; }

    /// <summary>False for a package unioned in by an addon's own manifest (requirement P1-5) — the
    /// project manifest has no entry to remove, so a UI must not offer to remove it.</summary>
    public bool DeclaredByProject { get; init; }
}

/// <summary>Payload of <c>dotquay list --json</c>.</summary>
public sealed record ListJson
{
    public IReadOnlyList<ListAddonJson> Addons { get; init; } = [];

    public IReadOnlyList<DotnetJson> Dotnet { get; init; } = [];
}

public sealed record ListAddonJson
{
    public required string Name { get; init; }

    public string? Version { get; init; }

    public required string Resolved { get; init; }

    public required string ShortSha { get; init; }

    public required string Git { get; init; }

    public bool IsPatched { get; init; }
}

/// <summary>Payload of <c>dotquay status --json</c>.</summary>
public sealed record StatusJson
{
    public IReadOnlyList<StatusEntryJson> Addons { get; init; } = [];

    /// <summary>True when every addon is <c>clean</c> or <c>patched</c> — the one flag a UI badge
    /// needs.</summary>
    public bool AllClean { get; init; }
}

public sealed record StatusEntryJson(string Name, string Status);

/// <summary>Payload of <c>dotquay outdated --json</c>.</summary>
public sealed record OutdatedJson
{
    public IReadOnlyList<OutdatedEntryJson> Addons { get; init; } = [];

    public IReadOnlyList<OutdatedEntryJson> Dotnet { get; init; } = [];

    public int OutdatedCount { get; init; }
}

public sealed record OutdatedEntryJson(string Name, string Current, string? Latest, bool IsOutdated);

/// <summary>Payload of <c>dotquay doctor --json</c>.</summary>
public sealed record DoctorJson
{
    public IReadOnlyList<DoctorIssueJson> Issues { get; init; } = [];

    public bool Healthy { get; init; }

    /// <summary>What the project trusts and which feeds that trust brought in (SPEC.md §11.3).
    /// Additive and informational — it never affects <see cref="Healthy"/>, so a consumer that only
    /// reads <c>issues</c> and <c>healthy</c> is unchanged.</summary>
    public DoctorTrustJson Trust { get; init; } = new();
}

public sealed record DoctorTrustJson
{
    public IReadOnlyList<string> TrustedHosts { get; init; } = [];

    public IReadOnlyList<DoctorAdoptedSourceJson> AdoptedSources { get; init; } = [];
}

/// <summary>A feed that came from an addon rather than the project, and which addon asked.</summary>
public sealed record DoctorAdoptedSourceJson(string Name, string Url, string DeclaredBy);

public sealed record DoctorIssueJson(string Kind, string Message);

/// <summary>
/// Payload of <c>dotquay add --preview --json</c> (SPEC.md §6, §11.3): what an add would do, before
/// it does it.
/// </summary>
/// <remarks>
/// Its own payload type rather than a reshaped <see cref="OperationJson"/>, because a consumer's
/// response to it is the opposite of a mutating command's: an operation is reported so a UI can
/// refresh, a preview is reported so a UI can ask. Sharing the shape would mean sharing the parser
/// with code whose job is to react to changes that already happened.
/// </remarks>
public sealed record AddPreviewJson
{
    /// <summary>The name <c>add</c> would write into <c>[addons.*]</c>.</summary>
    public required string Name { get; init; }

    public required string Git { get; init; }

    public IReadOnlyList<PreviewAddonJson> Addons { get; init; } = [];

    public IReadOnlyList<PreviewDotnetJson> Dotnet { get; init; } = [];

    /// <summary>Feeds that would be taken on without asking: already trusted by host, or named in
    /// <c>--adopt-source</c>.</summary>
    public IReadOnlyList<PreviewSourceJson> SourcesToAdopt { get; init; } = [];

    /// <summary>Feeds needing an explicit yes. Pass their <c>url</c> values back as
    /// <c>--adopt-source</c> to complete the add.</summary>
    public IReadOnlyList<PreviewSourceJson> SourcesNeedingApproval { get; init; } = [];

    /// <summary>Convenience mirrors, so a UI reads one boolean per banner instead of measuring two
    /// arrays — the same reasoning as <see cref="GraphJson.HasConflicts"/>.</summary>
    public bool NeedsApproval { get; init; }

    /// <summary>True when nothing in the graph would move. An add can still be worth running (the
    /// manifest gains its entry), but the confirmation can say so plainly.</summary>
    public bool IsNoOp { get; init; }
}

/// <summary>One addon the graph resolves to.</summary>
public sealed record PreviewAddonJson
{
    public required string Name { get; init; }

    public required string Git { get; init; }

    public string? Version { get; init; }

    public required string Resolved { get; init; }

    public required string ShortSha { get; init; }

    /// <summary>The folder under <c>addons/</c> it would install into.</summary>
    public required string Folder { get; init; }

    /// <summary>False for an addon already locked at this exact commit — shown for context, but
    /// nothing about it changes.</summary>
    public bool IsNew { get; init; }

    /// <summary>The commit currently locked, or null when this addon isn't locked at all. Non-null
    /// alongside <see cref="IsNew"/> means the addon moves rather than arrives.</summary>
    public string? PreviousResolved { get; init; }

    /// <summary>Who requires it — <c>"&lt;project&gt;"</c> for the one the user asked for, addon names
    /// for everything it drags in.</summary>
    public IReadOnlyList<string> RequiredBy { get; init; } = [];
}

public sealed record PreviewDotnetJson
{
    public required string Name { get; init; }

    public string? Constraint { get; init; }

    /// <summary>The addon whose package manifest asked for it, or null when the project declares it
    /// directly.</summary>
    public string? DeclaredByAddon { get; init; }

    public bool IsNew { get; init; }
}

/// <summary>A NuGet feed an addon in the graph declared (SPEC.md §11.3).</summary>
public sealed record PreviewSourceJson
{
    public required string Name { get; init; }

    public required string Url { get; init; }

    public required string DeclaredByAddon { get; init; }

    /// <summary>True when <c>[nuget] trusted_hosts</c> already covers this host — adoption is
    /// automatic, and a UI should say so rather than present it as a choice.</summary>
    public bool IsTrustedHost { get; init; }

    /// <summary>The package ID patterns this feed would own: the concrete scope of what adopting it
    /// grants.</summary>
    public IReadOnlyList<string> Packages { get; init; } = [];

    /// <summary>The declaring addon's <c>[dotnet]</c> package IDs — the packages that actually depend
    /// on the answer.</summary>
    public IReadOnlyList<string> PackageIds { get; init; } = [];
}

/// <summary>
/// Payload of every <em>mutating</em> command's <c>--json</c> output — <c>add</c>, <c>remove</c>,
/// <c>update</c>, <c>install</c>, <c>override</c>, <c>patch</c>. One shape rather than six: a UI's
/// response to all of them is identical (report what moved, then refresh the graph), so six payload
/// types would be six parsers earning nothing.
/// </summary>
public sealed record OperationJson
{
    public IReadOnlyList<ChangeJson> Changes { get; init; } = [];

    /// <summary>Human-readable lines the CLI would otherwise have printed in color — surfaced so a UI
    /// can show the same explanation instead of inventing its own.</summary>
    public IReadOnlyList<string> Messages { get; init; } = [];
}

/// <summary>One thing an operation did.</summary>
public sealed record ChangeJson
{
    public required string Name { get; init; }

    /// <summary><c>"addon"</c> or <c>"dotnet"</c>.</summary>
    public required string Kind { get; init; }

    /// <summary><c>"added"</c>, <c>"updated"</c>, <c>"removed"</c>, <c>"installed"</c>, or
    /// <c>"unchanged"</c>.</summary>
    public required string Action { get; init; }

    public string? From { get; init; }

    public string? To { get; init; }
}
