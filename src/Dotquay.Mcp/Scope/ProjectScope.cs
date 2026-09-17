namespace Dotquay.Mcp.Scope;

/// <summary>
/// The real <see cref="IProjectScope"/>: the fixed list of roots the server was started with (from
/// <c>dotquay mcp --project &lt;dir&gt;</c>, repeatable), plus whatever a client has added at runtime
/// via <see cref="AddRoots"/> (design doc Â§4.3).
/// </summary>
/// <remarks>
/// A path is in scope when it equals a root or sits under one — a prefix check on the normalized
/// absolute paths, deliberately not delegated to <see cref="Uri"/> or a symlink-resolving API: dotquay
/// projects are ordinary directories on the same machine the server runs on, and the roots themselves
/// come from a trusted source (whoever started this process, or a client it is talking to over its own
/// negotiated capability), so nothing here needs to defend against adversarial path tricks — only
/// against an agent naming a directory it was never given.
/// </remarks>
public sealed class ProjectScope : IProjectScope
{
    private readonly object _lock = new();
    private List<string> _roots;

    /// <param name="roots">The <c>--project</c> values the server was started with, in the order
    /// given. Empty means "no root was named" — the process's own current directory becomes the one
    /// and only root, matching how every other dotquay command defaults <c>--project</c>
    /// (<c>Dotquay.Cli.Commands.GlobalOptions.Resolve</c>).</param>
    public ProjectScope(IReadOnlyList<string> roots)
    {
        _roots = (roots.Count == 0 ? [Directory.GetCurrentDirectory()] : roots)
            .Select(Path.GetFullPath)
            .ToList();
    }

    /// <summary>A snapshot of the roots this scope allows right now, for the tool descriptions and
    /// error messages that name them, and for <c>ResourceRegistry</c> to enumerate live.</summary>
    public IReadOnlyList<string> Roots
    {
        get
        {
            lock (_lock)
            {
                return _roots.ToArray();
            }
        }
    }

    public void AddRoots(IEnumerable<string> paths)
    {
        lock (_lock)
        {
            foreach (var path in paths)
            {
                if (string.IsNullOrWhiteSpace(path))
                {
                    continue;
                }

                var full = Path.GetFullPath(path);

                if (!_roots.Any(existing => PathsEqual(existing, full)))
                {
                    _roots.Add(full);
                }
            }
        }
    }

    public string Resolve(string? requestedPath)
    {
        var roots = Roots;
        var candidate = Path.GetFullPath(string.IsNullOrWhiteSpace(requestedPath) ? roots[0] : requestedPath);

        foreach (var root in roots)
        {
            if (IsWithin(root, candidate))
            {
                return candidate;
            }
        }

        throw new ScopeException(
            $"'{candidate}' is outside every directory this MCP server was started with or has been given "
            + $"via the client's roots (--project). Allowed: {string.Join(", ", roots)}.");
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

    private static bool PathsEqual(string a, string b)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        return string.Equals(
            a.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            b.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            comparison);
    }
}
