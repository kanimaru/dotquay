using System.CommandLine;
using Dotquay.Core.Init;
using Dotquay.Core.IO;
using Dotquay.Core.Json;

namespace Dotquay.Cli.Commands;

internal static class InitCommand
{
    private const string CommandName = "init";

    public static Command Build()
    {
        var directoryArgument = new Argument<string?>("directory")
        {
            Description = "Directory to initialize. Defaults to the current directory.",
            Arity = ArgumentArity.ZeroOrOne,
        };

        var packageOption = new Option<bool>("--package")
        {
            Description = "Scaffold a package manifest (with a [package] header) for an addon, instead of a project manifest.",
        };

        var nuGetSourceOption = new Option<string?>("--nuget-source")
        {
            Description = "V3 service-index URL of a private NuGet feed (e.g. a GitLab/Azure Artifacts "
                + "package registry). Only used without --package. Omit to use the public nuget.org. "
                + "Credentials come from DOTQUAY_NUGET_USERNAME/DOTQUAY_NUGET_TOKEN, never this file.",
        };

        var nameOption = new Option<string?>("--name")
        {
            Description = "Package name. Only used with --package. Defaults to the directory name.",
        };

        var jsonOption = JsonOutput.CreateOption();

        var command = new Command(CommandName, "Scaffold a dotquay.toml in the project root (or, with --package, inside an addon folder).")
        {
            directoryArgument,
            packageOption,
            nuGetSourceOption,
            nameOption,
            jsonOption,
        };

        command.SetAction(parseResult =>
        {
            // The positional argument wins over --project: `init <dir>` predates the global option
            // and reads more naturally for the one command whose whole job is to create a directory's
            // manifest.
            var directory = Path.GetFullPath(parseResult.GetValue(directoryArgument) ?? GlobalOptions.Resolve(parseResult));
            var isPackage = parseResult.GetValue(packageOption);
            var json = parseResult.GetValue(jsonOption);
            var initService = new InitService(new SystemFileSystem());

            var result = isPackage
                ? initService.InitPackage(directory, parseResult.GetValue(nameOption) ?? new DirectoryInfo(directory).Name)
                : initService.InitProject(directory, parseResult.GetValue(nuGetSourceOption));

            var kind = isPackage ? "package" : "project";

            if (!result.WasCreated)
            {
                // Not an error envelope: the manifest the caller asked for exists, which is the state
                // it wanted. Refusing to overwrite is the safety behaviour, not a failure — a GUI
                // that treated this as an error would show a red banner on a project that is fine.
                var message = $"{result.ManifestPath} already exists — not overwriting.";

                if (json)
                {
                    JsonOutput.WriteOperation(CommandName, new OperationJson
                    {
                        Changes =
                        [
                            new ChangeJson
                            {
                                Name = kind,
                                Kind = JsonMapper.ChangeKind.Addon,
                                Action = JsonMapper.ChangeAction.Unchanged,
                                To = result.ManifestPath,
                            },
                        ],
                        Messages = [message],
                    });
                    return 0;
                }

                ConsoleOutput.WriteLine(ConsoleColor.Yellow, message);
                return 1;
            }

            var created = $"Wrote {kind} manifest to {result.ManifestPath}";

            if (json)
            {
                JsonOutput.WriteOperation(CommandName, new OperationJson
                {
                    Changes =
                    [
                        new ChangeJson
                        {
                            Name = kind,
                            Kind = JsonMapper.ChangeKind.Addon,
                            Action = JsonMapper.ChangeAction.Added,
                            To = result.ManifestPath,
                        },
                    ],
                    Messages = [created],
                });
                return 0;
            }

            ConsoleOutput.WriteLine(ConsoleColor.Green, created);
            return 0;
        });

        return command;
    }
}
