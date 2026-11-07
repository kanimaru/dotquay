namespace Dotquay.Core.Git;

/// <summary>How a git URL reaches its host. Identity, not preference: two URLs can name the same
/// repository over different transports.</summary>
public enum GitTransport {
    /// <summary>Either <c>git@host:path</c> (scp-like) or <c>ssh://host/path</c>.</summary>
    Ssh,

    Https,

    Http,

    /// <summary>The unauthenticated <c>git://</c> daemon protocol. Parsed for completeness; nothing
    /// rewrites to it, because it has no authentication story at all.</summary>
    Daemon,

    /// <summary>A filesystem path or <c>file://</c> URL. Never rewritten and never compared across
    /// transports -- a local clone is a different thing from the remote it came from.</summary>
    Local,
}

/// <summary>
/// A parsed git remote URL, normalised far enough that two spellings of the same repository compare
/// equal and one can be re-expressed over the other's transport.
/// </summary>
/// <remarks>
/// <para>
/// Exists because a git URL is a bare string in exactly two places where it must not be:
/// unification (<see cref="Dotquay.Core.Resolve.AddonUnifier"/> rejects a name required from
/// "different git repositories", comparing the raw strings ordinally) and transitive discovery (an
/// addon's own manifest hardcodes whatever transport its author uses, which every consumer then
/// inherits). Both treat <c>https://host/g/r</c> and <c>git@host:g/r.git</c> as unrelated, and both
/// are wrong to.
/// </para>
/// <para>
/// The concrete incident: a project added over HTTPS pulled in a transitive dependency declared as
/// <c>git@gitlab.kemomi.de:...</c>, so a developer with no SSH key -- or, as it happened, no host
/// key in <c>known_hosts</c> -- could not install a project every URL of which they had written as
/// HTTPS.
/// </para>
/// <para>
/// Parsing follows git's own rules rather than <see cref="Uri"/>'s, because the scp-like form
/// (<c>git@host:path</c>) is not a URI and <see cref="Uri"/> mis-parses it as scheme <c>git</c>
/// with an opaque body. The Windows drive-letter ambiguity that shape creates (<c>C:\repos\x</c>
/// reads as host <c>C</c>) is resolved the same way git resolves it: a single-letter authority
/// followed by a separator is a drive, not a host.
/// </para>
/// </remarks>
public sealed record GitUrl {
    private GitUrl(
        GitTransport transport,
        string? user,
        string host,
        int? port,
        string path,
        bool hadGitSuffix,
        bool scpLike,
        string original) {
        Transport = transport;
        User = user;
        Host = host;
        Port = port;
        Path = path;
        HadGitSuffix = hadGitSuffix;
        ScpLike = scpLike;
        Original = original;
    }

    public GitTransport Transport { get; }

    /// <summary>
    /// The authority's user, e.g. <c>git</c> in <c>git@host:x</c>.
    /// </summary>
    /// <remarks>
    /// Deliberately excluded from <see cref="Identity"/>. On every forge that matters the SSH user
    /// is a fixed protocol constant (<c>git@</c>), not a statement about which repository is meant
    /// -- treating it as identity would make one person's <c>gitlab@host:x</c> a different
    /// repository from another's <c>git@host:x</c>.
    /// </remarks>
    public string? User { get; }

    /// <summary>Lowercased: DNS is case-insensitive, so the casing carries no
    /// information.</summary>
    public string Host { get; }

    /// <summary>Null when the URL used the transport's default port, so <c>ssh://host:22/x</c> and
    /// <c>git@host:x</c> compare equal.</summary>
    public int? Port { get; }

    /// <summary>
    /// The repository path: forward slashes, no leading or trailing separator, no <c>.git</c>
    /// suffix.
    /// </summary>
    /// <remarks>
    /// Compared <em>case-sensitively</em> by <see cref="Identity"/>, unlike <see cref="Host"/>. The
    /// major forges do treat namespace and project case-insensitively, so this is stricter than it
    /// strictly needs to be for GitHub and GitLab -- chosen anyway because a self-hosted server
    /// backed by a case-sensitive filesystem genuinely serves two different repositories at two
    /// casings, and the failure modes are not symmetric: comparing too strictly reports a conflict
    /// the user resolves with one <c>[overrides]</c> line, while comparing too loosely silently
    /// unifies two distinct repositories into one install.
    /// </remarks>
    public string Path { get; }

    /// <summary>Whether the original spelling carried a <c>.git</c> suffix. Not part of identity;
    /// kept so <see cref="WithTransportOf"/> can mirror a project's own house style rather than
    /// imposing one.</summary>
    public bool HadGitSuffix { get; }

    /// <summary>Whether the original used the scp-like <c>user@host:path</c> form rather than a
    /// <c>scheme://</c> URL. Only meaningful for <see cref="GitTransport.Ssh"/>.</summary>
    public bool ScpLike { get; }

    /// <summary>The string as the user wrote it. What every message quotes back, and what a
    /// <see cref="GitTransport.Local"/> URL round-trips to unchanged.</summary>
    public string Original { get; }

