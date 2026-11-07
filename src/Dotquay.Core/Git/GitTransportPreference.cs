namespace Dotquay.Core.Git;

/// <summary>
/// How this project reaches each git host, inferred from the URLs the project's own manifest
/// declares -- and used to re-express a transitively-discovered URL on a known host over the same
/// transport.
/// </summary>
/// <remarks>
/// <para>
/// The problem it solves: an addon's package manifest hardcodes whatever transport its author uses.
/// A consumer who added every one of their own dependencies over HTTPS still inherits
/// <c>git@host:...</c> for anything those dependencies pull in, and then needs an SSH key, an agent,
/// and a <c>known_hosts</c> entry for a project they described entirely in HTTPS. The reverse hurts
/// too: an SSH-only shop inherits HTTPS URLs and gets credential prompts where their keys would have
/// worked.
/// </para>
/// <para>
/// Transport is the consuming machine's business, not the addon author's. The author's URL is the
/// authority on <em>which repository</em> (host and path, preserved exactly); the project's own
/// declarations are the authority on <em>how to get there</em>. Splitting it that way needs no new
/// configuration: a project that writes HTTPS URLs has already said everything required, and a fresh
/// clone on a new machine inherits the answer with it.
/// </para>
/// <para>
/// Deliberately not applied to the project's own top-level <c>[addons.*]</c> entries -- those are the
/// evidence, and rewriting them would be circular. <c>[overrides.*]</c> count as evidence too: an
/// override is the project speaking about a dependency, in the project's own voice.
/// </para>
/// </remarks>
public sealed class GitTransportPreference {
    private readonly IReadOnlyDictionary<string, GitUrl> _templatesByHost;
    private readonly IReadOnlySet<string> _ambiguousHosts;

    private GitTransportPreference(
        IReadOnlyDictionary<string, GitUrl> templatesByHost,
        IReadOnlySet<string> ambiguousHosts) {
        _templatesByHost = templatesByHost;
        _ambiguousHosts = ambiguousHosts;
    }

    /// <summary>A preference that rewrites nothing. What every code path gets when the project
    /// declares no usable URLs, so callers never branch on null.</summary>
    public static GitTransportPreference None { get; } = new(
        new Dictionary<string, GitUrl>(StringComparer.Ordinal),
        new HashSet<string>(StringComparer.Ordinal));

    /// <summary>Hosts the project described over more than one transport. Nothing on these hosts is
    /// rewritten -- exposed so a caller can say why rather than appearing to do nothing.</summary>
    public IReadOnlySet<string> AmbiguousHosts => _ambiguousHosts;

    /// <summary>
    /// Infers a preference from the URLs a project declares for itself, in declaration order.
    /// </summary>
    /// <remarks>
    /// A host the project describes two different ways gets no preference at all rather than a
    /// majority vote. Two transports for one host is a deliberate-looking act (a mirror, a
    /// read-only HTTPS remote next to a push-capable SSH one), and guessing which one a
    /// dependency wants would be inventing an answer the project did not give. When several
    /// declarations agree on transport but differ in spelling -- <c>.git</c> suffix, scp-like
    /// versus <c>ssh://</c> -- the first wins, so the result is stable for a given manifest.
    /// </remarks>
    public static GitTransportPreference FromProjectUrls(IEnumerable<string?> projectDeclaredUrls) {
        var templates = new Dictionary<string, GitUrl>(StringComparer.Ordinal);
        var ambiguous = new HashSet<string>(StringComparer.Ordinal);

        foreach (var raw in projectDeclaredUrls) {
            if (!GitUrl.TryParse(raw, out var url) || url is null) {
                continue;
            }

            // A local path says nothing about how to reach a remote host, and there is no host to
            // key it under.
            if (url.Transport == GitTransport.Local) {
                continue;
            }

            if (!templates.TryGetValue(url.Host, out var existing)) {
                templates[url.Host] = url;
                continue;
            }

            if (existing.Transport != url.Transport || existing.Port != url.Port) {
                ambiguous.Add(url.Host);
            }
        }

        foreach (var host in ambiguous) {
            templates.Remove(host);
        }

        return templates.Count == 0 && ambiguous.Count == 0
            ? None
            : new GitTransportPreference(templates, ambiguous);
    }

    /// <summary>
    /// Rewrites <paramref name="declaredUrl"/> to this project's transport for its host, reporting
    /// whether anything changed.
    /// </summary>
    /// <returns>
    /// True only when the result differs from the input. A caller can therefore treat true as "worth
    /// telling the user about" without re-comparing strings.
    /// </returns>
    public bool TryRewrite(string? declaredUrl, out string rewritten) {
        rewritten = declaredUrl ?? string.Empty;

        if (!GitUrl.TryParse(declaredUrl, out var url) || url is null) {
            return false;
        }

        if (!_templatesByHost.TryGetValue(url.Host, out var template)) {
            return false;
        }

        var result = url.WithTransportOf(template);
        if (string.Equals(result.Original, url.Original, StringComparison.Ordinal)) {
            return false;
        }

        rewritten = result.Original;
        return true;
    }
}
