using System.CommandLine;
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
    OutdatedCommand.Build(),
    StatusCommand.Build(),
    PatchCommand.Build(),
    DoctorCommand.Build(),
};

// Recursive, so every subcommand accepts it without restating it (SPEC.md §17; see GlobalOptions
// for why a GUI caller needs this).
rootCommand.Options.Add(GlobalOptions.ProjectDirectory);

return rootCommand.Parse(args).Invoke();
