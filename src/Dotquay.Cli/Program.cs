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

var exitCode = parseResult.Invoke();

notice?.Report();
run.End(exitCode);

return exitCode;
