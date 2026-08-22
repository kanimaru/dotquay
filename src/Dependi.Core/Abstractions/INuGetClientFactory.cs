namespace Dependi.Core.Abstractions;

/// <summary>
/// Builds an <see cref="INuGetClient"/> for a given NuGet source. A project can point at a private
/// feed via <c>[project] nuget_source</c> in <c>dependi.toml</c> (SPEC.md §11) instead of the public
/// default, so the client to use isn't known until the manifest has been read — this is the seam
/// that lets <see cref="Workflows.DependencyWorkflow"/> resolve the right one per call instead of
/// baking a single source in at construction time.
/// </summary>
public interface INuGetClientFactory
{
    /// <summary>Returns a client for <paramref name="sourceUrl"/> (a V3 service-index URL).
    /// Null/empty means "the public default" (nuget.org).</summary>
    INuGetClient Create(string? sourceUrl);
}
