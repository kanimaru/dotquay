using System.CommandLine;
using Dotquay.Cli;
using Dotquay.Cli.Commands;

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

    // Hidden: git invokes this, people don't (SPEC.md §11.2).
    CredentialHelperCommand.Build(),
};

// Recursive, so every subcommand accepts it without restating it (SPEC.md §17; see GlobalOptions
// for why a GUI caller needs this).
rootCommand.Options.Add(GlobalOptions.ProjectDirectory);

// Started before the command and reported after it, so the check overlaps the work instead of
// following it, and the line lands after the command's own output rather than in the middle of it.
// Returns null — and prints nothing — for every invocation that must stay quiet
// (docs/DESIGN-v0.17-SELF-UPDATE.md).
var notice = UpdateNotice.Start(args);

var exitCode = rootCommand.Parse(args).Invoke();

notice?.Report();

return exitCode;
