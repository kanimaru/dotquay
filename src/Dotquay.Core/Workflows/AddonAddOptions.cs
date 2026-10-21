using Dotquay.Core.Manifest;

namespace Dotquay.Core.Workflows;

/// <summary>
/// What the caller asked for when adding a git addon, beyond the URL itself (SPEC.md §6).
/// </summary>
/// <remarks>
/// A record rather than five more parameters on <see cref="DependencyWorkflow.AddAsync"/>: they are
/// one coherent statement of intent ("this repo, on this ref, into this folder"), and every one of
/// them is optional. Leaving it null asks for the historical behaviour — pick the newest release tag.
/// </remarks>
public sealed record AddonAddOptions
{
    /// <summary>The <c>[addons.&lt;name&gt;]</c> key to write instead of the one <see cref="AddonNaming"/>
    /// would choose. Only needed when the repository's name, <see cref="InstallAs"/> and the last
    /// segment of <see cref="Path"/> are all already taken.</summary>
    public string? Name { get; init; }

    /// <summary>Version constraint, e.g. <c>"~2.5"</c> or an exact tag.</summary>
    public string? Version { get; init; }

    /// <summary>Branch to track. The lockfile still pins the resolved SHA (SPEC.md §7.1).</summary>
    public string? Branch { get; init; }

    /// <summary>Exact commit to pin.</summary>
    public string? Rev { get; init; }

    /// <summary>Subtree to extract, overriding the <c>addons/&lt;name&gt;</c> default.</summary>
    public string? Path { get; init; }

    /// <summary>Folder to install into, overriding the addon's own name (SPEC.md §7.1).</summary>
    public string? InstallAs { get; init; }

    /// <summary>Overwrite a folder <see cref="Dotquay.Core.Sync.DirtyChecker"/> would otherwise refuse to touch
    /// (SPEC.md P0-6) — an addon-sized escape hatch matching <c>update --force</c>. Needed the moment
    /// an addon occupies its destination without a matching lockfile entry: most often a previous
    /// <c>add</c> that installed the addon graph before failing on an unrelated later step (a dotnet
    /// package that couldn't resolve, say), leaving real, correctly-installed content the lockfile
    /// never got a chance to record.</summary>
    public bool Force { get; init; }

    /// <summary>
    /// The declared <c>[nuget.sources.*]</c> name to pin a NuGet package to (v0.14), i.e.
    /// <c>--source</c>. Null searches every declared feed in order.
    /// </summary>
    /// <remarks>
    /// <para>Only meaningful for a NuGet add; <c>AddCommand</c> refuses it against a git URL rather
    /// than accepting and ignoring it, the same way it refuses <c>--branch</c> against a package ID.</para>
    /// <para>v0.10's D8 declined to add flags for the <c>[dotnet.*]</c> asset arrays and swept
    /// <c>source</c> along by analogy. That analogy is the weak link: D8's argument is about three
    /// repeatable tag lists making an awkward CLI surface, and this is a single string that mirrors the
    /// <c>--version</c> flag already there.</para>
    /// </remarks>
    public string? NuGetSource { get; init; }

    /// <summary>True when the caller named a source explicitly, so the tag-seeking default should be
    /// skipped.</summary>
    public bool HasExplicitSource => Version is not null || Branch is not null || Rev is not null;

    /// <summary>Guards the manifest invariant that exactly one of version/branch/rev is set
    /// (SPEC.md §7.1) at the point the user states it, rather than letting a nonsense combination
    /// reach the manifest writer.</summary>
    public void Validate()
    {
        var named = new[] { Version, Branch, Rev }.Count(v => v is not null);

        if (named > 1)
        {
            throw new WorkflowException(
                "Pass at most one of --version, --branch, or --rev: an addon is pinned one way or the other, "
                + "not several at once (SPEC.md §7.1).");
        }
    }

    /// <summary>Turns a classified <c>#ref</c> into the matching field.</summary>
    public AddonAddOptions WithSource(AddonSourceKind kind, string value) => kind switch
    {
        AddonSourceKind.Branch => this with { Branch = value },
        AddonSourceKind.Rev => this with { Rev = value },
        _ => this with { Version = value },
    };
}
