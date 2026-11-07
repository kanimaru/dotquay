using Dotquay.Core.Diagnostics;
using Xunit;

namespace Dotquay.Tests.Diagnostics;

public sealed class SecretRedactorTests {
    [Theory]
    // user:password in a URL keeps the user, masks the password.
    [InlineData("https://oauth2:s3cr3t@gitlab.example/x/y.git", "https://oauth2:***@gitlab.example/x/y.git")]
    [InlineData("fetching https://kani:hunter2@host/a and more", "fetching https://kani:***@host/a and more")]
    // A bare user-info on http(s) is how a token is often passed: masked whole.
    [InlineData("https://abcdef123@gitlab.example/x.git", "https://***@gitlab.example/x.git")]
    // An ssh user is not a secret and is what an ssh failure needs to show.
    [InlineData("ssh://git@gitlab.example:2222/x/y.git", "ssh://git@gitlab.example:2222/x/y.git")]
    [InlineData("git@gitlab.example:x/y.git", "git@gitlab.example:x/y.git")]
    // Query-string tokens.
    [InlineData("https://h/api/v4/projects/1?private_token=abc&per_page=5", "https://h/api/v4/projects/1?private_token=***&per_page=5")]
    [InlineData("https://h/x?a=1&access_token=abc", "https://h/x?a=1&access_token=***")]
    // Header-shaped text.
    [InlineData("Authorization: Bearer abc.def", "Authorization: Bearer ***")]
    [InlineData("http.extraHeader=Authorization: Basic dXNlcjpwYXNz", "http.extraHeader=Authorization: Basic ***")]
    [InlineData("PRIVATE-TOKEN: abc", "PRIVATE-TOKEN: ***")]
    // Inline option values.
    [InlineData("--token=abc", "--token=***")]
    // Known token shapes, wherever they appear.
    [InlineData("token glpat-AbCdEfGhIjKlMnOp was refused", "token *** was refused")]
    [InlineData("ghp_0123456789abcdefABCDEF", "***")]
    // Nothing to hide.
    [InlineData("https://gitlab.com/x/y.git", "https://gitlab.com/x/y.git")]
    [InlineData("kani@kani.dev wrote this", "kani@kani.dev wrote this")]
    [InlineData("", "")]
    public void Redact(string input, string expected) {
        Assert.Equal(expected, SecretRedactor.Redact(input));
    }

    [Fact]
    public void Redact_null_is_empty() {
        Assert.Equal(string.Empty, SecretRedactor.Redact(null));
    }

    [Fact]
    public void RedactArguments_masks_the_value_after_a_secret_option() {
        var redacted = SecretRedactor.RedactArguments(
            ["login", "gitlab.example", "--token", "abc123", "--project", "C:\\p", "--TOKEN", "x"]);

        Assert.Equal(["login", "gitlab.example", "--token", "***", "--project", "C:\\p", "--TOKEN", "***"], redacted);
    }

    [Fact]
    public void RedactArguments_redacts_each_argument_as_text() {
        var redacted = SecretRedactor.RedactArguments(["ls-remote", "https://u:p@h/r.git"]);

        Assert.Equal(["ls-remote", "https://u:***@h/r.git"], redacted);
    }

    [Fact]
    public void A_trailing_secret_option_with_no_value_is_kept_as_is() {
        Assert.Equal(["--token"], SecretRedactor.RedactArguments(["--token"]));
    }
}
