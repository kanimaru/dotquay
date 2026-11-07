using System.Text.Json;
using Dotquay.Core.Git;
using Dotquay.Core.Json;
using Dotquay.Core.Workflows;
using Dotquay.Mcp.Logging;
using Xunit;

namespace Dotquay.Tests.Json;

/// <summary>
/// Error and warning text never echoes a credential back to the user, the editor plugin or an MCP
/// client - found while verifying v0.24: a failing <c>add</c> of
/// <c>https://oauth2:secret@host/x.git</c> printed the token in its <c>--json</c> error message.
/// </summary>
public sealed class ErrorRedactionTests {
    private const string SecretUrl = "https://oauth2:supersecret@gitlab.example/kani/x.git";
    private const string MaskedUrl = "https://oauth2:***@gitlab.example/kani/x.git";

    [Fact]
    public void JsonError_masks_its_message_on_construction() {
        var error = new JsonError(JsonErrorKind.Git, $"git ls-remote failed for {SecretUrl}");

        Assert.Equal($"git ls-remote failed for {MaskedUrl}", error.Message);
    }

    [Fact]
    public void JsonError_masks_a_message_assigned_through_with() {
        var error = new JsonError(JsonErrorKind.Git,
            "fine") with { Message = $"failed for {SecretUrl}" };

        Assert.Equal($"failed for {MaskedUrl}", error.Message);
    }

    [Fact]
    public void JsonError_keeps_equality_and_kind() {
        Assert.Equal(new JsonError("git", SecretUrl), new JsonError("git", MaskedUrl));
        Assert.Equal("git", new JsonError("git", "x").Kind);
    }

    [Fact]
    public void Classify_masks_the_message_of_a_wrapped_git_failure_and_keeps_its_kind() {
        var exception = new WorkflowException(
            $"'x': git ls-remote failed for {SecretUrl}: fatal: could not resolve host",
            new GitCommandException($"git ls-remote failed for {SecretUrl}"));

        var (kind, message) = JsonErrorClassifier.Classify(exception);

        Assert.Equal(JsonErrorKind.Git, kind);
        Assert.Equal($"'x': git ls-remote failed for {MaskedUrl}: fatal: could not resolve host",
            message);
    }

    [Fact]
    public void Classify_masks_the_http_backstop_message() {
        var (_, message) = JsonErrorClassifier.Classify(
            new HttpRequestException("https://h/v3/index.json?private_token=abc refused"));

        Assert.Equal("A NuGet source could not be reached: "
            + "https://h/v3/index.json?private_token=*** refused", message);
    }

    [Fact]
    public void Classify_still_recognises_a_missing_manifest() {
        var (kind,
            _) = JsonErrorClassifier.Classify(new WorkflowException("No dotquay.toml. Run 'dotquay "
            + "init' first."));

        Assert.Equal(JsonErrorKind.NoManifest, kind);
    }

    [Fact]
    public void Serialized_failure_envelope_carries_no_secret_and_keeps_the_wire_shape() {
        var json = DotquayJson.Write(JsonEnvelope<OperationJson>.Failure(
            "add", new JsonError(JsonErrorKind.Git, $"failed for {SecretUrl}"),
            [$"warned about {SecretUrl}"]));

        Assert.DoesNotContain("supersecret", json);

        var root = JsonDocument.Parse(json).RootElement;
        Assert.Equal($"failed for {MaskedUrl}", root.GetProperty("error").GetProperty("message")
            .GetString());
        Assert.Equal("git", root.GetProperty("error").GetProperty("kind").GetString());
        Assert.Equal($"warned about {MaskedUrl}", root.GetProperty("warnings")[0].GetString());
    }

    [Fact]
    public void Success_envelope_masks_warnings_and_tolerates_none() {
        var withWarnings = JsonEnvelope<OperationJson>.Success("update", new OperationJson(),
            [$"pinned {SecretUrl}"]);
        var withNone = JsonEnvelope<OperationJson>.Success("update", new OperationJson());

        Assert.Equal([$"pinned {MaskedUrl}"], withWarnings.Warnings);
        Assert.Empty(withNone.Warnings);
    }

    [Fact]
    public void DoctorIssue_masks_its_message_including_through_with() {
        var issue = new DoctorIssue(DoctorIssueKind.GitNotOnPath, $"could not reach {SecretUrl}");
        var changed = issue with { Message = "token glpat-AbCdEfGhIjKlMnOp found in dotquay.toml" };

        Assert.Equal($"could not reach {MaskedUrl}", issue.Message);
        Assert.Equal("token *** found in dotquay.toml", changed.Message);
    }

    [Fact]
    public void Mcp_request_log_masks_the_failure_line() {
        var writer = new StringWriter();
        using var log = new RequestLog(writer, ownsWriter: false);

        log.WriteFailure("7", "tools/call", TimeSpan.FromMilliseconds(3),
            new InvalidOperationException($"failed for {SecretUrl}\nsecond line"));

        var line = writer.ToString();
        Assert.Contains($"error=failed for {MaskedUrl}", line);
        Assert.DoesNotContain("supersecret", line);
        Assert.DoesNotContain("second line", line);
    }
}
