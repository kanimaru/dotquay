namespace Dotquay.Core.Workflows;

/// <summary>What <see cref="DependencyWorkflow.RemoveAsync"/> removed.</summary>
/// <param name="Kind">Which kind of dependency the name turned out to be.</param>
/// <param name="StillRequiredBy">Reachable addons that still declare the package, so its lock entry
/// and <c>PackageReference</c> were kept and only the project's own declaration was removed
/// (DESIGN-v0.19 §7a.2). Empty when the removal was complete.</param>
public sealed record DependencyRemoval(DependencyKind Kind, IReadOnlyList<string> StillRequiredBy) {
    public static DependencyRemoval Complete(DependencyKind kind) => new(kind, []);

    /// <summary>True when only the manifest entry went away.</summary>
    public bool KeptForAddons => StillRequiredBy.Count > 0;

    /// <summary>A one-line human summary, shared by the CLI and the MCP tool.</summary>
    public string Describe(string name) => KeptForAddons
        ? $"'{name}' is no longer declared by your project. Kept at its locked version because "
          + $"{string.Join(", ", StillRequiredBy.Select(a => $"'{a}'"))} still "
          + $"{(StillRequiredBy.Count == 1 ? "needs" : "need")} it; the next update re-resolves it "
          + $"to {(StillRequiredBy.Count == 1 ? "that addon's" : "their")} range."
        : $"Removed '{name}'.";
}
