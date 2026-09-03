namespace Dotquay.Core.Resolve;

/// <summary>Why an addon that looks dotquay-aware was still treated as a leaf.</summary>
public enum GraphWarningKind
{
    /// <summary>The extracted subtree contains a <c>dotquay.toml</c>, but it isn't a valid package
    /// manifest — most often because it has no <c>[package]</c> header (SPEC.md §7.2).</summary>
    PackageManifestNotReadable,

    /// <summary>The extracted subtree has no <c>dotquay.toml</c>, but one exists elsewhere in the
    /// addon's repository. Almost always a layout mistake: the manifest was put at the repo root or
    /// in the surrounding Godot project instead of inside the addon folder.</summary>
    PackageManifestOutsideSubtree,

    /// <summary>Two addons declare the same NuGet package with different asset metadata (v0.10).
    /// First-discovered wins, as it already does for the version — but unlike a version, disagreeing
    /// metadata usually means one of the two addons will not get the behaviour it asked for, so it is
    /// worth saying. Declaring the package in the project's own manifest resolves it (SPEC.md §8).</summary>
    DotnetAssetMetadataConflict,
}

/// <summary>
/// A non-fatal observation from the graph walk (SPEC.md §8). Warnings never change resolution — the
/// addon is still treated as a leaf, exactly as before — they only stop the walk from being silent
/// about a case that is nearly always a mistake.
/// </summary>
/// <remarks>
/// Both kinds exist because of one real incident: an addon whose <c>dotquay.toml</c> sat in the Godot
/// project folder <em>above</em> its addon subtree resolved and installed perfectly while its own
/// dependencies were quietly dropped, with nothing anywhere saying so. Treating an unreadable or
/// misplaced manifest as "no manifest" is the right resolution behaviour and the wrong silence.
/// </remarks>
public sealed record GraphWarning(string AddonName, GraphWarningKind Kind, string Message);
