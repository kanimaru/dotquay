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
public sealed record AddPreview
{
    /// <summary>The name <c>add</c> would derive from the URL, and therefore the key it would write
    /// into <c>[addons.*]</c>.</summary>
    public required string Name { get; init; }

    public required string Git { get; init; }

    /// <summary>Every addon the graph resolves to, not only the new one — a transitive dependency
    /// shared with something already installed can still move, and hiding that is how a "just one
    /// addon" install surprises someone.</summary>
    public IReadOnlyList<PreviewAddon> Addons { get; init; } = [];

    /// <summary>NuGet packages the graph's addons declare. Existing project-declared packages are
    /// included so a UI can show the whole picture, flagged via <see cref="PreviewDotnet.IsNew"/>.</summary>
    public IReadOnlyList<PreviewDotnet> Dotnet { get; init; } = [];

    /// <summary>Feeds that would be taken on without asking — already trusted by host, or
    /// pre-approved for this run.</summary>
    public IReadOnlyList<PreviewSource> SourcesToAdopt { get; init; } = [];

    /// <summary>Feeds that need an explicit yes. Passing their URLs back as
    /// <c>--adopt-source</c> is what turns this preview into a completed add.</summary>
    public IReadOnlyList<PreviewSource> SourcesNeedingApproval { get; init; } = [];

    /// <summary>Non-fatal observations from the graph walk, same as the ones <c>graph</c> reports.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

/// <param name="IsNew">False for an addon already locked at this exact commit — the row is shown so
/// the graph reads as a whole, but nothing about it would change.</param>
/// <param name="PreviousResolved">The commit currently locked, or null when this addon is not locked
/// at all yet. Non-null with <c>IsNew</c> true means "this one moves".</param>
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
public sealed record PreviewDotnet(string Name, string? Constraint, string? DeclaredByAddon, bool IsNew);

/// <summary>A NuGet feed an addon in the graph declared.</summary>
/// <param name="IsTrustedHost">True when the project's <c>[nuget] trusted_hosts</c> already covers
/// this host. A UI should say so rather than presenting an automatic adoption as a free choice.</param>
/// <param name="Packages">The package ID patterns this feed would own, which is the concrete scope of
/// what adopting it grants.</param>
public sealed record PreviewSource(
    string Name,
    string Url,
    string DeclaredByAddon,
    bool IsTrustedHost,
    IReadOnlyList<string> Packages,
    IReadOnlyList<string> PackageIds);
