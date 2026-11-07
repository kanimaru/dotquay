using System.CommandLine;
using System.Text.Json;
using Dotquay.Cli.Commands;
using Dotquay.Core.Auth;
using Dotquay.Core.Lockfile;
using Dotquay.Core.SelfUpdate;
using Xunit;

namespace Dotquay.Tests.Cli;

/// <summary>
/// <see cref="UnhandledFailure"/>, the last resort for an exception no command caught: one envelope
/// under <c>--json</c>, the parser's message alone for a broken project file, and the stack trace
/// for anything else (SPEC.md section 17).
/// </summary>
/// <remarks>
/// <see cref="Console.SetOut"/> and <see cref="Console.SetError"/> are process-global, so these run
/// outside the parallel collections.
/// </remarks>
[Collection(nameof(UnhandledFailureTests))]
[CollectionDefinition(nameof(UnhandledFailureTests), DisableParallelization = true)]
public sealed class UnhandledFailureTests {
    private const string LockfileMessage = @"C:\p\dotquay.lock: invalid TOML.
C:\p\dotquay.lock(1,6) : error : Expected `=` after key but was `is`.";

    private const string CredentialsMessage = @"C:\home\.dotquay\credentials.toml: invalid TOML.
C:\home\.dotquay\credentials.toml(1,6) : error : Expected `=` after key but was `is`.";

    [Fact]
    public void Json_gets_one_envelope_named_after_the_subcommand_path() {
        var (exitCode, stdout, stderr) = Report(["source", "list", "--json"],
            new InvalidOperationException("boom"));

        Assert.Equal(1, exitCode);
        Assert.Equal("", stderr);

        using var document = JsonDocument.Parse(stdout);
        Assert.Equal("source-list", document.RootElement.GetProperty("command").GetString());
        Assert.Equal("unexpected", document.RootElement.GetProperty("error").GetProperty("kind")
            .GetString());
        Assert.Equal("boom", document.RootElement.GetProperty("error").GetProperty("message")
            .GetString());
    }

    [Fact]
    public void Json_reports_a_broken_lockfile_as_invalid_project_file() {
        var (exitCode, stdout, _) = Report(["status", "--json"],
            new LockfileParseException(LockfileMessage));

        Assert.Equal(1, exitCode);
        using var document = JsonDocument.Parse(stdout);
        Assert.Equal("status", document.RootElement.GetProperty("command").GetString());
        Assert.Equal("invalid_project_file", document.RootElement.GetProperty("error")
            .GetProperty("kind").GetString());
    }

    [Fact]
    public void Plain_output_of_a_broken_project_file_is_its_message_alone() {
        var (exitCode, stdout, stderr) = Report(["status"],
            new LockfileParseException(LockfileMessage));

        Assert.Equal(1, exitCode);
        Assert.Equal(LockfileMessage.ReplaceLineEndings(), stdout.TrimEnd().ReplaceLineEndings());
        Assert.Equal("", stderr);
    }

    [Fact]
    public void Json_reports_a_broken_credentials_file_as_invalid_config_file() {
        var (exitCode, stdout, _) = Report(["status", "--json"],
            new CredentialsFileException(CredentialsMessage));

        Assert.Equal(1, exitCode);
        using var document = JsonDocument.Parse(stdout);
        Assert.Equal("invalid_config_file", document.RootElement.GetProperty("error")
            .GetProperty("kind").GetString());
    }

    [Fact]
    public void Plain_output_of_a_broken_user_config_file_is_its_message_alone() {
        foreach (Exception exception in new Exception[]
        {
            new CredentialsFileException(CredentialsMessage),
            new UserConfigFileException(CredentialsMessage),
        }) {
            var (exitCode, stdout, stderr) = Report(["status"], exception);

            Assert.Equal(1, exitCode);
            Assert.Equal(CredentialsMessage.ReplaceLineEndings(), stdout.TrimEnd()
                .ReplaceLineEndings());
            Assert.Equal("", stderr);
        }
    }

    [Fact]
    public void Plain_output_of_anything_else_keeps_the_stack_trace() {
        var (exitCode, stdout, stderr) = Report(["status"], new InvalidOperationException("boom"));

        Assert.Equal(1, exitCode);
        Assert.Equal("", stdout);
        Assert.StartsWith("Unhandled exception: System.InvalidOperationException: boom", stderr);
    }

    [Fact]
    public void A_cancellation_prints_nothing() {
        var (exitCode, stdout, stderr) = Report(["status", "--json"],
            new OperationCanceledException());

        Assert.Equal(1, exitCode);
        Assert.Equal("", stdout);
        Assert.Equal("", stderr);
    }

    private static (int ExitCode, string Stdout, string Stderr) Report(string[] args,
        Exception exception) {
        var root = new RootCommand("test")
        {
            new Command("status") { JsonOption() },
            new Command("source") { new Command("list") { JsonOption() } },
        };

        var parseResult = root.Parse(args);
        Assert.Empty(parseResult.Errors);

        var originalOut = Console.Out;
        var originalError = Console.Error;
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        try {
            Console.SetOut(stdout);
            Console.SetError(stderr);
            var exitCode = UnhandledFailure.Report(parseResult, exception);
            return (exitCode, stdout.ToString(), stderr.ToString());
        } finally {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }

    private static Option<bool> JsonOption() => new("--json");
}
