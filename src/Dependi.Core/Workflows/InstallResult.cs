using Dependi.Core.Lockfile;

namespace Dependi.Core.Workflows;

/// <summary>What <see cref="DependencyWorkflow.InstallAsync"/> actually installed, split by kind
/// since addons and NuGet packages sync through entirely different pipelines (git subtree swap vs.
/// <c>.csproj</c> + <c>dotnet restore</c> — SPEC.md §5 vs §11).</summary>
public sealed record InstallResult(IReadOnlyList<LockedAddon> Addons, IReadOnlyList<LockedDotnet> Dotnet);
