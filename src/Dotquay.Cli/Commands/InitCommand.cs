using System.CommandLine;
using Dotquay.Core.Init;
using Dotquay.Core.IO;

namespace Dotquay.Cli.Commands;

internal static class InitCommand
{
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

        var godotOption = new Option<string?>("--godot")
        {
            Description = "Godot version to record in [project]. Only used without --package. Defaults to \"4.3\".",
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

        var packageVersionOption = new Option<string?>("--package-version")
        {
            Description = "Package version. Only used with --package. Defaults to \"0.1.0\".",
        };

        var command = new Command("init", "Scaffold a dotquay.toml in the project root (or, with --package, inside an addon folder).")
        {
            directoryArgument,
            packageOption,
            godotOption,
            nuGetSourceOption,
            nameOption,
            packageVersionOption,
        };

        command.SetAction(parseResult =>
        {
            var directory = Path.GetFullPath(parseResult.GetValue(directoryArgument) ?? Directory.GetCurrentDirectory());
            var isPackage = parseResult.GetValue(packageOption);
            var initService = new InitService(new SystemFileSystem());

            var result = isPackage
                ? initService.InitPackage(
                    directory,
                    parseResult.GetValue(nameOption) ?? new DirectoryInfo(directory).Name,
                    parseResult.GetValue(packageVersionOption) ?? "0.1.0")
                : initService.InitProject(directory, parseResult.GetValue(godotOption), parseResult.GetValue(nuGetSourceOption));

            if (!result.WasCreated)
            {
                ConsoleOutput.WriteLine(ConsoleColor.Yellow, $"{result.ManifestPath} already exists — not overwriting.");
                return 1;
            }

            var kind = isPackage ? "package" : "project";
            ConsoleOutput.WriteLine(ConsoleColor.Green, $"Wrote {kind} manifest to {result.ManifestPath}");
            return 0;
        });

        return command;
    }
}
