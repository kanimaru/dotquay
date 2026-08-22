using System.CommandLine;
using Dependi.Cli.Commands;

var rootCommand = new RootCommand("dependi - a package manager for Godot projects")
{
    InitCommand.Build(),
    AddCommand.Build(),
    RemoveCommand.Build(),
    InstallCommand.Build(),
    UpdateCommand.Build(),
    ListCommand.Build(),
    OutdatedCommand.Build(),
    StatusCommand.Build(),
    PatchCommand.Build(),
    DoctorCommand.Build(),
};

return rootCommand.Parse(args).Invoke();
