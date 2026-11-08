using Dotquay.Core.Dotnet.SourceBuild;
using Dotquay.Core.Lockfile;

namespace Dotquay.Core.Workflows;

/// <summary>What <see cref="DependencyWorkflow.InstallAsync"/> actually installed, split by kind
/// since addons and NuGet packages sync through entirely different pipelines (git subtree swap vs.
/// <c>.csproj</c> + <c>dotnet restore</c> — SPEC.md §5 vs §11).</summary>
public sealed record InstallResult(IReadOnlyList<LockedAddon> Addons,
    IReadOnlyList<LockedDotnet> Dotnet) {
    /// <summary>Packages left without a <c>PackageReference</c> because the project builds them
    /// from source (docs/DESIGN-v0.26-SOURCE-BUILT-PACKAGES.md). Still listed in
    /// <see cref="Dotnet"/>: they are locked like any other package.</summary>
    public IReadOnlyList<SourceBuiltPackage> SourceBuilt { get; init; } = [];

    /// <summary>One line per <see cref="SourceBuilt"/> package: what the CLI prints and what
    /// <c>--json</c> and the MCP tool carry in <c>data.messages</c> (the field the editor plugin
    /// shows), built here so no surface composes its own.</summary>
    public IReadOnlyList<string> SourceBuildNotes => SourceBuilt.Select(s => s.Describe()).ToList();

    /// <summary>One line per patch this run dropped because upstream now contains its change
    /// (<see cref="Sync.AddonInstaller"/>): the patch file is deleted and the addon is locked
    /// unpatched.</summary>
    public IReadOnlyList<string> PatchNotes { get; init; } = [];

    /// <summary>Every note for the person who ran it, after the results: what the CLI prints and
    /// what <c>--json</c> and the MCP tool carry in <c>data.messages</c>.</summary>
    public IReadOnlyList<string> Notes => [.. SourceBuildNotes, .. PatchNotes];
}