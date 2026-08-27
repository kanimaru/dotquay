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
