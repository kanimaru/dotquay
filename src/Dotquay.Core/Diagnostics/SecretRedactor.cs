using System.Text.RegularExpressions;

namespace Dotquay.Core.Diagnostics;

/// <summary>
/// Strips credentials out of anything headed for the diagnostic log.
/// </summary>
/// <remarks>
/// <para>
/// A verbose log is something people paste into an issue, so it must be safe to paste by
/// construction rather than by the reader remembering to scrub it. dotquay handles GitLab personal
/// access tokens and NuGet feed passwords (docs/DESIGN-v0.7-AUTH.md); any of them can turn up in a
/// git URL (<c>https://oauth2:glpat-...@host/...</c>), a query string (<c>?private_token=...</c>), a
/// command-line argument (<c>--token ...</c>) or a header echoed in an error message.
/// </para>
/// <para>
/// Deliberately conservative about what it keeps: an ssh user (<c>git@host</c>, <c>ssh://git@host</c>)
/// is not a secret and is exactly what someone debugging an ssh failure needs to see, so only a
/// user-info part that carries a password, or any user-info at all on http(s) - where a bare token in
/// the user slot is a common way to authenticate - is masked.
/// </para>
/// <para>
/// HTTP headers are never logged at all (see <see cref="DiagnosticHttpHandler"/>); this class is the
/// second line of defence for text that was never meant to carry a secret but might.
/// </para>
/// </remarks>
public static partial class SecretRedactor
{
    /// <summary>What a secret is replaced with.</summary>
    public const string Mask = "***";

    /// <summary>Arguments whose <em>next</em> argument is a secret.</summary>
    private static readonly HashSet<string> SecretValueOptions = new(StringComparer.OrdinalIgnoreCase)
    {
        "--token",
        "--password",
        "--secret",
        "--api-key",
    };

    /// <summary>Masks every credential this class knows how to recognise in free text.</summary>
    public static string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text ?? string.Empty;
        }

        var redacted = UrlUserInfo().Replace(text, RedactUserInfo);
        redacted = SecretQueryParameter().Replace(redacted, match => match.Groups["key"].Value + Mask);
        redacted = AuthorizationValue().Replace(redacted, match => match.Groups["prefix"].Value + Mask);
        redacted = InlineSecretOption().Replace(redacted, match => match.Groups["key"].Value + Mask);
        redacted = KnownTokenShape().Replace(redacted, Mask);
        return redacted;
    }

    /// <summary>
    /// Redacts an argument vector: each argument as free text, and the value following any option
    /// in <see cref="SecretValueOptions"/> wholesale - a token passed as its own argument has no
    /// surrounding syntax for <see cref="Redact"/> to recognise it by.
    /// </summary>
    public static IReadOnlyList<string> RedactArguments(IEnumerable<string> arguments)
    {
        var result = new List<string>();
        var maskNext = false;

        foreach (var argument in arguments)
        {
            if (maskNext)
            {
                result.Add(Mask);
                maskNext = false;
                continue;
            }

            result.Add(Redact(argument));
            maskNext = SecretValueOptions.Contains(argument);
        }

        return result;
    }

    private static string RedactUserInfo(Match match)
    {
        var scheme = match.Groups["scheme"].Value;
        var userInfo = match.Groups["userinfo"].Value;
        var colon = userInfo.IndexOf(':');

        if (colon >= 0)
        {
            // user:password - the user is useful ("which account did it try?"), the password is not.
            return $"{scheme}{userInfo[..colon]}:{Mask}@";
        }

        var isHttp = scheme.StartsWith("http", StringComparison.OrdinalIgnoreCase);
        return isHttp ? $"{scheme}{Mask}@" : match.Value;
    }

    [GeneratedRegex(@"(?<scheme>\b[a-zA-Z][a-zA-Z0-9+.\-]*://)(?<userinfo>[^/@\s]+)@", RegexOptions.CultureInvariant)]
    private static partial Regex UrlUserInfo();

    [GeneratedRegex(
        @"(?<key>[?&](?:private_token|access_token|job_token|token|password|api_key|apikey)=)[^&#\s]+",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex SecretQueryParameter();

    // "Authorization: Bearer abc", "PRIVATE-TOKEN: abc", "http.extraHeader=Authorization: Basic abc".
    [GeneratedRegex(
        @"(?<prefix>\b(?:authorization|private-token|job-token|x-nuget-apikey)\s*[:=]\s*(?:(?:basic|bearer|token)\s+)?)\S+",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex AuthorizationValue();

    // "--token=abc", "--password=abc".
    [GeneratedRegex(@"(?<key>--(?:token|password|secret|api-key)=)\S+", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex InlineSecretOption();

    // Token formats that are secrets wherever they appear: GitLab PATs/deploy/job tokens, GitHub tokens.
    [GeneratedRegex(@"\b(?:glpat|gldt|glcbt|gloas|ghp|gho|ghs|ghu|github_pat)[-_][A-Za-z0-9_\-]{8,}", RegexOptions.CultureInvariant)]
    private static partial Regex KnownTokenShape();
}
