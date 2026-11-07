using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Dotquay.Core.Abstractions;

namespace Dotquay.Core.Doctor;

/// <summary>What <c>known_hosts</c> says about one host.</summary>
public enum KnownHostStatus {
    /// <summary>No entry matches. The first SSH fetch will fail with "Host key verification
    /// failed" unless something else (an <c>ssh_config</c> setting, an agent-forwarded
    /// configuration) supplies trust.</summary>
    NotFound,

    /// <summary>An ordinary or <c>@cert-authority</c> entry matches.</summary>
    Trusted,

    /// <summary>An <c>@revoked</c> entry matches. Worse than absent: ssh refuses and says so in
    /// wording that reads like a compromise, because it can be one.</summary>
    Revoked,
}

/// <summary>
/// Reads OpenSSH <c>known_hosts</c> files well enough to answer "is this host trusted on this
/// machine?" without running <c>ssh</c>.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately not implemented by shelling out to <c>ssh-keygen -F</c>, which would look simpler.
/// The bug this check exists to catch is precisely that a machine can have two OpenSSH installs --
/// on Windows, the one in <c>System32\OpenSSH</c> and the newer one Git for Windows ships -- with
/// different capabilities. Probing through one of them would inherit the very ambiguity being
/// diagnosed, and could fail at key exchange before ever reaching the question. Both binaries read
/// the same <c>~/.ssh/known_hosts</c>, so the file is the more reliable witness as well as the
/// cheaper one.
/// </para>
/// <para>
/// Reading it means implementing OpenSSH's matching rules, hashed entries included. That is a real
/// format with real corners -- a host absent from a hashed file looks identical to a host absent
/// from an empty one if the HMAC is skipped, and reporting "not trusted" for a host that plainly is
/// would make <c>doctor</c> the thing people learn to ignore.
/// </para>
/// </remarks>
public static class KnownHostsReader {
    private const string HashedPrefix = "|1|";

    /// <summary>
    /// The files OpenSSH consults, in the order it consults them: the user's, then the system's.
    /// </summary>
    /// <remarks>
    /// <c>UserKnownHostsFile</c> and <c>GlobalKnownHostsFile</c> can both be redirected in
    /// <c>ssh_config</c>, which is not parsed here. That is a deliberate floor on ambition: this
    /// check reports a missing entry as a likely cause, and a false "not trusted" on an exotic
    /// configuration costs a reader one puzzled minute. Guessing at <c>ssh_config</c> -- with its
    /// <c>Host</c> blocks, <c>Match</c> conditions and includes -- would cost far more than it saves.
    /// </remarks>
    public static IReadOnlyList<string> DefaultPaths(IEnvironmentReader environment, IFileSystem fileSystem) =>
        DefaultPaths(environment, fileSystem, OperatingSystem.IsWindows());

    /// <param name="isWindows">Which platform's machine-wide location to use. A parameter rather
    /// than an inline <see cref="OperatingSystem.IsWindows"/> so both branches are reachable from a
    /// test on either platform -- and because the first version of this decided it by sniffing the
    /// <c>ProgramData</c> variable, which is set on every normal Windows session and empty in some
    /// stripped-down ones, so a Windows machine could be told its known_hosts lived in
    /// <c>/etc/ssh</c>.</param>
    public static IReadOnlyList<string> DefaultPaths(
        IEnvironmentReader environment, IFileSystem fileSystem, bool isWindows) {
        var paths = new List<string>();

        if (environment.HomeDirectory is { Length: > 0 } home) {
            paths.Add(fileSystem.CombinePath(home, ".ssh", "known_hosts"));
            paths.Add(fileSystem.CombinePath(home, ".ssh", "known_hosts2"));
        }

        if (!isWindows) {
            paths.Add("/etc/ssh/ssh_known_hosts");
            return paths;
        }

        // ProgramData is where Windows keeps it, but the variable is not guaranteed to be present;
        // the literal is the documented default and is better than naming a Unix path on Windows.
        var programData = environment.GetVariable("ProgramData") is { Length: > 0 } value
            ? value
            : @"C:\ProgramData";

        paths.Add(fileSystem.CombinePath(programData, "ssh", "ssh_known_hosts"));

        return paths;
    }

