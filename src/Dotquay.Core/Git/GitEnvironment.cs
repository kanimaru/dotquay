using Dotquay.Core.Abstractions;

namespace Dotquay.Core.Git;

/// <summary>
/// Builds the environment every <c>git</c> child process gets (SPEC.md §11.2): prompts disabled, and
/// dotquay appended as a credential helper.
/// </summary>
/// <remarks>
/// A pure function in its own type specifically so it is testable. The alternative — building this
/// inline in <see cref="ProcessGitClient"/> — would put the two subtlest decisions in this file
/// (append-don't-reset, and the shell quoting) behind a real process launch, which is exactly the
/// kind of thing that gets verified once by hand and then silently regresses.
/// </remarks>
public static class GitEnvironment {
    /// <summary>The sub-command git invokes on dotquay. Must match the CLI command's name.</summary>
    public const string HelperCommandName = "credential-helper";

    /// <summary>
    /// Builds the child environment. <paramref name="executablePath"/> is dotquay's own binary —
    /// <see cref="Environment.ProcessPath"/>, never <c>Assembly.Location</c>, which returns an empty
    /// string under NativeAOT. Null or unusable means the helper is simply not registered: a config
    /// line pointing at nothing is worse than no config line, because git would report a helper
    /// failure instead of a missing credential.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Build(string? executablePath, IEnvironmentReader environment) {
        var result = new Dictionary<string, string>(StringComparer.Ordinal) {
            // The bug fix that stands on its own. ProcessGitClient redirects stdout and stderr but
            // not stdin, so before this, a repository needing credentials made git print a prompt
            // into a captured stream and wait forever on a stdin nobody was going to answer —
            // dotquay simply stopped, with the explanation invisible inside the captured stderr.
            ["GIT_TERMINAL_PROMPT"] = "0",
        };

        if (!TryBuildHelperValue(executablePath, out var helperValue)) {
            return result;
        }

        // Append at the next free index rather than assuming index 0. GIT_CONFIG_COUNT may already
        // be set by whatever launched dotquay, and overwriting KEY_0 would silently discard the
        // caller's own config entry.
        var existingCount = ParseCount(environment.GetVariable("GIT_CONFIG_COUNT"));

        result["GIT_CONFIG_COUNT"] = (existingCount + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
        result[$"GIT_CONFIG_KEY_{existingCount}"] = "credential.helper";
        result[$"GIT_CONFIG_VALUE_{existingCount}"] = helperValue;

        return result;
    }

    /// <summary>
    /// Formats the <c>credential.helper</c> value.
    /// </summary>
    /// <remarks>
    /// <para>The leading <c>!</c> tells git to run the value as a shell command rather than looking
    /// for a <c>git-credential-&lt;value&gt;</c> binary on PATH. It is also what makes quoting work:
    /// git splits an unquoted helper value on whitespace, so an install at
    /// <c>C:\Program Files\dotquay\dotquay.exe</c> would otherwise be invoked as the program
    /// <c>C:\Program</c>.</para>
    /// <para>Forward slashes because git-for-windows runs the command through its bundled <c>sh</c>,
    /// where a backslash is an escape character, not a separator.</para>
    /// <para>A path containing a double quote is refused rather than escaped. It cannot occur on
    /// Windows (the character is illegal in paths) and is pathological elsewhere; declining to
    /// register the helper degrades to "no stored credentials for git", while getting the escaping
    /// subtly wrong would hand a broken command to a shell.</para>
    /// </remarks>
    private static bool TryBuildHelperValue(string? executablePath, out string helperValue) {
        helperValue = string.Empty;

        if (string.IsNullOrWhiteSpace(executablePath) || executablePath.Contains('"', StringComparison.Ordinal)) {
            return false;
        }

        helperValue = $"!\"{executablePath.Replace('\\', '/')}\" {HelperCommandName}";
        return true;
    }

    private static int ParseCount(string? value) =>
        int.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var count)
        && count >= 0
            ? count
            : 0;
}
