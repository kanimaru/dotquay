using Dotquay.Core.Git;
using Xunit;

namespace Dotquay.Tests.Git;

/// <summary>
/// Authentication advice must match the URL's transport.
/// </summary>
/// <remarks>
/// A second developer hit an ssh fetch refused with <c>Permission denied (publickey,password)</c>,
/// followed the old advice to run <c>dotquay login</c>, and got the identical failure: dotquay hands
/// its token to git through a credential helper, and git only consults helpers over https.
/// </remarks>
public class GitAuthenticationRemediationTests {
    private const string SshUrl = "git@gitlab.kemomi.de:wild-alliance/vbeami_gdmp.git";
    private const string HttpsUrl = "https://gitlab.kemomi.de/wild-alliance/vbeami_gdmp.git";

    // Verbatim from the report, password retries included: ssh fell through to password auth.
    private const string PublicKeyRefused =
        "Permission denied, please try again.\n"
        + "Permission denied, please try again.\n"
        + "git@gitlab.kemomi.de: Permission denied (publickey,password).\n"
        + "fatal: Could not read from remote repository.\n\n"
        + "Please make sure you have the correct access rights\n"
        + "and the repository exists.";

    private const string HttpsRefused =
        "remote: HTTP Basic: Access denied\n"
        + "fatal: Authentication failed for 'https://gitlab.kemomi.de/wild-alliance/vbeami_gdmp.git/'";

    [Fact]
    public void The_reported_stderr_is_an_authentication_failure() =>
        Assert.Equal(GitFailureKind.Authentication, GitErrorClassifier.Classify(PublicKeyRefused));

    [Fact]
    public void An_ssh_refusal_says_a_stored_login_does_not_apply() {
        var advice = GitErrorClassifier.Remediation(GitFailureKind.Authentication, SshUrl)!;

        Assert.Contains("never used for an ssh URL", advice);
        Assert.Contains("ssh -T git@gitlab.kemomi.de", advice);
        Assert.Contains("[overrides.", advice);
    }

    [Fact]
    public void An_https_refusal_still_points_at_dotquay_login() {
        Assert.Equal(GitFailureKind.Authentication, GitErrorClassifier.Classify(HttpsRefused));
        var advice = GitErrorClassifier.Remediation(GitFailureKind.Authentication, HttpsUrl)!;

        Assert.Contains("dotquay login gitlab.kemomi.de", advice);
        Assert.DoesNotContain("ssh", advice);
    }

    [Fact]
    public void The_full_fetch_message_for_the_reported_case_leads_with_the_ssh_remedy() {
        var message = GitErrorClassifier.DescribeFetchFailure(
            SshUrl, "5f977e7ac86903e970c556cc93d7d05e542b68f0", PublicKeyRefused, PublicKeyRefused);

        Assert.Contains("refused the ssh key", message);
        Assert.DoesNotContain("'dotquay login gitlab.kemomi.de' stores a token", message);
    }
}