namespace Dotquay.Core.Workflows;

/// <summary>The dotnet-side twin of <see cref="AddonOutdatedInfo"/> — one NuGet dependency's
/// outcome from <see cref="DependencyWorkflow.OutdatedAsync"/>.</summary>
public sealed record DotnetOutdatedInfo(string Name, string CurrentVersion, string? LatestVersion,
    bool IsOutdated);
