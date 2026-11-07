namespace Dotquay.Core.Workflows;

/// <summary>
/// What <c>dotquay add --preview</c> found: everything the real <c>add</c> would do, before it does
/// any of it (SPEC.md §6, §11.3).
/// </summary>
/// <remarks>
/// The editor plugin's confirmation step exists because two of an <c>add</c>'s consequences are
/// invisible from the URL the user typed: the addons it pulls in transitively, and the NuGet feeds
/// those addons declare. Both are only knowable after a full graph walk, so the preview is a real
/// resolve — it just writes nothing. Nothing here is inferred by the caller; a UI that renders this
/// record is showing what the resolver actually said.
/// </remarks>
public sealed record AddPreview {
    /// <summary>The key <c>add</c> would write into <c>[addons.*]</c>, as <see cref="AddonNaming"/>
    /// chooses it: the URL's name, or — for a second addon of the same repository — its install_as
    /// or path leaf.</summary>
    public required string Name { get; init; }

    public required string Git { get; init; }

    /// <summary>Every addon the graph resolves to, not only the new one — a transitive dependency
    /// shared with something already installed can still move, and hiding that is how a "just one
    /// addon" install surprises someone.</summary>
    public IReadOnlyList<PreviewAddon> Addons { get; init; } = [];

    /// <summary>NuGet packages the graph's addons declare. Existing project-declared packages are
    /// included so a UI can show the whole picture, flagged via
    /// <see cref="PreviewDotnet.IsNew"/>.</summary>
    public IReadOnlyList<PreviewDotnet> Dotnet { get; init; } = [];

    /// <summary>Feeds that would be taken on without asking — already trusted by host, or
    /// pre-approved for this run.</summary>
    public IReadOnlyList<PreviewSource> SourcesToAdopt { get; init; } = [];

    /// <summary>Feeds that need an explicit yes. Passing their URLs back as
    /// <c>--adopt-source</c> is what turns this preview into a completed add.</summary>
    public IReadOnlyList<PreviewSource> SourcesNeedingApproval { get; init; } = [];

    /// <summary>Non-fatal observations from the graph walk, same as the ones <c>graph</c>
    /// reports.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>
    /// Where dotquay believes the addon lives inside its repository, when
    /// <c>addons/&lt;name&gt;</c> turned out not to be there (v0.20). Null when the guess was
    /// right, when the caller stated a <c>path</c> itself, or when the repository offered no clear
    /// answer — that last case fails the preview rather than reporting, because there is nothing to
    /// confirm.
    /// </summary>
    /// <remarks>
    /// The second of the two questions a preview exists to surface, alongside
    /// <see cref="SourcesNeedingApproval"/>, and it works the same way: this preview's graph is
    /// resolved <em>as if</em> the suggestion were accepted, so what it reports is what a confirmed
    /// add installs. A caller confirms by passing <see cref="SubtreeSuggestion.Path"/> and
    /// <see cref="SubtreeSuggestion.Folder"/> back as <c>--path</c>/<c>--install-as</c>.
    /// </remarks>
    public SubtreeSuggestion? Subtree { get; init; }
}

/// <summary>
/// dotquay's guess at where an addon lives inside its own repository, and the evidence for it
/// (docs/DESIGN-v0.20-SUBTREE-GUESS.md §7).
/// </summary>
/// <param name="GuessedPath">The subtree that was looked for and isn't there — what the user would
/// otherwise have seen in a failure message, and the "from" half of the question.</param>
/// <param name="Path">The subtree to extract instead: the <c>--path</c> value, and what
/// <c>[addons.&lt;name&gt;] path</c> becomes.</param>
/// <param name="Folder">The folder under <c>addons/</c> it must install into: the
/// <c>--install-as</c> value. A Godot addon's own <c>res://addons/&lt;folder&gt;/</c> references
/// are written against this, so it travels with the path rather than being a separate
/// preference.</param>
/// <param name="Reason">A fixed wire string (<c>name_shape</c> / <c>only_addon</c>) naming the rule
/// that found it, so a UI can style or filter without re-deriving anything. Like
/// <c>DoctorIssueKind</c>, adding a value here is a contract change a consumer has to be
/// taught.</param>
/// <param name="Explanation">One sentence covering all of the above, written once here so every UI
/// explains the same finding the same way.</param>
public sealed record SubtreeSuggestion(
    string GuessedPath, string Path, string Folder, string Reason, string Explanation);

/// <param name="IsNew">False for an addon already locked at this exact commit — the row is shown so
/// the graph reads as a whole, but nothing about it would change.</param>
/// <param name="PreviousResolved">The commit currently locked, or null when this addon is not
/// locked at all yet. Non-null with <c>IsNew</c> true means "this one moves".</param>
/// <param name="RequiredBy">Who asked for it: the project itself, or the addons that depend on it.
/// This is what distinguishes "the thing I asked for" from "the four things it drags in".</param>
public sealed record PreviewAddon(
    string Name,
    string Git,
    string? Version,
    string Resolved,
    string Folder,
    bool IsNew,
    string? PreviousResolved,
    IReadOnlyList<string> RequiredBy);

/// <param name="DeclaredByAddon">The addon whose package manifest asked for it, or null when the
/// project declares it directly.</param>
public sealed record PreviewDotnet(string Name, string? Constraint, string? DeclaredByAddon,
    bool IsNew);

/// <summary>A NuGet feed an addon in the graph declared.</summary>
/// <param name="IsTrustedHost">True when the project's <c>[nuget] trusted_hosts</c> already covers
/// this host. A UI should say so rather than presenting an automatic adoption as a free
/// choice.</param>
/// <param name="Packages">The package ID patterns this feed would own, which is the concrete scope
/// of what adopting it grants.</param>
public sealed record PreviewSource(
    string Name,
    string Url,
    string DeclaredByAddon,
    bool IsTrustedHost,
    IReadOnlyList<string> Packages,
    IReadOnlyList<string> PackageIds);
