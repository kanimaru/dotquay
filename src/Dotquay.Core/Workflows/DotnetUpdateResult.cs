using Dotquay.Core.Lockfile;

namespace Dotquay.Core.Workflows;

/// <summary>The dotnet-side twin of <see cref="AddonUpdateResult"/> — one NuGet dependency's
/// outcome from <see cref="DependencyWorkflow.UpdateAsync"/>.</summary>
public sealed record DotnetUpdateResult(string Name, string? PreviousResolved, LockedDotnet Locked)
{
    public bool Changed => PreviousResolved != Locked.Resolved;
}
