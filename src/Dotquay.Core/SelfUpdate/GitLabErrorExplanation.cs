using System.Net;

namespace Dotquay.Core.SelfUpdate;

/// <summary>
/// Turns a GitLab refusal into a sentence worth acting on.
/// </summary>
/// <remarks>
/// <para>Its own type, and pure, so the one part of the release channel that is easy to get
/// confidently wrong can be tested without a GitLab.</para>
/// <para>The rule that matters: <b>say what GitLab said.</b> A fine-grained personal access token
/// answers a refusal with the exact permission it is missing — <c>"…requires a fine-grained personal
/// access token with the following project permissions: [Release: Read]."</c> — and dropping that in
/// favour of a guess about scopes is worse than saying nothing, because the guess sounds
/// authoritative. Measured against gitlab.kemomi.de: the same token that reads the package registry
/// happily (200 on <c>/packages</c>) is refused on <c>/releases</c>, and only the body explains
/// why.</para>
/// <para>The other rule, inherited from <see cref="Dotnet.NuGetV3Client"/>: branch on whether a
/// credential was sent, not on the status code alone. An unauthenticated 404 on a GitLab project
/// endpoint is ambiguous between "does not exist" and "not allowed to see"; with a credential it
/// is not.</para>
/// </remarks>
public static class GitLabErrorExplanation
{
    /// <summary>Whether this refusal is about credentials, and so should be reported as
    /// <c>error.kind = "auth"</c> rather than as a transport failure (SPEC.md §17) — which is what
    /// lets a UI offer a login instead of a retry.</summary>
    public static bool IsAuthFailure(HttpStatusCode status, bool authenticated) =>
        status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
        || (status == HttpStatusCode.NotFound && !authenticated);

    /// <param name="detail">GitLab's own <c>error_description</c> or <c>message</c>, when the body
    /// carried one.</param>
    /// <param name="authenticated">Whether a credential was sent at all.</param>
    /// <param name="host">The channel's host, for a message someone can act on without matching a
    /// URL by eye.</param>
    /// <param name="what">What was being attempted, e.g. "looking up the newest release".</param>
    public static string Describe(
        HttpStatusCode status,
        string? detail,
        bool authenticated,
        string host,
        string what)
    {
        var opening = $"The update channel ({host}) answered {(int)status} {status} while {what}.";

        // GitLab's own words first and verbatim when it gave any. Nothing this method could add
        // beats a server naming the permission it wants.
        if (!string.IsNullOrWhiteSpace(detail))
        {
            var quoted = $"{opening} GitLab said: {detail.Trim()}";

            return status == HttpStatusCode.Forbidden && authenticated
                ? quoted + " A fine-grained token grants permissions one at a time, so this is most "
                    + "likely a permission that is simply not ticked for this project rather than a "
                    + "bad token - the package registry and the release list are separate grants."
                : quoted;
        }

        if (!IsAuthFailure(status, authenticated))
        {
            return status == HttpStatusCode.NotFound
                ? opening + " A credential was accepted this far, so the channel URL is the likelier "
                    + "fault: it needs the project's numeric ID, as in /api/v4/projects/<id>."
                : opening;
        }

        return authenticated
            ? opening + " The stored credential was sent and refused. If it is a fine-grained token, "
                + "check that it grants read access to this project's releases; if it is a classic "
                + "one, that it has read_api and has not expired."
            // Deliberately does not assert that the channel is private. It might not be - the channel
            // is configurable, so this can be someone's own mirror, and dotquay's own project is
            // expected to be public once it ships. GitLab answers a project that does not exist and
            // one you may not see identically, so both readings stay on the table and the reader
            // picks. Asserting the wrong one is the mistake this whole type exists to stop making.
            : opening + " No credential was sent. GitLab answers the same way for a project that does "
                + "not exist and one you are not allowed to see, so this is either the wrong channel "
                + $"URL or a project that wants a login - 'dotquay login {host}'.";
    }
}
