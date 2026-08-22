namespace Dependi.Core.Lockfile;

/// <summary>One resolved <c>[[dotnet]]</c> entry (SPEC.md §7.3).</summary>
public sealed class LockedDotnet
{
    public required string Name { get; init; }

    public required string Resolved { get; init; }
}
