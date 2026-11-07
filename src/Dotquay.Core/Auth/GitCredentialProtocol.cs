namespace Dotquay.Core.Auth;

/// <summary>
/// git's credential helper wire format: <c>key=value</c> lines, terminated by a blank line, in both
/// directions (SPEC.md §11.2).
/// </summary>
/// <remarks>
/// Parsing and formatting live here, apart from the command that performs the I/O, so the protocol
/// is testable without a process and a pipe. The whole reason dotquay speaks this protocol rather
/// than rewriting remote URLs or passing <c>http.extraheader</c> is that the token travels over a
/// pipe: it is never written into <c>.git/config</c>, never appears in a command line, and so never
/// shows up in an error message or in another user's <c>ps</c> output.
/// </remarks>
public static class GitCredentialProtocol {
    /// <summary>
    /// Parses git's request into its key/value pairs. Unknown keys are kept — git adds fields over
    /// time (<c>wwwauth[]</c>, <c>capability[]</c>) and a helper that choked on one it didn't
    /// recognise would break on a git upgrade.
    /// </summary>
    public static IReadOnlyDictionary<string, string> ParseRequest(string input) {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var rawLine in input.Split('\n')) {
            var line = rawLine.TrimEnd('\r');
            if (line.Length == 0) {
                // A blank line terminates the request. Anything after it belongs to a later
                // exchange and is not ours to interpret.
                break;
            }

            var separator = line.IndexOf('=', StringComparison.Ordinal);
            if (separator <= 0) {
                continue;
            }

            result[line[..separator]] = line[(separator + 1)..];
        }

        return result;
    }

    /// <summary>
    /// Rebuilds the URL git is asking about, so it can be matched against a
    /// <see cref="CredentialScope"/>.
    /// </summary>
    /// <remarks>
    /// git sends a <c>url</c> key only when the caller supplied one; the normal case is separate
    /// <c>protocol</c>, <c>host</c>, and (optionally) <c>path</c> fields. <c>path</c> arrives only
    /// when <c>credential.useHttpPath</c> is set, which dotquay deliberately does not turn on —
    /// doing so would change how the *user's own* credential manager keys its stored entries, and
    /// breaking a working Git Credential Manager setup to enable a rarely-needed narrowing is a bad
    /// trade. The practical consequence: git credentials match host-wide scopes.
    /// </remarks>
    public static string? BuildUrl(IReadOnlyDictionary<string, string> request) {
        if (request.TryGetValue("url", out var url) && url.Length > 0) {
            return url;
        }

        if (!request.TryGetValue("host", out var host) || host.Length == 0) {
            return null;
        }

        var protocol = request.TryGetValue("protocol", out var value) && value.Length > 0 ? value : "https";
        var path = request.TryGetValue("path", out var requestPath) && requestPath.Length > 0
            ? "/" + requestPath.TrimStart('/')
            : string.Empty;

        return $"{protocol}://{host}{path}";
    }

    /// <summary>
    /// Formats the answer git expects. Trailing blank line included: git reads until one, and a
    /// helper that omits it leaves git waiting on a pipe that will never produce anything.
    /// </summary>
    public static string FormatResponse(Credential credential) =>
        $"username={Sanitize(credential.Username)}\npassword={Sanitize(credential.Token)}\n\n";

    /// <summary>
    /// Strips newlines from a value before it goes on the wire.
    /// </summary>
    /// <remarks>
    /// A protocol where the delimiter is a newline and the values come from a user-editable file is
    /// a protocol where an embedded newline injects a field. The store escapes newlines on the way
    /// in, so this should never fire — it is here because "should never" is not a guarantee, and the
    /// consequence of being wrong is a forged credential field rather than a rendering glitch.
    /// </remarks>
    private static string Sanitize(string value) => value.Replace("\r", string.Empty, StringComparison.Ordinal)
        .Replace("\n", string.Empty, StringComparison.Ordinal);
}
