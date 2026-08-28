using Dotquay.Core.Dotnet;

namespace Dotquay.Core.Abstractions;

/// <summary>
/// Builds the <see cref="INuGetClient"/> for a project's feeds, so <c>DependencyWorkflow</c> can be
/// tested without HTTP.
/// </summary>
public interface INuGetClientFactory
{
    /// <summary>
    /// A client over <paramref name="sources"/>, narrowed to one feed when
    /// <paramref name="pinnedSourceName"/> names it (SPEC.md §7.1's <c>source = "..."</c>).
    /// </summary>
    /// <remarks>
    /// Takes the whole set rather than a single URL because ordered multi-feed search is the
    /// behaviour, not a detail: handing the workflow one client per URL and letting it loop would
    /// scatter the "first match wins, but a 401 stops the search" rule across every call site.
    /// </remarks>
    INuGetClient Create(NuGetSourceSet sources, string? pinnedSourceName = null);
}