    /// <summary>
    /// What makes two URLs the same repository: host and path, with transport, user, default port
    /// and <c>.git</c> suffix all discarded.
    /// </summary>
    /// <remarks>
    /// <see cref="GitTransport.Local"/> gets an identity nothing else can collide with. A path on
    /// this machine is not evidence about a remote, and the reverse inference -- deciding a local
    /// clone "is" some origin -- is exactly what would let a graph walk quietly substitute
    /// somebody's working copy for a pinned dependency.
    /// </remarks>
    public string Identity => Transport == GitTransport.Local
        ? "local:" + Original.Replace('\\', '/').TrimEnd('/')
        : Port is null
            ? Host + "/" + Path
            : Host + ":" + Port.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + "/" + Path;

    /// <summary>Whether both URLs name the same repository, whatever transport each uses.</summary>
    public bool SameRepositoryAs(GitUrl other) =>
        string.Equals(Identity, other.Identity, StringComparison.Ordinal);

    /// <summary>
    /// This repository re-expressed over <paramref name="template"/>'s transport, or this URL
    /// unchanged when the rewrite would be unsafe or pointless.
    /// </summary>
    /// <remarks>
    /// <para>Refuses in three cases, each for its own reason:</para>
    /// <list type="bullet">
    /// <item><description>Different hosts. A transport preference is a statement about how this
    /// machine reaches <em>one</em> host -- credentials, keys and firewall rules are all per-host
    /// -- and generalising it to another host is not supported by any
    /// evidence.</description></item>
    /// <item><description>Either side is <see cref="GitTransport.Local"/>. There is no path from a
    /// filesystem location to a remote URL, or back, that does not invent
    /// information.</description></item>
    /// <item><description>The template carries an explicit non-default port. The port belongs to
    /// the template's own path on that host, not necessarily to this
    /// repository.</description></item>
    /// </list>
    /// <para>The rendered form mirrors the template's spelling -- scp-like versus <c>ssh://</c>,
    /// and whether to write <c>.git</c> -- so a rewritten URL landing in a lockfile looks like the
    /// ones the project wrote by hand rather than announcing itself as machine-generated.</para>
    /// </remarks>
    public GitUrl WithTransportOf(GitUrl template) {
        if (Transport == template.Transport) {
            return this;
        }

        if (Transport == GitTransport.Local || template.Transport == GitTransport.Local) {
            return this;
        }

        if (!string.Equals(Host, template.Host, StringComparison.Ordinal)) {
            return this;
        }

        if (template.Port is not null) {
            return this;
        }

        return new GitUrl(
            template.Transport,
            template.Transport == GitTransport.Ssh ? template.User ?? "git" : null,
            Host,
            port: null,
            Path,
            template.HadGitSuffix,
            template.ScpLike,
            original: Render(template.Transport, template.Transport == GitTransport.Ssh
                ? template.User ?? "git" : null, Host, Path, template.HadGitSuffix,
                template.ScpLike));
    }

    /// <summary>The URL as a string git will accept. Round-trips <see cref="Original"/> for
    /// anything this type did not construct itself.</summary>
    public override string ToString() => Original;

    private static string Render(GitTransport transport, string? user, string host, string path,
        bool gitSuffix, bool scpLike) {
        var suffix = gitSuffix ? ".git" : string.Empty;

        if (transport == GitTransport.Ssh && scpLike) {
            return $"{user ?? "git"}@{host}:{path}{suffix}";
        }

        var scheme = transport switch {
            GitTransport.Ssh => "ssh",
            GitTransport.Https => "https",
            GitTransport.Http => "http",
            GitTransport.Daemon => "git",
            _ => "https",
        };

        // A username is never rendered into an http(s) URL. git would treat it as the credential
        // identity for that remote, which silently changes which stored credential matches -- a
        // transport rewrite has no business making that decision.
        var authority = transport == GitTransport.Ssh && user is not null ? $"{user}@{host}" : host;

        return $"{scheme}://{authority}/{path}{suffix}";
    }

    public static bool TryParse(string? raw, out GitUrl? url) {
        url = null;

        if (string.IsNullOrWhiteSpace(raw)) {
            return false;
        }

        var trimmed = raw.Trim();

        return trimmed.Contains("://", StringComparison.Ordinal)
            ? TryParseSchemed(trimmed, out url)
            : TryParseScpLikeOrLocal(trimmed, out url);
    }

    /// <summary>Parses, or throws. For call sites that have already validated the URL and would
    /// only wrap a false return in an exception anyway.</summary>
    public static GitUrl Parse(string raw) =>
        TryParse(raw, out var url) && url is not null
            ? url
            : throw new GitCommandException($"'{raw}' is not a git URL dotquay can parse.");

