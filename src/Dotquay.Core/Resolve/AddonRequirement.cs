using Dotquay.Core.Manifest;

namespace Dotquay.Core.Resolve;

/// <summary>One "X wants Y" edge in the dependency graph walk: the requirer's name paired with the
/// <see cref="AddonSpec"/> it declared. <see cref="Requirer"/> is
/// <see cref="GraphConflict.ProjectRequirerName"/> for the project's own top-level entries.</summary>
public sealed record AddonRequirement(string Requirer, AddonSpec Spec) {
    /// <summary>Flattens to the reporting shape — drops <see cref="AddonSpec.Path"/>, which is a
    /// fact about repo layout rather than something requirers disagree about (SPEC.md §8).</summary>
    public GraphRequirement ToGraphRequirement() =>
        new(Requirer, Spec.SourceKind, ConstraintText(Spec), Spec.Git);

    /// <summary>The one of <c>version</c>/<c>branch</c>/<c>rev</c> this spec actually set —
    /// <see cref="AddonSpec.SourceKind"/> guarantees exactly one is non-null.</summary>
    public static string ConstraintText(AddonSpec spec) => spec.SourceKind switch {
        AddonSourceKind.Rev => spec.Rev!,
        AddonSourceKind.Branch => spec.Branch!,
        _ => spec.Version!,
    };
}
