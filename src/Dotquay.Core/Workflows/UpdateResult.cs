using Dotquay.Core.Dotnet.SourceBuild;

namespace Dotquay.Core.Workflows;

/// <summary>What <see cref="DependencyWorkflow.UpdateAsync"/> re-resolved, split by kind (see
/// <see cref="InstallResult"/> for why).</summary>
/// <param name="Warnings">Non-fatal notes from the graph walk (SPEC.md §8) — most usefully, an addon
/// whose own package manifest couldn't be read or wasn't inside its installed subtree, so its
/// dependencies were skipped. Surfaced here because <c>update</c> is the command someone runs when a
/// transitive dependency fails to show up.</param>
public sealed record UpdateResult(
    IReadOnlyList<AddonUpdateResult> Addons,
    IReadOnlyList<DotnetUpdateResult> Dotnet,
    IReadOnlyList<string> Warnings) {
    /// <summary>Packages the sync left without a <c>PackageReference</c> because the project builds them
    /// from source (docs/DESIGN-v0.26-SOURCE-BUILT-PACKAGES.md). Only packages the sync actually wrote are
    /// here: <c>update</c> leaves an unchanged package's <c>.csproj</c> entry alone either way.</summary>
    public IReadOnlyList<SourceBuiltPackage> SourceBuilt { get; init; } = [];

    /// <summary>One line per <see cref="SourceBuilt"/> package, carried in <c>data.messages</c> (which the
    /// editor plugin shows). Distinct from <see cref="Warnings"/>, which stays in the envelope's own
    /// <c>warnings</c> as before.</summary>
    public IReadOnlyList<string> SourceBuildNotes => SourceBuilt.Select(s => s.Describe()).ToList();
}
