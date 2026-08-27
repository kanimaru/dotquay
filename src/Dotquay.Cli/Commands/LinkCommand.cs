using System.CommandLine;
using Dotquay.Core.IO;
using Dotquay.Core.Json;
using Dotquay.Core.Local;
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
            Description = "The addon to link. Omit to list this machine's links.",
            Arity = ArgumentArity.ZeroOrOne,
        };

        var pathArgument = new Argument<string?>("path")
        {
            Description = "Folder to link to — the addon folder itself, whose contents should appear under addons/.",
            Arity = ArgumentArity.ZeroOrOne,
        };

        var removeOption = new Option<bool>("--remove")
        {
            Description = "Remove the link and reinstall the addon from the lockfile.",
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

        var command = new Command(CommandName, "Develop an addon in place: point it at a local folder instead of its repository.")
        {
            nameArgument, pathArgument, removeOption, noInstallOption, forceOption, jsonOption,
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

                var message = remove
                    ? links.Remove(projectDirectory, name)
                        ? $"Unlinked '{name}'."
                        : $"'{name}' wasn't linked — nothing to do."
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

    private static string LinkAndDescribe(LocalLinks links, string projectDirectory, string name, string path)
    {
        links.Set(projectDirectory, name, path);

        var ignored = EnsureGitIgnored(projectDirectory);
        var suffix = ignored
            ? $" Added {LocalLinks.FileName} to .gitignore."
            : $" {LocalLinks.FileName} is per-machine — don't commit it.";

        return $"Linked '{name}' -> {path}. Edits there are live." + suffix;
    }

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
        if (contents.Contains(LocalLinks.FileName, StringComparison.Ordinal))
        {
            return false;
        }

        var separator = contents.EndsWith('\n') ? string.Empty : Environment.NewLine;
        fileSystem.WriteAllText(
            gitignore,
            contents + separator + Environment.NewLine
            // Plain ASCII on purpose: .gitignore is read by tooling that doesn't always agree on
            // encoding, and a section-sign here renders as mojibake in a plain-ANSI viewer.
            + "# Per-machine dotquay links; holds absolute paths." + Environment.NewLine
            + LocalLinks.FileName + Environment.NewLine);

        return true;
    }

    private static int ListLinks(LocalLinks links, string projectDirectory, bool json)
    {
        var all = links.Read(projectDirectory);

        if (json)
        {
            JsonOutput.WriteOperation(CommandName, new OperationJson
            {
                Changes = all
                    .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                    .Select(kv => new ChangeJson
                    {
                        Name = kv.Key,
                        Kind = JsonMapper.ChangeKind.Addon,
                        Action = JsonMapper.ChangeAction.Unchanged,
                        To = kv.Value.Path,
                    })
                    .ToList(),
            });

            return 0;
        }

        if (all.Count == 0)
        {
            Console.WriteLine("No local links. Add one with 'dotquay link <name> <path>'.");
            return 0;
        }

        foreach (var (linkName, link) in all.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            Console.WriteLine($"{linkName}  ->  {link.Path}");
        }

        return 0;
    }
}
