using Dotquay.Core.Auth;
using Dotquay.Core.Json;
using Dotquay.Core.Local;
using Dotquay.Core.Lockfile;
using Dotquay.Core.Manifest;
using Dotquay.Core.SelfUpdate;
using Dotquay.Core.Workflows;
using Xunit;

namespace Dotquay.Tests.Json;

/// <summary>
/// <see cref="JsonErrorClassifier"/> on the project's own files: a broken <c>dotquay.toml</c>,
/// <c>dotquay.lock</c> or <c>dotquay.local.toml</c> is <c>invalid_project_file</c>, bare or
/// wrapped, and its message (path and parser position) passes through unchanged. A broken
/// user-level file (<c>~/.dotquay/credentials.toml</c>, <c>config.toml</c>) is
/// <c>invalid_config_file</c> the same way.
/// </summary>
public sealed class JsonErrorClassifierTests {
    private const string Message = @"C:\p\dotquay.lock: invalid TOML.
C:\p\dotquay.lock(1,6) : error : Expected `=` after key but was `is`.";

    public static TheoryData<Exception> ParseFailures => new()
    {
        new LockfileParseException(Message),
        new ManifestParseException(Message),
        new LocalLinksParseException(Message),
        new WorkflowException(Message, new LockfileParseException(Message)),
        new WorkflowException(Message, new ManifestParseException(Message)),
        new WorkflowException(Message, new LocalLinksParseException(Message)),
    };

    [Theory]
    [MemberData(nameof(ParseFailures))]
    public void A_broken_project_file_is_invalid_project_file(Exception exception) {
        var (kind, message) = JsonErrorClassifier.Classify(exception);

        Assert.Equal(JsonErrorKind.InvalidProjectFile, kind);
        Assert.Equal("invalid_project_file", kind);
        Assert.Equal(Message, message);
        Assert.True(JsonErrorClassifier.IsInvalidProjectFile(exception));
    }

    [Fact]
    public void A_link_target_problem_stays_a_workflow_failure() {
        var exception = new WorkflowException("'x' is not a directory.",
            new LocalLinkException("'x' is not a directory."));

        Assert.Equal(JsonErrorKind.Workflow, JsonErrorClassifier.Classify(exception).Kind);
        Assert.False(JsonErrorClassifier.IsInvalidProjectFile(exception));
    }

    [Fact]
    public void A_missing_manifest_is_still_no_manifest() {
        var exception = new WorkflowException("No dotquay.toml. Run 'dotquay init' first.");

        Assert.Equal(JsonErrorKind.NoManifest, JsonErrorClassifier.Classify(exception).Kind);
    }

    private const string ConfigMessage = @"C:\home\.dotquay\credentials.toml: invalid TOML.
C:\home\.dotquay\credentials.toml(1,6) : error : Expected `=` after key but was `is`.";

    public static TheoryData<Exception> ConfigFileFailures => new()
    {
        new CredentialsFileException(ConfigMessage),
        new UserConfigFileException(ConfigMessage),
        new WorkflowException(ConfigMessage, new CredentialsFileException(ConfigMessage)),
        new WorkflowException(ConfigMessage, new UserConfigFileException(ConfigMessage)),
    };

    [Theory]
    [MemberData(nameof(ConfigFileFailures))]
    public void A_broken_user_config_file_is_invalid_config_file(Exception exception) {
        var (kind, message) = JsonErrorClassifier.Classify(exception);

        Assert.Equal(JsonErrorKind.InvalidConfigFile, kind);
        Assert.Equal("invalid_config_file", kind);
        Assert.Equal(ConfigMessage, message);
        Assert.True(JsonErrorClassifier.IsInvalidConfigFile(exception));
        Assert.False(JsonErrorClassifier.IsInvalidProjectFile(exception));
    }

    [Fact]
    public void A_broken_credentials_file_is_still_a_credential_exception() {
        // The credential helper and login/logout catch CredentialException; a broken file must keep
        // reaching those catches (a clone must not fail on it), only its kind changes.
        Assert.IsAssignableFrom<CredentialException>(new CredentialsFileException(ConfigMessage));
    }

    [Fact]
    public void A_credential_problem_that_is_not_the_file_stays_auth() {
        var exception = new CredentialException("A credential needs both a username and a token.");

        Assert.Equal(JsonErrorKind.Auth, JsonErrorClassifier.Classify(exception).Kind);
        Assert.False(JsonErrorClassifier.IsInvalidConfigFile(exception));
    }
}
