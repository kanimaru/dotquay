namespace Dotquay.Core.Git;

/// <summary>
/// Recognises an authentication failure in <c>git</c>'s stderr, so it can be reported as
/// <c>error.kind = "auth"</c> instead of a generic <c>"git"</c> (SPEC.md §17) — which is what lets a
/// UI offer a login prompt rather than showing a wall of text.
/// </summary>
/// <remarks>
/// String matching on another program's output is unpleasant, and it is the only option: git exits
/// 128 for almost everything and has no machine-readable failure categories. It is done here, at the
/// point that still holds the exit code and stderr, rather than in the presentation layer — so there
/// is exactly one list to correct when a git release rewords a message, and nothing downstream is
/// re-parsing prose.
/// </remarks>
internal static class GitErrorClassifier
{
    private static readonly string[] AuthenticationMarkers =
    [
        "authentication failed",
        "could not read username",
        "could not read password",
        "terminal prompts disabled",
        "http basic: access denied",
        "invalid username or password",
        "permission denied",
        "access denied",
        "403 forbidden",
        "401 unauthorized",
    ];

    /// <summary>
    /// Whether <paramref name="stderr"/> reads as "your credentials were missing or rejected".
    /// </summary>
    /// <remarks>
    /// Deliberately excludes "repository not found", even though GitHub returns exactly that for a
    /// private repository fetched without credentials. The same message is what a typo in the URL
    /// produces, and a typo is at least as likely — labelling it an auth failure would send the user
    /// to configure a token when what they need is to fix a character. Reported as a plain git error,
    /// whose message already names the URL that failed.
    /// </remarks>
    public static bool LooksLikeAuthenticationFailure(string stderr)
    {
        if (string.IsNullOrWhiteSpace(stderr))
        {
            return false;
        }

        var lowered = stderr.ToLowerInvariant();
        return AuthenticationMarkers.Any(marker => lowered.Contains(marker, StringComparison.Ordinal));
    }
}
