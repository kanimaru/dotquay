namespace Dotquay.Core.Workflows;

/// <summary>The two kinds of dependency a manifest can declare: a git addon under <c>addons/</c>,
/// or a NuGet package in the <c>.csproj</c>.</summary>
/// <remarks>
/// Returned by <see cref="DependencyWorkflow.RemoveAsync"/>, which accepts either kind by name.
/// Before it returned anything, <c>remove --json</c> and the MCP remove tool reported every removal
/// as <c>"addon"</c>, because the caller had no way to know which branch had matched.
/// </remarks>
public enum DependencyKind {
    Addon,
    Dotnet,
}
