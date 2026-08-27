namespace Dotquay.Core.Workflows;

/// <summary>What <see cref="DependencyWorkflow.Status"/> found. Addons only — SPEC.md §6 scopes
/// <c>status</c> to "diff disk against lockfile hashes", and content hashes are an addon-only concept
/// (<see cref="Lockfile.LockedDotnet"/> has none; NuGet's own restore state is <c>dotnet restore</c>'s
/// business, not dotquay's to re-verify).</summary>
public sealed record StatusResult(IReadOnlyList<AddonStatusInfo> Addons);
