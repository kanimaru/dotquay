using System.Diagnostics.CodeAnalysis;

namespace Dotquay.Core.Auth;

/// <summary>
/// What a stored credential applies to: a host, optionally narrowed to a path prefix (SPEC.md
/// §11.2).
/// </summary>
/// <remarks>
/// Host-first is the whole point. GitLab publishes a NuGet feed per project — a dozen feeds on one
/// host — and issues one personal access token that works for all of them. Keying credentials by
/// feed, or by project, would ask the user to paste the same token a dozen times. The optional path
/// prefix exists for the case that motivates an exception: a narrower, differently-scoped token for
/// one project, which must be able to win without displacing the host-wide one.
/// </remarks>
public sealed record CredentialScope {
    private CredentialScope(string host, string path, bool isSecure) {
        Host = host;
        Path = path;
        IsSecure = isSecure;
    }

    /// <summary>Lowercased <c>host</c> or <c>host:port</c>. The port is present only when it was
    /// stated and isn't the scheme's default, so <c>https://example.com</c> and
    /// <c>https://example.com:443</c> are the same scope.</summary>
    public string Host { get; }

    /// <summary>Path prefix with no leading or trailing slash, or empty for a host-wide scope.
    /// Compared ordinally: hosts are case-insensitive by specification, paths are not.</summary>
    public string Path { get; }

    /// <summary>Whether a token may be sent here at all — HTTPS, or a loopback address. Checked at
    /// the store boundary so no call site can leak a token over cleartext by forgetting to
    /// ask.</summary>
    public bool IsSecure { get; }

    /// <summary>The canonical text form used as the TOML table key and shown by
    /// <c>dotquay login --list</c>.</summary>
    public string Key => Path.Length == 0 ? Host : $"{Host}/{Path}";

    /// <summary>How specific this scope is. Longest path wins when several scopes match
    /// (SPEC.md §11.2), so a per-project token overrides a host-wide one.</summary>
    public int Specificity => Path.Length;

    /// <summary>
    /// Parses a URL (<c>https://gitlab.example.com/api/v4/...</c>) or a bare host
    /// (<c>gitlab.example.com</c>, <c>gitlab.example.com/kani</c>) into a scope.
    /// </summary>
    /// <remarks>
    /// A bare host is assumed to be HTTPS. That's the safe direction to guess: it can only cause a
    /// refusal to send a token somewhere, never a token sent somewhere it shouldn't be.
    /// </remarks>
    public static CredentialScope Parse(string hostOrUrl) {
        if (!TryParse(hostOrUrl, out var scope, out var error)) {
            throw new CredentialException(error);
        }

        return scope;
    }

    public static bool TryParse(
        string hostOrUrl,
        [NotNullWhen(true)] out CredentialScope? scope,
        [NotNullWhen(false)] out string? error) {
        scope = null;
        error = null;

        var text = hostOrUrl?.Trim() ?? string.Empty;
        if (text.Length == 0) {
            error = "A host or URL is required (for example 'gitlab.example.com').";
            return false;
        }

        // A bare host has no scheme. Adding one is what makes Uri parse it as an authority rather
        // than as a relative path — "gitlab.example.com/kani" is otherwise just a path segment.
        var hasScheme = text.Contains("://", StringComparison.Ordinal);
        if (!Uri.TryCreate(hasScheme ? text : $"https://{text}", UriKind.Absolute, out var uri)) {
            error = $"'{hostOrUrl}' is not a valid host or URL.";
            return false;
        }

        if (uri.HostNameType == UriHostNameType.Unknown || uri.Host.Length == 0) {
            error = $"'{hostOrUrl}' has no host.";
            return false;
        }

        var isHttps = string.Equals(uri.Scheme, Uri.UriSchemeHttps,
            StringComparison.OrdinalIgnoreCase);
        var isHttp = string.Equals(uri.Scheme, Uri.UriSchemeHttp,
            StringComparison.OrdinalIgnoreCase);
        if (!isHttps && !isHttp) {
            // ssh:// and git:// carry no token — they authenticate with keys, which is git's job
            // and works today. Storing a password against them would be storing something unusable.
            error = $"'{hostOrUrl}' uses the '{uri.Scheme}' scheme. Only http and https "
                + "credentials are stored; SSH remotes authenticate with keys, which git already "
                + "handles.";
            return false;
        }

        var host = uri.IsDefaultPort
            ? uri.Host.ToLowerInvariant()
            : $"{uri.Host.ToLowerInvariant()}:{uri.Port}";

        scope = new CredentialScope(host, NormalizePath(uri.AbsolutePath), isHttps
            || uri.IsLoopback);
        return true;
    }

    /// <summary>Whether this scope covers <paramref name="other"/> — same host, and this path is a
    /// segment-boundary prefix of the other's. <c>a/b</c> covers <c>a/b/c</c> but never
    /// <c>a/bc</c>.</summary>
    public bool Covers(CredentialScope other) {
        if (!string.Equals(Host, other.Host, StringComparison.Ordinal)) {
            return false;
        }

        return Path.Length == 0
            || string.Equals(Path, other.Path, StringComparison.Ordinal)
            || other.Path.StartsWith(Path + "/", StringComparison.Ordinal);
    }

    public override string ToString() => Key;

    private static string NormalizePath(string absolutePath) => absolutePath.Trim('/');
}
