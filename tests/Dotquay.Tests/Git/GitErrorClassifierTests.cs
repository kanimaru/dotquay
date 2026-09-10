using Dotquay.Core.Git;
using Xunit;

namespace Dotquay.Tests.Git;

/// <summary>
/// Covers reading git's stderr for a cause and a remedy.
/// </summary>
/// <remarks>
/// The stderr strings here are real captures, not paraphrases. Matching another program's prose only
/// works for as long as the prose is what was actually observed, and a test written from memory
/// quietly stops being evidence the first time it is wrong.
/// </remarks>
public class GitErrorClassifierTests
{
    // Captured from a fresh Windows machine fetching over SSH from gitlab.kemomi.de with no
    // known_hosts entry. Note the trailing sentence: git appends its generic access-rights advice to
    // an ssh failure that has nothing to do with access rights.
    private const string HostKeyStdErr =
        "Host key verification failed.\r\nfatal: Could not read from remote repository.\r\n\r\n"
        + "Please make sure you have the correct access rights\r\nand the repository exists.";

    private const string UnsupportedKexStdErr =
        "choose_kex: unsupported KEX method sntrup761x25519-sha512@openssh.com";

    private const string PublicKeyDeniedStdErr =
        "git@gitlab.kemomi.de: Permission denied (publickey).\r\nfatal: Could not read from remote repository.";

    private const string Url = "git@gitlab.kemomi.de:wild-alliance/mtoon-shader.git";

    // -----------------------------------------------------------------------------------------
    // Classification
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void HostKeyFailure_IsNotReportedAsAuthentication()
    {
        // The regression this whole type exists for. git's trailing "correct access rights" matched
        // the old auth markers, so a host-key rejection offered a login prompt for a problem no
        // credential can solve.
        Assert.Equal(GitFailureKind.HostKeyVerification, GitErrorClassifier.Classify(HostKeyStdErr));
        Assert.False(GitErrorClassifier.LooksLikeAuthenticationFailure(HostKeyStdErr));
    }

    [Fact]
    public void UnsupportedKex_IsAnAlgorithmMismatch() =>
        Assert.Equal(GitFailureKind.SshAlgorithmMismatch, GitErrorClassifier.Classify(UnsupportedKexStdErr));

    [Fact]
    public void PublicKeyDenied_IsStillAuthentication()
    {
        // The narrowing must not go too far: this one really is about the user's credentials, and
        // error.kind = "auth" is what lets the editor plugin offer a login.
        Assert.Equal(GitFailureKind.Authentication, GitErrorClassifier.Classify(PublicKeyDeniedStdErr));
        Assert.True(GitErrorClassifier.LooksLikeAuthenticationFailure(PublicKeyDeniedStdErr));
    }

    [Theory]
    [InlineData("remote: HTTP Basic: Access denied", GitFailureKind.Authentication)]
    [InlineData("fatal: could not read Username for 'https://host': terminal prompts disabled", GitFailureKind.Authentication)]
    [InlineData("ssh: Could not resolve hostname gitlab.kemomi.de: No such host is known.", GitFailureKind.HostUnreachable)]
    [InlineData("ssh: connect to host gitlab.kemomi.de port 22: Connection refused", GitFailureKind.HostUnreachable)]
    [InlineData("remote: Repository not found.", GitFailureKind.RepositoryNotFound)]
    [InlineData("Unable to negotiate with 10.0.0.1 port 22: no matching host key type found.", GitFailureKind.SshAlgorithmMismatch)]
    [InlineData("WARNING: REMOTE HOST IDENTIFICATION HAS CHANGED!", GitFailureKind.HostKeyVerification)]
    [InlineData("error: RPC failed; curl 92 HTTP/2 stream 5 was not closed cleanly", GitFailureKind.Unknown)]
    [InlineData("", GitFailureKind.Unknown)]
    [InlineData(null, GitFailureKind.Unknown)]
    public void Classify(string? stderr, GitFailureKind expected) =>
        Assert.Equal(expected, GitErrorClassifier.Classify(stderr));

    [Fact]
    public void RepositoryNotFound_IsNotAuthentication()
    {
        // GitHub returns this for a private repository fetched without credentials, but a typo in the
        // URL returns it too -- and a typo is at least as likely. Calling it auth sends the user to
        // configure a token when they need to fix a character.
        Assert.False(GitErrorClassifier.LooksLikeAuthenticationFailure("remote: Repository not found."));
    }

    // -----------------------------------------------------------------------------------------
    // Remediation
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void Remediation_NamesTheHostAndTheTwoSshBinaryTrap()
    {
        var remediation = GitErrorClassifier.Remediation(GitFailureKind.HostKeyVerification, Url);

        Assert.NotNull(remediation);
        Assert.Contains("gitlab.kemomi.de", remediation, StringComparison.Ordinal);
        Assert.Contains("ssh-keyscan", remediation, StringComparison.Ordinal);
        // The part that is invisible from the error itself: keyscan and git can be different binaries
        // with different capabilities, so the obvious fix can appear not to work.
        Assert.Contains("PATH", remediation, StringComparison.Ordinal);
    }

