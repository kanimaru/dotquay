using System.CommandLine;
using Dotquay.Cli;
using Dotquay.Cli.Commands;
using Dotquay.Cli.Diagnostics;
using Dotquay.Core.Diagnostics;

var rootCommand = new RootCommand("dotquay - a package manager for Godot projects")
{
    InitCommand.Build(),
    AddCommand.Build(),
    RemoveCommand.Build(),
    InstallCommand.Build(),
    UpdateCommand.Build(),
    ListCommand.Build(),
    GraphCommand.Build(),
    OverrideCommand.Build(),
    SourceCommand.Build(),
    LinkCommand.Build(),
    OutdatedCommand.Build(),
    StatusCommand.Build(),
    PatchCommand.Build(),
    DoctorCommand.Build(),
    LoginCommand.Build(),
    LogoutCommand.Build(),
    SelfCommand.Build(),
    McpCommand.Build(),

    // Hidden: git invokes this, people don't (SPEC.md §11.2).
    CredentialHelperCommand.Build(),
};

// Recursive, so every subcommand accepts it without restating it (SPEC.md §17; see GlobalOptions
// for why a GUI caller needs this).
rootCommand.Options.Add(GlobalOptions.ProjectDirectory);
rootCommand.Options.Add(GlobalOptions.Verbose);
rootCommand.Options.Add(GlobalOptions.Trace);

var parseResult = rootCommand.Parse(args);

// Installed before anything else runs, for the whole process: the sink is ambient (see
// DiagnosticLog), so every git call, HTTP request and file operation underneath - including the
// update check started next - reports to it without any command having to pass it along
// (docs/DESIGN-v0.24-VERBOSE.md).
using var diagnostics = DiagnosticLog.Use(StderrDiagnosticSink.Create(
    DiagnosticSettings.Resolve(parseResult, Environment.GetEnvironmentVariable(DiagnosticSettings.EnvironmentVariable))));
var run = RunDiagnostics.Begin(args, parseResult);

// Started before the command and reported after it, so the check overlaps the work instead of
// following it, and the line lands after the command's own output rather than in the middle of it.
// Returns null — and prints nothing — for every invocation that must stay quiet
// (docs/DESIGN-v0.17-SELF-UPDATE.md).
var notice = UpdateNotice.Start(args);

// The default exception handler is off so nothing escapes as a bare stack trace: under --json that
// left stdout empty, which breaks the envelope contract (SPEC.md §17). UnhandledFailure reports it
// instead, as an envelope with --json and as the old trace without it, except for a project file the
// user has to fix, which gets the parser's message alone.
int exitCode;
try {
    exitCode = parseResult.Invoke(new InvocationConfiguration { EnableDefaultExceptionHandler = false });
} catch (Exception ex) {
    exitCode = UnhandledFailure.Report(parseResult, ex);
}

notice?.Report();
run.End(exitCode);

return exitCode;
