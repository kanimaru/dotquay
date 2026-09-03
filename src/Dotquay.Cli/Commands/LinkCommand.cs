using System.CommandLine;
using Dotquay.Core.IO;
using Dotquay.Core.Json;
using Dotquay.Core.Local;
using Dotquay.Core.Manifest;
using Dotquay.Core.Workflows;

namespace Dotquay.Cli.Commands;

/// <summary>
/// <c>dotquay link</c> — points an addon at a folder on this machine instead of its upstream
/// repository (SPEC.md §7.4), so an addon can be developed in place while a project consumes it.
/// </summary>
/// <remarks>
/// Writes <c>dotquay.local.toml</c>, which is per-machine and must not be committed: the value is an
/// absolute path on one person's disk. The lockfile is untouched and keeps naming the upstream commit,
/// so the project stays reproducible for everyone who doesn't have the link — unlinking simply puts
/// this machine back on what the lockfile always said.
/// </remarks>
internal static class LinkCommand
{
    private const string CommandName = "link";

    public static Command Build()
    {
        var nameArgument = new Argument<string?>("name")
        {
            Description = "The addon or [dotnet] package to link. Omit to list this machine's links.",
            Arity = ArgumentArity.ZeroOrOne,
        };

        var pathArgument = new Argument<string?>("path")
        {
            Description =
                "What to link to: the addon folder itself, whose contents should appear under addons/ — "
                + "or, for a [dotnet] package, a .csproj or the folder holding one.",
            Arity = ArgumentArity.ZeroOrOne,
        };

        var removeOption = new Option<bool>("--remove")
        {
            Description = "Remove the link and go back to what the lockfile says.",
        };

        var analyzerOption = new Option<bool>("--analyzer")
        {
            Description =
                "The linked project is a Roslyn analyzer or source generator. Adds OutputItemType=\"Analyzer\" "
                + "and ReferenceOutputAssembly=\"false\" — without them the generator is referenced as an "
                + "ordinary library and never runs.",
        };

        var noInstallOption = new Option<bool>("--no-install")
        {
            Description = "Write dotquay.local.toml only. Skips the install, so addons/ isn't touched yet.",
        };

        var forceOption = new Option<bool>("--force")
        {
            Description = "Replace a hand-modified addon folder with the link instead of refusing (SPEC.md P0-6).",
        };

        var jsonOption = JsonOutput.CreateOption();

        var command = new Command(
            CommandName,
            "Develop a dependency in place: point an addon at a local folder, or a [dotnet] package at a local .csproj.")
        {
            nameArgument, pathArgument, removeOption, analyzerOption, noInstallOption, forceOption, jsonOption,
        };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var name = parseResult.GetValue(nameArgument);
            var json = parseResult.GetValue(jsonOption);
            var projectDirectory = GlobalOptions.Resolve(parseResult);
            var links = new LocalLinks(new SystemFileSystem());

            try
            {
                if (name is null)
                {
                    return ListLinks(links, projectDirectory, json);
                }

                var remove = parseResult.GetValue(removeOption);
                var path = parseResult.GetValue(pathArgument);

                if (!remove && string.IsNullOrWhiteSpace(path))
                {
                    return JsonOutput.Fail(
                        json, CommandName, JsonErrorKind.Workflow,
                        $"'dotquay link {name} <path>' needs a folder to link to, or --remove to unlink it.");
                }

                // Which kind of link this is comes from the manifest, not from a second verb: the
                // project already says whether a name is an addon or a NuGet package, and asking the
                // user to repeat it is two things to remember for one concept.
                var packageId = ResolveDotnetPackageId(projectDirectory, name);

                var message = remove
                    ? Unlink(links, projectDirectory, name, packageId)
                    : packageId is not null
                        ? LinkDotnetAndDescribe(
                            links, projectDirectory, packageId, path!, parseResult.GetValue(analyzerOption))
                        : LinkAndDescribe(links, projectDirectory, name, path!);

                // Reinstalling is what actually swaps the folder for a link (or the link back for the
                // real addon). Skipped when nothing changed, so an idempotent re-run is free.
                var installed = !parseResult.GetValue(noInstallOption) && !message.Contains("nothing to do");
                if (installed)
                {
                    await WorkflowFactory.Create().InstallAsync(
                        projectDirectory, parseResult.GetValue(forceOption), cancellationToken);
                }

                if (json)
                {
                    JsonOutput.WriteOperation(CommandName, new OperationJson
                    {
                        Changes =
                        [
                            new ChangeJson
                            {
                                Name = name,
                                Kind = JsonMapper.ChangeKind.Addon,
                                Action = remove ? JsonMapper.ChangeAction.Removed : JsonMapper.ChangeAction.Updated,
                                To = remove ? null : path,
                            },
                        ],
                        Messages = [message],
                    });
                    return 0;
                }

                ConsoleOutput.WriteLine(ConsoleColor.Green, message);

                if (!installed)
                {
                    Console.WriteLine("Run 'dotquay install' to apply it.");
                }

                return 0;
            }
            catch (Exception ex) when (ex is LocalLinkException or WorkflowException)
            {
                return JsonOutput.Fail(json, CommandName, ex);
            }
        });

        return command;
    }

    /// <summary>
    /// The manifest's own spelling of <paramref name="name"/> if it names a <c>[dotnet.*]</c> package,
    /// null if it doesn't (or there is no readable manifest, in which case an addon link is the right
    /// default — it is what every link was before v0.11).
    /// </summary>
    /// <remarks>
    /// Matched case-insensitively because NuGet package IDs are, but the value returned is the
    /// manifest's spelling: everything downstream keys on that, so accepting a differently-cased name
    /// must not create a second entry under it.
    /// </remarks>
    private static string? ResolveDotnetPackageId(string projectDirectory, string name)
    {
        var fileSystem = new SystemFileSystem();
        var manifestPath = fileSystem.CombinePath(projectDirectory, ProjectFileStore.ManifestFileName);

        if (!fileSystem.FileExists(manifestPath))
        {
            return null;
        }

        try
        {
            return ManifestReader.ReadProject(fileSystem.ReadAllText(manifestPath), manifestPath)
                .Dotnet.Keys
                .FirstOrDefault(id => string.Equals(id, name, StringComparison.OrdinalIgnoreCase));
        }
        catch (ManifestParseException)
        {
            // A broken manifest is a problem for the commands that need it. Refusing to link over it
            // would be this command inventing a prerequisite it doesn't actually have.
            return null;
        }
    }

    private static string Unlink(LocalLinks links, string projectDirectory, string name, string? packageId)
    {
        var removed = packageId is not null
            ? links.RemoveDotnet(projectDirectory, packageId)
            : links.Remove(projectDirectory, name);

        return removed ? $"Unlinked '{name}'." : $"'{name}' wasn't linked — nothing to do.";
    }

    private static string LinkAndDescribe(LocalLinks links, string projectDirectory, string name, string path)
    {
        links.Set(projectDirectory, name, path);

        return $"Linked '{name}' -> {path}. Edits there are live." + GitIgnoreSuffix(projectDirectory);
    }

    private static string LinkDotnetAndDescribe(
        LocalLinks links, string projectDirectory, string packageId, string path, bool analyzer)
    {
        links.SetDotnet(projectDirectory, packageId, path, analyzer);

        var kind = analyzer ? " as an analyzer" : string.Empty;

        return $"Linked '{packageId}'{kind} -> {path}. The .csproj keeps its PackageReference; "
            + $"{LocalTargetsWriter.FileName} swaps it for a ProjectReference on this machine."
            + GitIgnoreSuffix(projectDirectory);
    }

    private static string GitIgnoreSuffix(string projectDirectory) =>
        EnsureGitIgnored(projectDirectory)
            ? $" Added {LocalLinks.FileName} and {LocalTargetsWriter.FileName} to .gitignore."
            : $" {LocalLinks.FileName} and {LocalTargetsWriter.FileName} are per-machine — don't commit them.";

    /// <summary>
    /// Appends <c>dotquay.local.toml</c> to the project's <c>.gitignore</c> if one exists and doesn't
    /// already mention it. Returns whether a line was added.
    /// </summary>
    /// <remarks>
    /// Editing someone's <c>.gitignore</c> is intrusive, and doing it anyway is the lesser evil: the
    /// file this protects holds an absolute path on one machine, and committing it breaks every
    /// teammate and every CI run with a path that doesn't exist for them. A one-line append to a file
    /// that already exists is easy to see in a diff and easy to undo. No <c>.gitignore</c> is created
    /// where there wasn't one — that would be dotquay deciding how a repository is organised.
    /// </remarks>
    private static bool EnsureGitIgnored(string projectDirectory)
    {
        var fileSystem = new SystemFileSystem();
        var gitignore = fileSystem.CombinePath(projectDirectory, ".gitignore");

        if (!fileSystem.FileExists(gitignore))
        {
            return false;
        }

        var contents = fileSystem.ReadAllText(gitignore);
        if (contents.Contains(LocalLinks.FileName, StringComparison.Ordinal)
            && contents.Contains(LocalTargetsWriter.FileName, StringComparison.Ordinal))
        {
            return false;
        }

        var lines = new List<string>();
        if (!contents.Contains(LocalLinks.FileName, StringComparison.Ordinal))
        {
            lines.Add(LocalLinks.FileName);
        }

        // The generated overlay holds the same absolute paths as the link file and is regenerated on
        // demand, so it is ignored on exactly the same terms. A project that upgraded from v0.6 has
        // the first line already and needs only this one.
        if (!contents.Contains(LocalTargetsWriter.FileName, StringComparison.Ordinal))
        {
            lines.Add(LocalTargetsWriter.FileName);
        }

        var separator = contents.EndsWith('\n') ? string.Empty : Environment.NewLine;
        fileSystem.WriteAllText(
            gitignore,
            contents + separator + Environment.NewLine
            // Plain ASCII on purpose: .gitignore is read by tooling that doesn't always agree on
            // encoding, and a section-sign here renders as mojibake in a plain-ANSI viewer.
            + "# Per-machine dotquay links; hold absolute paths." + Environment.NewLine
            + string.Join(Environment.NewLine, lines) + Environment.NewLine);

        return true;
    }

    private static int ListLinks(LocalLinks links, string projectDirectory, bool json)
    {
        var all = links.ReadAll(projectDirectory);

        if (json)
        {
            JsonOutput.WriteOperation(CommandName, new OperationJson
            {
                Changes =
                [
                    .. all.Addons
                        .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                        .Select(kv => new ChangeJson
                        {
                            Name = kv.Key,
                            Kind = JsonMapper.ChangeKind.Addon,
                            Action = JsonMapper.ChangeAction.Unchanged,
                            To = kv.Value.Path,
                        }),
                    .. all.Dotnet
                        .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                        .Select(kv => new ChangeJson
                        {
                            Name = kv.Key,
                            Kind = JsonMapper.ChangeKind.Dotnet,
                            Action = JsonMapper.ChangeAction.Unchanged,
                            To = kv.Value.ProjectPath,
                        }),
                ],
            });

            return 0;
        }

        if (all.IsEmpty)
        {
            Console.WriteLine("No local links. Add one with 'dotquay link <name> <path>'.");
            return 0;
        }

        foreach (var (linkName, link) in all.Addons.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            Console.WriteLine($"{linkName}  ->  {link.Path}");
        }

        foreach (var (packageId, link) in all.Dotnet.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var kind = link.Analyzer ? "  (analyzer)" : string.Empty;
            Console.WriteLine($"{packageId}  ->  {link.ProjectPath}{kind}");
        }

        return 0;
    }
}