    [Fact]
    public void Remediation_UnparseableUrl_StillProducesAdvice()
    {
        var remediation = GitErrorClassifier.Remediation(GitFailureKind.HostKeyVerification, "not a url at all");

        Assert.NotNull(remediation);
        Assert.DoesNotContain("null", remediation, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Remediation_Unknown_IsNull() =>
        Assert.Null(GitErrorClassifier.Remediation(GitFailureKind.Unknown, Url));

    // -----------------------------------------------------------------------------------------
    // When git will not say why
    // -----------------------------------------------------------------------------------------

    // Exactly what a real published binary captured fetching over ssh from a host it did not trust:
    // ssh's "Host key verification failed." went to the terminal, not to the pipe git handed the
    // child, so this is all that arrives. The same fault on another machine yields HostKeyStdErr --
    // which is why guessing from this is wrong and pointing at doctor is right.
    private const string GenericRefusalStdErr =
        "fatal: Could not read from remote repository.\r\n\r\n"
        + "Please make sure you have the correct access rights\r\nand the repository exists.";

    [Fact]
    public void GenericRefusal_IsItsOwnKind_NotUnknown() =>
        Assert.Equal(GitFailureKind.RemoteUnreadable, GitErrorClassifier.Classify(GenericRefusalStdErr));

    [Fact]
    public void GenericRefusal_IsNotAuthentication()
    {
        // "correct access rights" is the whole reason this wording is a trap. It is git's boilerplate
        // for every transport failure, not a statement about credentials.
        Assert.False(GitErrorClassifier.LooksLikeAuthenticationFailure(GenericRefusalStdErr));
    }

    [Fact]
    public void AMoreSpecificCause_AlwaysWinsOverTheGenericWording()
    {
        // git appends the generic advice to the specific message, so the two co-occur constantly.
        // Reporting the vague half would throw away the only useful sentence in the output.
        Assert.Equal(GitFailureKind.HostKeyVerification, GitErrorClassifier.Classify(HostKeyStdErr));
        Assert.Equal(
            GitFailureKind.Authentication,
            GitErrorClassifier.Classify(PublicKeyDeniedStdErr + "\nPlease make sure you have the correct access rights"));
    }

    [Fact]
    public void GenericRefusal_OverSsh_PointsAtDoctorRatherThanGuessing()
    {
        var remediation = GitErrorClassifier.Remediation(GitFailureKind.RemoteUnreadable, Url);

        Assert.NotNull(remediation);
        Assert.Contains("dotquay doctor", remediation, StringComparison.Ordinal);
        Assert.Contains("ssh -T git@gitlab.kemomi.de", remediation, StringComparison.Ordinal);
    }

    [Fact]
    public void GenericRefusal_OverHttps_DoesNotTalkAboutSsh()
    {
        var remediation = GitErrorClassifier.Remediation(
            GitFailureKind.RemoteUnreadable, "https://gitlab.kemomi.de/wild-alliance/mtoon-shader");

        Assert.NotNull(remediation);
        Assert.DoesNotContain("ssh", remediation, StringComparison.OrdinalIgnoreCase);
    }

    // -----------------------------------------------------------------------------------------
    // The fetch message
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void DescribeFetchFailure_IdenticalStderrs_AreReportedOnce()
    {
        // A host that refuses one attempt refuses both. Printing the same paragraph twice under two
        // labels taught readers to skim past the part that mattered.
        var message = GitErrorClassifier.DescribeFetchFailure(Url, "abc123", HostKeyStdErr, HostKeyStdErr);

        Assert.DoesNotContain("Shallow fetch:", message, StringComparison.Ordinal);
        Assert.DoesNotContain("Full fetch:", message, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(message, "Host key verification failed"));
    }

    [Fact]
    public void DescribeFetchFailure_DifferingStderrs_AreBothReported()
    {
        // The case actually worth seeing: shallow refused for one reason, the fallback failing for
        // another.
        var message = GitErrorClassifier.DescribeFetchFailure(
            Url, "abc123", "error: Server does not allow request for unadvertised object", HostKeyStdErr);

        Assert.Contains("Shallow fetch:", message, StringComparison.Ordinal);
        Assert.Contains("Full fetch:", message, StringComparison.Ordinal);
    }

    [Fact]
    public void DescribeFetchFailure_CarriesTheRemedy()
    {
        var message = GitErrorClassifier.DescribeFetchFailure(Url, "abc123", HostKeyStdErr, HostKeyStdErr);

        Assert.Contains("ssh-keyscan gitlab.kemomi.de", message, StringComparison.Ordinal);
    }

    [Fact]
    public void DescribeFetchFailure_AlwaysNamesTheCommitAndUrl()
    {
        // Whatever else is unknown, these two are the facts the user needs to act at all.
        var message = GitErrorClassifier.DescribeFetchFailure(Url, "abc123", "", "");

        Assert.Contains("abc123", message, StringComparison.Ordinal);
        Assert.Contains(Url, message, StringComparison.Ordinal);
    }

    [Fact]
    public void DescribeFetchFailure_UnrecognisedError_KeepsGitsOwnWords()
    {
        // No hint is better than a wrong hint; git's message is then the best available answer.
        const string stderr = "error: RPC failed; curl 92 HTTP/2 stream 5 was not closed cleanly";

        var message = GitErrorClassifier.DescribeFetchFailure(Url, "abc123", stderr, stderr);

        Assert.Contains("RPC failed", message, StringComparison.Ordinal);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = haystack.IndexOf(needle, StringComparison.Ordinal);
        while (index >= 0)
        {
            count++;
            index = haystack.IndexOf(needle, index + needle.Length, StringComparison.Ordinal);
        }

        return count;
    }
}
