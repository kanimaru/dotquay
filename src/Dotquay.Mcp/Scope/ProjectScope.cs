namespace Dotquay.Mcp.Scope;

/// <summary>
/// The real <see cref="IProjectScope"/>: the fixed list of roots the server was started with (from
/// <c>dotquay mcp --project &lt;dir&gt;</c>), or its working directory when none was given (design
/// doc §4.3).
/// </summary>
/// <remarks>
/// <para>
/// A path is in scope when it equals a root or sits under one — a prefix check on the normalized
/// absolute paths, deliberately not delegated to <see cref="Uri"/> or a symlink-resolving API: dotquay
/// projects are ordinary directories on the same machine the server runs on, and the roots themselves
/// come from a trusted source (whoever started this process), so nothing here needs to defend against
/// adversarial path tricks — only against an agent naming a directory it was never given.
/// </para>
/// <para>
/// The set never changes after construction. A client can no longer widen it through the MCP
/// <c>roots</c> capability: that feature is deprecated as of specification version 2026-07-28
/// (SEP-2577), and dotquay dropped it rather than keep a legacy path alive.
/// </para>
/// </remarks>
public sealed class ProjectScope : IProjectScope
{
    private readonly string[] _roots;

    /// <param name="roots">The <c>--project</c> values the server was started with, in the order
    /// given. Empty means "no root was named" — the process's own current directory becomes the one
    /// and only root, matching how every other dotquay command defaults <c>--project</c>
    /// (<c>Dotquay.Cli.Commands.GlobalOptions.Resolve</c>).</param>
    public ProjectScope(IReadOnlyList<string> roots)
    {
        _roots = (roots.Count == 0 ? [Directory.GetCurrentDirectory()] : roots)
            .Select(Path.GetFullPath)
            .ToArray();
    }

    /// <summary>The roots this scope allows, for the tool descriptions and error messages that name
    /// them, and for <c>ResourceRegistry</c> to enumerate.</summary>
    public IReadOnlyList<string> Roots => _roots;

    public string Resolve(string? requestedPath)
    {
        var candidate = Path.GetFullPath(
            string.IsNullOrWhiteSpace(requestedPath) ? _roots[0] : requestedPath);

        foreach (var root in _roots)
        {
            if (IsWithin(root, candidate))
            {
                return candidate;
            }
        }

        throw new ScopeException(
            $"'{candidate}' is outside every directory this MCP server was started with "
            + $"(--project, or its working directory). Allowed: {string.Join(", ", _roots)}.");
    }

    /// <summary>Case-insensitive on Windows (where two paths differing only in case are the same
    /// file) and case-sensitive everywhere else — the same rule <c>Path</c> itself applies, applied
    /// here because a scope check that disagreed with the filesystem it's guarding would be a hole,
    /// not a safeguard.</summary>
    private static bool IsWithin(string root, string candidate)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var normalizedRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        return candidate.Equals(normalizedRoot, comparison)
            || candidate.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, comparison);
    }
}