    /// <summary>
    /// Looks <paramref name="host"/> up across <paramref name="paths"/>, skipping any that do not
    /// exist.
    /// </summary>
    /// <remarks>
    /// A revocation anywhere wins over a match anywhere else, which is how ssh itself behaves and the
    /// only safe way to combine several files: a key revoked in the system file must not be rescued
    /// by a stale entry in the user's.
    /// </remarks>
    public static KnownHostStatus Lookup(
        IFileSystem fileSystem, IEnumerable<string> paths, string host, int? port = null) {
        var target = port is null or 22
            ? host
            : $"[{host}]:{port.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)}";

        var status = KnownHostStatus.NotFound;

        foreach (var path in paths) {
            if (!fileSystem.FileExists(path)) {
                continue;
            }

            switch (LookupIn(fileSystem.ReadAllText(path), target)) {
                case KnownHostStatus.Revoked:
                    return KnownHostStatus.Revoked;
                case KnownHostStatus.Trusted:
                    status = KnownHostStatus.Trusted;
                    break;
            }
        }

        return status;
    }

    /// <summary>Scans one file's contents. Separated from the I/O so the format's corners can be
    /// tested as text.</summary>
    public static KnownHostStatus LookupIn(string knownHostsContent, string target) {
        var status = KnownHostStatus.NotFound;

        // A byte-order mark would otherwise become part of the first entry's hostname, silently
        // costing that one host its trust. Not hypothetical: PowerShell 5.1's '>>' writes UTF-16, so
        // 'ssh-keyscan host >> known_hosts' -- the exact command everyone reaches for -- prepends one.
        var content = knownHostsContent.TrimStart('\uFEFF', '\uFFFE');

        foreach (var rawLine in content.Split('\n')) {
            var line = rawLine.Trim();

            if (line.Length == 0 || line[0] == '#') {
                continue;
            }

            var marker = string.Empty;
            if (line[0] == '@') {
                var space = line.IndexOfAny([' ', '\t']);
                if (space < 0) {
                    continue;
                }

                marker = line[..space].ToLowerInvariant();
                line = line[(space + 1)..].TrimStart();
            }

            var patternsEnd = line.IndexOfAny([' ', '\t']);
            var patterns = patternsEnd < 0 ? line : line[..patternsEnd];

            if (!MatchesAnyPattern(patterns, target)) {
                continue;
            }

            // Keep scanning after a match: a later @revoked line for the same host must still win.
            if (marker == "@revoked") {
                return KnownHostStatus.Revoked;
            }

            status = KnownHostStatus.Trusted;
        }

        return status;
    }

    private static bool MatchesAnyPattern(string patternList, string target) {
        if (patternList.StartsWith(HashedPrefix, StringComparison.Ordinal)) {
            return MatchesHashed(patternList, target);
        }

        var matched = false;

        foreach (var pattern in patternList.Split(',', StringSplitOptions.RemoveEmptyEntries)) {
            // A negation anywhere in the list vetoes the whole line, even if another pattern in the
            // same list matched -- that is what makes "*.example.com,!secret.example.com" mean what
            // it reads as.
            if (pattern[0] == '!') {
                if (Matches(pattern[1..], target)) {
                    return false;
                }

                continue;
            }

            matched |= Matches(pattern, target);
        }

        return matched;
    }

    /// <summary>
    /// Matches a hashed entry, <c>|1|salt|hash</c>, where the hash is
    /// <c>HMAC-SHA1(key: salt, message: hostname)</c>.
    /// </summary>
    /// <remarks>
    /// Hashing is on by default on most Linux distributions, so skipping this would report every
    /// correctly-trusted host on such a machine as untrusted -- a check that is confidently wrong
    /// everywhere it is most likely to run. A hashed entry cannot carry a wildcard by construction,
    /// which is why this is an exact comparison and not a pattern match.
    /// </remarks>
    private static bool MatchesHashed(string entry, string target) {
        var parts = entry.Split('|');

        // "" , "1", salt, hash -- the leading empty element comes from the leading '|'.
        if (parts.Length != 4) {
            return false;
        }

        try {
            var salt = Convert.FromBase64String(parts[2]);
            var expected = Convert.FromBase64String(parts[3]);

            var actual = HMACSHA1.HashData(salt, Encoding.UTF8.GetBytes(target));

            return CryptographicOperations.FixedTimeEquals(actual, expected);
        } catch (FormatException) {
            // A hand-mangled line. Not this check's business to report, and certainly not a reason to
            // fail the command that asked.
            return false;
        }
    }

    private static bool Matches(string pattern, string target) {
        if (!pattern.Contains('*', StringComparison.Ordinal) && !pattern.Contains('?', StringComparison.Ordinal)) {
            // Hostnames are case-insensitive; a bracketed [host]:port form still compares fine this
            // way because the port digits have no case.
            return string.Equals(pattern, target, StringComparison.OrdinalIgnoreCase);
        }

        var regex = "^" + Regex.Escape(pattern).Replace("\\*", ".*", StringComparison.Ordinal)
            .Replace("\\?", ".", StringComparison.Ordinal) + "$";

        return Regex.IsMatch(target, regex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    }
}
