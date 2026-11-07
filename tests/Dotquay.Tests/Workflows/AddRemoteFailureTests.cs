using Dotquay.Core.Git;
using Dotquay.Core.Workflows;
using Dotquay.Tests.Support;
using Xunit;

namespace Dotquay.Tests.Workflows;

/// <summary>
/// <c>dotquay add</c> against a remote that refuses.
/// </summary>
/// <remarks>
/// Found running the real binary with ssh public-key auth disabled: the ls-remote that <c>add</c>
/// starts with threw a <see cref="GitCommandException"/> nothing caught, and the CLI printed
/// "Unhandled exception" and a stack trace instead of the refusal and what to do about it.
/// </remarks>
public class AddRemoteFailureTests {
    private const string ProjectDir = "project";
    private const string SshUrl = "git@gitlab.kemomi.de:wild-alliance/vbeami_gdmp.git";

    private const string PublicKeyRefused =
        "git@gitlab.kemomi.de: Permission denied (publickey,password).\n"
        + "fatal: Could not read from remote repository.";

    [Fact]
    public async Task A_refused_remote_is_a_workflow_error_carrying_the_advice() {
        var fileSystem = new InMemoryFileSystem();
        fileSystem.WriteAllText($"{ProjectDir}/dotquay.toml", "[project]\n");

        var gitClient = new FakeGitClient();
        gitClient.RemoteFailuresByUrl[SshUrl] = new GitCommandException(
            GitErrorClassifier.DescribeRemoteFailure($"git ls-remote failed for {SshUrl}", SshUrl,
                PublicKeyRefused),
            isAuthenticationFailure: true);

        var workflow = new DependencyWorkflow(fileSystem, gitClient, "cache");

        var exception = await Assert.ThrowsAsync<WorkflowException>(() =>
            workflow.AddAsync(ProjectDir, SshUrl));

        Assert.Contains("never used for an ssh URL", exception.Message, StringComparison.Ordinal);

        // The auth classification must survive the wrap: it is what the CLI turns into
        // error.kind = "auth" for the editor plugin.
        var inner = Assert.IsType<GitCommandException>(exception.InnerException);
        Assert.True(inner.IsAuthenticationFailure);
    }

    [Fact]
    public void An_ls_remote_failure_gets_the_same_remediation_a_fetch_does() {
        var message = GitErrorClassifier.DescribeRemoteFailure(
            $"git ls-remote failed for {SshUrl}", SshUrl, PublicKeyRefused);

        Assert.StartsWith($"git ls-remote failed for {SshUrl}: git@gitlab.kemomi.de: Permission "
            + $"denied", message, StringComparison.Ordinal);
        Assert.Contains("ssh -T git@gitlab.kemomi.de", message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unrecognised_failure_is_left_in_gits_own_words() {
        var message = GitErrorClassifier.DescribeRemoteFailure("git ls-remote failed for x", "x",
            "something odd");

        Assert.Equal("git ls-remote failed for x: something odd", message);
    }
}