    private static bool TryParseSchemed(string raw, out GitUrl? url) {
        url = null;

        var separatorIndex = raw.IndexOf("://", StringComparison.Ordinal);
        var scheme = raw[..separatorIndex].ToLowerInvariant();
        var rest = raw[(separatorIndex + 3)..];

        // "git+ssh://" and friends: the transport is the half that names a protocol.
        if (scheme.StartsWith("git+", StringComparison.Ordinal)) {
            scheme = scheme[4..];
        }

        var transport = scheme switch {
            "ssh" => GitTransport.Ssh,
            "https" => GitTransport.Https,
            "http" => GitTransport.Http,
            "git" => GitTransport.Daemon,
            "file" => GitTransport.Local,
            _ => (GitTransport?)null,
        };

        if (transport is null) {
            return false;
        }

        if (transport == GitTransport.Local) {
            url = new GitUrl(GitTransport.Local, null, string.Empty, null,
                NormalizePath(rest, out _), false, false, raw);
            return true;
        }

        var slashIndex = rest.IndexOf('/');
        var authority = slashIndex < 0 ? rest : rest[..slashIndex];
        var pathPart = slashIndex < 0 ? string.Empty : rest[(slashIndex + 1)..];

        if (!TryParseAuthority(authority, out var user, out var host, out var port)
            || host.Length == 0) {
            return false;
        }

        var path = NormalizePath(pathPart, out var hadGitSuffix);
        if (path.Length == 0) {
            return false;
        }

        url = new GitUrl(transport.Value, user, host, NormalizePort(transport.Value, port), path,
            hadGitSuffix, false, raw);
        return true;
    }

    private static bool TryParseScpLikeOrLocal(string raw, out GitUrl? url) {
        url = null;

        var colonIndex = raw.IndexOf(':');

        if (colonIndex < 0) {
            url = Local(raw);
            return true;
        }

        var beforeColon = raw[..colonIndex];

        // git's own disambiguation, in order:
        //   "C:\repos\x" / "C:/repos/x" -> a Windows drive, because a one-letter host is not a
        // thing.
        //   "./sub:dir/x"               -> a path, because a real authority cannot contain a
        // separator.
        var isWindowsDrive = beforeColon.Length == 1
            && char.IsLetter(beforeColon[0])
            && colonIndex + 1 < raw.Length
            && (raw[colonIndex + 1] == '\\' || raw[colonIndex + 1] == '/');

        if (isWindowsDrive
            || beforeColon.Contains('/', StringComparison.Ordinal)
            || beforeColon.Contains('\\', StringComparison.Ordinal)) {
            url = Local(raw);
            return true;
        }

        if (!TryParseAuthority(beforeColon, out var user, out var host, out _)
            || host.Length == 0) {
            return false;
        }

        // No port here, and that is not an oversight: in the scp-like form a colon separates the
        // path, so "git@host:2222/x" means the path "2222/x" to git too. A port needs ssh:// form.
        var path = NormalizePath(raw[(colonIndex + 1)..], out var hadGitSuffix);
        if (path.Length == 0) {
            return false;
        }

        url = new GitUrl(GitTransport.Ssh, user, host, null, path, hadGitSuffix, scpLike: true,
            raw);
        return true;
    }

    private static GitUrl Local(string raw) =>
        new(GitTransport.Local, null, string.Empty, null, NormalizePath(raw, out _), false, false,
            raw);

    private static bool TryParseAuthority(string authority, out string? user, out string host,
        out int? port) {
        user = null;
        host = string.Empty;
        port = null;

        if (authority.Length == 0) {
            return false;
        }

        // Last '@', not first: a password-bearing authority ("user:pw@host") and an '@' inside a
        // username both end at the final separator.
        var atIndex = authority.LastIndexOf('@');
        if (atIndex >= 0) {
            user = authority[..atIndex];
            authority = authority[(atIndex + 1)..];
        }

        var colonIndex = authority.LastIndexOf(':');
        if (colonIndex >= 0) {
            var portText = authority[(colonIndex + 1)..];
            if (!int.TryParse(portText, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var parsedPort)) {
                return false;
            }

            port = parsedPort;
            authority = authority[..colonIndex];
        }

        host = authority.ToLowerInvariant();
        return host.Length > 0;
    }

    private static int? NormalizePort(GitTransport transport, int? port) {
        if (port is null) {
            return null;
        }

        var defaultPort = transport switch {
            GitTransport.Ssh => 22,
            GitTransport.Https => 443,
            GitTransport.Http => 80,
            GitTransport.Daemon => 9418,
            _ => -1,
        };

        return port.Value == defaultPort ? null : port;
    }

    private static string NormalizePath(string path, out bool hadGitSuffix) {
        hadGitSuffix = false;

        var normalized = path.Replace('\\', '/').Trim('/');

        while (normalized.Contains("//", StringComparison.Ordinal)) {
            normalized = normalized.Replace("//", "/", StringComparison.Ordinal);
        }

        if (normalized.EndsWith(".git", StringComparison.OrdinalIgnoreCase)
            && normalized.Length > 4) {
            hadGitSuffix = true;
            normalized = normalized[..^4];
        }

        return normalized;
    }
}
