using System.Text.RegularExpressions;

namespace Dotquay.Core.Doctor;

/// <summary>
/// Spots things that look like credentials in a file meant to be committed (SPEC.md §11.2).
/// </summary>
/// <remarks>
/// <para>Deliberately narrow. This is not a secret scanner — it is a check for the one specific
/// mistake dotquay's own design invites: pasting a token into <c>dotquay.toml</c> next to the feed
/// URL, because that is where the URL lives and it looks like where the credential should go. Broad
/// entropy heuristics would produce false positives on git SHAs and content hashes, which this file
/// is full of, and a check that cries wolf gets ignored.</para>
/// <para>Prefix patterns are drawn from the providers dotquay actually targets. Missing a novel token
/// format is acceptable; flagging a legitimate commit SHA is not.</para>
/// </remarks>
public static partial class SecretScanner
{
    /// <summary>A short description of what was found, or null when nothing was. Never includes the
    /// matched text — a message quoting the secret would put it in terminal scrollback and bug
    /// reports, which is where it least belongs.</summary>
    public static string? Describe(string text)
    {
        if (ProviderTokenPattern().IsMatch(text))
        {
            return "what looks like an access token";
        }

        if (UrlCredentialPattern().IsMatch(text))
        {
            return "a URL with an embedded username and password";
        }

        return null;
    }

    /// <summary>GitLab PATs and deploy tokens, GitHub PATs (classic and fine-grained), and GitHub
    /// app/OAuth tokens.</summary>
    [GeneratedRegex(@"\b(glpat-|gldt-|glrt-|ghp_|gho_|ghu_|ghs_|ghr_|github_pat_)[A-Za-z0-9_\-]{8,}")]
    private static partial Regex ProviderTokenPattern();

    /// <summary>An <c>https://user:secret@host/…</c> authority. Excludes an empty password, which is
    /// how some tooling writes a username-only URL.</summary>
    [GeneratedRegex(@"https?://[^/\s:@""]+:[^/\s@""]+@")]
    private static partial Regex UrlCredentialPattern();
}
