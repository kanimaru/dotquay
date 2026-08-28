using Dotquay.Core.Git;
using Dotquay.Tests.Support;
using Xunit;

namespace Dotquay.Tests.Git;

/// <summary>
/// Covers the environment every <c>git</c> child gets. Extracted from <see cref="ProcessGitClient"/>
/// specifically so the two subtlest decisions here — append-don't-reset, and the shell quoting — are
/// asserted rather than verified once by hand and left to regress.
/// </summary>
public class GitEnvironmentTests
{
    [Fact]
    public void Build_AlwaysDisablesTerminalPrompts()
    {
        var environment = GitEnvironment.Build("/usr/local/bin/dotquay", new FakeEnvironmentReader());

        // The bug fix that stands on its own: ProcessGitClient redirects stdout and stderr but not
        // stdin, so without this a repository needing credentials made git print an invisible prompt
        // and wait forever — dotquay just stopped, with no message.
        Assert.Equal("0", environment["GIT_TERMINAL_PROMPT"]);
    }

    [Fact]
    public void Build_RegistersDotquayAsACredentialHelper()
    {
        var environment = GitEnvironment.Build("/usr/local/bin/dotquay", new FakeEnvironmentReader());

        Assert.Equal("1", environment["GIT_CONFIG_COUNT"]);
        Assert.Equal("credential.helper", environment["GIT_CONFIG_KEY_0"]);
        Assert.Equal("!\"/usr/local/bin/dotquay\" credential-helper", environment["GIT_CONFIG_VALUE_0"]);
    }

    [Fact]
    public void Build_AppendsAfterExistingConfigEntries_RatherThanOverwritingThem()
    {
        // GIT_CONFIG_COUNT may already be set by whatever launched dotquay. Writing KEY_0 regardless
        // would silently discard the caller's own config entry.
        var reader = new FakeEnvironmentReader().Set("GIT_CONFIG_COUNT", "2");

        var environment = GitEnvironment.Build("/usr/local/bin/dotquay", reader);

        Assert.Equal("3", environment["GIT_CONFIG_COUNT"]);
        Assert.Equal("credential.helper", environment["GIT_CONFIG_KEY_2"]);
        Assert.False(environment.ContainsKey("GIT_CONFIG_KEY_0"));
    }

    [Fact]
    public void Build_NeverEmptiesCredentialHelper()
    {
        var environment = GitEnvironment.Build("/usr/local/bin/dotquay", new FakeEnvironmentReader());

        // Setting credential.helper to "" resets the list, which would disable the machine's own SSH
        // agent or Git Credential Manager. dotquay appends itself as a fallback; it never displaces
        // a setup that already works.
        Assert.DoesNotContain(environment.Values, value => value.Length == 0);
    }

    [Fact]
    public void Build_WindowsPathWithSpaces_IsQuotedAndForwardSlashed()
    {
        var environment = GitEnvironment.Build(@"C:\Program Files\dotquay\dotquay.exe", new FakeEnvironmentReader());

        // git splits an unquoted helper value on whitespace, and git-for-windows runs the command
        // through its bundled sh, where a backslash escapes rather than separates.
        Assert.Equal("!\"C:/Program Files/dotquay/dotquay.exe\" credential-helper", environment["GIT_CONFIG_VALUE_0"]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Build_NoExecutablePath_SkipsTheHelperButStillDisablesPrompts(string? path)
    {
        // Environment.ProcessPath can be null. A config line pointing at nothing is worse than no
        // config line: git would report a helper failure instead of a missing credential.
        var environment = GitEnvironment.Build(path, new FakeEnvironmentReader());

        Assert.Equal("0", environment["GIT_TERMINAL_PROMPT"]);
        Assert.False(environment.ContainsKey("GIT_CONFIG_COUNT"));
    }

    [Fact]
    public void Build_PathContainingADoubleQuote_IsRefusedRatherThanEscaped()
    {
        // Impossible on Windows and pathological elsewhere. Degrading to "no stored git credentials"
        // beats handing a subtly mis-escaped command to a shell.
        var environment = GitEnvironment.Build("/opt/we\"ird/dotquay", new FakeEnvironmentReader());

        Assert.False(environment.ContainsKey("GIT_CONFIG_VALUE_0"));
    }

    [Fact]
    public void Build_GarbageExistingCount_FallsBackToZeroInsteadOfThrowing()
    {
        var reader = new FakeEnvironmentReader().Set("GIT_CONFIG_COUNT", "not-a-number");

        var environment = GitEnvironment.Build("/usr/local/bin/dotquay", reader);

        Assert.Equal("1", environment["GIT_CONFIG_COUNT"]);
        Assert.Equal("credential.helper", environment["GIT_CONFIG_KEY_0"]);
    }
}
