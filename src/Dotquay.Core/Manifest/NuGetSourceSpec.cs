using System.Text.RegularExpressions;

namespace Dotquay.Core.Manifest;

/// <summary>
/// One declared NuGet feed: <c>[nuget.sources.&lt;name&gt;] url = "..."</c> (SPEC.md §7.1, §11.1).
/// </summary>
/// <remarks>
/// There can be several because there have to be — GitLab publishes a NuGet feed <em>per project</em>,
/// so a Godot project consuming C# packages from two GitLab repositories has two feeds by
/// construction. Through v0.6 dotquay held a single <c>[project] nuget_source</c> string, which made
/// that arrangement impossible to express.
/// </remarks>
public sealed partial record NuGetSourceSpec(string Name, string Url) {
    /// <summary>
    /// Package ID patterns this feed owns, e.g. <c>["VBeami.*"]</c>. Empty means "only the IDs
    /// declared alongside it".
    /// </summary>
    /// <remarks>
    /// Drives <c>&lt;packageSourceMapping&gt;</c> (SPEC.md §11.1). Patterns rather than exact IDs
    /// because a private package's own transitive private dependencies would otherwise match no
    /// pattern, fall to the <c>*</c> catch-all, and be looked for on nuget.org — where they are not.
    /// Prefix patterns are NuGet's own documented answer to this, and an addon author is the one
    /// person who knows which ID prefix their packages occupy.
    /// </remarks>
    public IReadOnlyList<string> Packages { get; init; } = [];

    /// <summary>
    /// The addon whose package manifest declared this feed, when it wasn't declared by the project
    /// itself (SPEC.md §11.3). Null for a project-declared source.
    /// </summary>
    /// <remarks>
    /// Carried so the manifest can attribute an adopted block to whoever asked for it, and so
    /// <c>doctor</c> can answer "why is this feed here?". Not part of equality by value semantics
    /// anyone relies on — two sources are the same feed when their URLs match.
    /// </remarks>
    public string? DeclaredByAddon { get; init; }

    /// <summary>The name a legacy <c>[project] nuget_source</c> is read as. Grandfathered onto the
    /// bare <c>dotquay</c> NuGet.Config key so upgrading rewrites nothing (SPEC.md §11.1).</summary>
    public const string LegacyName = "default";

    /// <summary>The implicit public feed, appended last unless a declared source already points at
    /// it. Never authenticated.</summary>
    public const string PublicName = "nuget.org";

    public const string PublicUrl = "https://api.nuget.org/v3/index.json";

    public static NuGetSourceSpec Public { get; } = new(PublicName, PublicUrl);

    public bool IsPublicDefault => string.Equals(Url, PublicUrl, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The <c>&lt;add key="..."&gt;</c> this source owns in the project's <c>NuGet.Config</c>.
    /// </summary>
    /// <remarks>
    /// One special case, and it earns its keep: a source named <see cref="LegacyName"/> keeps the
    /// bare <c>dotquay</c> key that pre-v0.7 projects already have on disk. Without it, upgrading
    /// would rename the entry to <c>dotquay-default</c> and produce a spurious diff in every existing
    /// project's committed <c>NuGet.Config</c>.
    /// </remarks>
    public string ConfigKey => IsPublicDefault
        ? PublicName
        : Name == LegacyName ? "dotquay" : $"dotquay-{Name}";

    /// <summary>Prefix of every key dotquay owns and is therefore allowed to prune. The bare
    /// <c>dotquay</c> key matches this too.</summary>
    public const string ConfigKeyPrefix = "dotquay";

    /// <summary>
    /// Whether <paramref name="name"/> is usable as a source name.
    /// </summary>
    /// <remarks>
    /// The name is interpolated into a <c>NuGetPackageSourceCredentials_&lt;key&gt;</c> environment
    /// variable and into an XML attribute, so it can't be arbitrary. Note the limit of what this
    /// buys: <c>NuGetPackageSourceCredentials_dotquay-core</c> is a legal .NET environment variable
    /// name (which is all that matters, since dotquay sets it on a child process) but not one a POSIX
    /// shell can export. Anyone setting credentials by hand wants
    /// <c>DOTQUAY_NUGET_TOKEN_&lt;SOURCE&gt;</c>, which is normalised to <c>[A-Z0-9_]</c>.
    /// </remarks>
    public static bool IsValidName(string name) => NamePattern().IsMatch(name);

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]*$")]
    private static partial Regex NamePattern();
}
