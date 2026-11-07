using System.CommandLine;
using Dotquay.Core.Json;
using Dotquay.Core.Manifest;
using Dotquay.Core.Workflows;

namespace Dotquay.Cli.Commands;

/// <summary>
/// <c>dotquay override</c> (v0.5) — writes, clears, or lists <c>[overrides.&lt;name&gt;]</c> entries,
/// the project's unconditional escape hatch out of a dependency conflict (SPEC.md §7.1/§8). Every
/// conflict message dotquay prints already tells the user to add one; before v0.5 the only way to do
/// that was to hand-edit TOML.
/// </summary>
/// <remarks>
/// Writing an override is followed by <see cref="DependencyWorkflow.ResyncAsync"/>, not a plain
/// <c>update</c>: settling one conflict must not silently advance every other addon to its newest
/// tag. If the resync fails, the override stays written — it's the user's stated decision, and
/// silently reverting it would hide which choice they made from the error they now have to debug.
/// </remarks>
internal static class OverrideCommand {
    private const string CommandName = "override";

    public static Command Build() {
        var nameArgument = new Argument<string?>("name") {
            Description = "The addon to force. Omit to list the project's current overrides.",
            Arity = ArgumentArity.ZeroOrOne,
        };

        var versionOption = new Option<string?>("--version") { Description = "Force a version constraint, e.g. \"~1.4\"." };
        var branchOption = new Option<string?>("--branch") { Description = "Force a branch; the lockfile still pins the resolved SHA." };
        var revOption = new Option<string?>("--rev") { Description = "Force an exact commit." };
        var gitOption = new Option<string?>("--git") {
            Description = "Repository to pin to. Defaults to whatever the manifest or lockfile already "
                + "records for this name — only needed when two requirers disagree about the repo itself.",
        };
        var pathOption = new Option<string?>("--path") { Description = "Subtree to extract, if it isn't addons/<name>." };
        var installAsOption = new Option<string?>("--install-as") {
            Description = "Folder to install into under addons/, if it shouldn't be the addon's own name. "
                + "Settles a conflict where two addons want the same dependency in different folders.",
        };
        var clearOption = new Option<bool>("--clear") { Description = "Remove the override and hand the name back to normal resolution." };
        var noInstallOption = new Option<bool>("--no-install") {
            Description = "Write the manifest only. Skips the resync, so nothing is fetched or installed.",
        };
        var forceOption = new Option<bool>("--force") { Description = "Overwrite hand-modified addons during the resync (SPEC.md P0-6)." };
        var jsonOption = JsonOutput.CreateOption();

        var command = new Command(CommandName, "Force one addon to a specific version, branch, or commit — the conflict escape hatch.")
        {
            nameArgument, versionOption, branchOption, revOption, gitOption, pathOption, installAsOption,
            clearOption, noInstallOption, forceOption, jsonOption,
        };

        command.SetAction(async (parseResult, cancellationToken) => {
            var name = parseResult.GetValue(nameArgument);
            var json = parseResult.GetValue(jsonOption);
            var overrides = WorkflowFactory.CreateOverrides();
            var projectDirectory = GlobalOptions.Resolve(parseResult);

            try {
                if (name is null) {
                    return ListOverrides(overrides, projectDirectory, json);
                }

                if (parseResult.GetValue(clearOption)) {
                    return await ApplyAsync(
                        overrides.Clear(projectDirectory, name),
                        parseResult.GetValue(noInstallOption),
                        parseResult.GetValue(forceOption),
                        json,
                        projectDirectory,
                        cancellationToken);
                }

                var (kind, value) = ReadSource(parseResult, versionOption, branchOption, revOption);
                if (value is null) {
                    return JsonOutput.Fail(
                        json, CommandName, JsonErrorKind.Workflow,
                        $"'{name}': pass exactly one of --version, --branch, or --rev (or --clear to remove the override).");
                }

                var result = overrides.Set(
                    projectDirectory,
                    name,
                    kind,
                    value,
                    parseResult.GetValue(gitOption),
                    parseResult.GetValue(pathOption),
                    parseResult.GetValue(installAsOption));

                return await ApplyAsync(
                    result,
                    parseResult.GetValue(noInstallOption),
                    parseResult.GetValue(forceOption),
                    json,
                    projectDirectory,
                    cancellationToken);
            } catch (Exception ex) when (ex is not OperationCanceledException) {
                return JsonOutput.Fail(json, CommandName, ex);
            }
        });

        return command;
    }

    /// <summary>Exactly one source flag may be set; two would silently make one of them a lie about
    /// what got written.</summary>
    private static (AddonSourceKind Kind, string? Value) ReadSource(
        ParseResult parseResult, Option<string?> version, Option<string?> branch, Option<string?> rev) {
        var values = new (AddonSourceKind Kind, string? Value)[]
        {
            (AddonSourceKind.Version, parseResult.GetValue(version)),
            (AddonSourceKind.Branch, parseResult.GetValue(branch)),
            (AddonSourceKind.Rev, parseResult.GetValue(rev)),
        };

        var set = values.Where(v => !string.IsNullOrWhiteSpace(v.Value)).ToList();
        return set.Count == 1 ? set[0] : (AddonSourceKind.Version, null);
    }

    private static async Task<int> ApplyAsync(
        OverrideResult result,
        bool noInstall,
        bool force,
        bool json,
        string projectDirectory,
        CancellationToken cancellationToken) {
        var messages = new List<string> { Describe(result) };
        var changes = new List<ChangeJson>
        {
            new()
            {
                Name = result.Name,
                Kind = JsonMapper.ChangeKind.Addon,
                Action = result.Change switch
                {
                    OverrideChange.Added => JsonMapper.ChangeAction.Added,
                    OverrideChange.Replaced => JsonMapper.ChangeAction.Updated,
                    OverrideChange.Removed => JsonMapper.ChangeAction.Removed,
                    _ => JsonMapper.ChangeAction.Unchanged,
                },
                From = null,
                To = result.Spec is null ? null : Core.Resolve.AddonRequirement.ConstraintText(result.Spec),
            },
        };

        // Nothing was written, so there is nothing for a resync to apply — running one anyway would
        // fetch and reinstall for no reason, which on a large project is a noticeable pause in the
        // editor for a click that changed nothing.
        var shouldResync = !noInstall && result.Change != OverrideChange.Unchanged;

        if (shouldResync) {
            var update = await WorkflowFactory.Create().ResyncAsync(projectDirectory, force, cancellationToken);
            var applied = JsonMapper.ToJson(update);
            changes.AddRange(applied.Changes.Where(c => c.Action != JsonMapper.ChangeAction.Unchanged));

            foreach (var change in changes.Skip(1)) {
                messages.Add($"{change.Name}: {change.From ?? "(new)"} -> {change.To}");
            }

            messages.AddRange(update.SourceBuildNotes);
        }

        if (json) {
            JsonOutput.WriteOperation(CommandName, new OperationJson { Changes = changes, Messages = messages });
        } else {
            foreach (var message in messages) {
                ConsoleOutput.WriteLine(ConsoleColor.Green, message);
            }
        }

        return 0;
    }

    private static string Describe(OverrideResult result) => result.Change switch {
        OverrideChange.Added => $"Added [overrides.{result.Name}] -> {Core.Resolve.AddonRequirement.ConstraintText(result.Spec!)}",
        OverrideChange.Replaced => $"Changed [overrides.{result.Name}] -> {Core.Resolve.AddonRequirement.ConstraintText(result.Spec!)}",
        OverrideChange.Removed => $"Removed [overrides.{result.Name}]",
        _ => $"[overrides.{result.Name}] already says that — nothing to do.",
    };

    private static int ListOverrides(OverrideWorkflow overrides, string projectDirectory, bool json) {
        var entries = overrides.List(projectDirectory);

        if (json) {
            JsonOutput.WriteOperation(CommandName, new OperationJson {
                Changes = entries
                    .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                    .Select(kv => new ChangeJson {
                        Name = kv.Key,
                        Kind = JsonMapper.ChangeKind.Addon,
                        Action = JsonMapper.ChangeAction.Unchanged,
                        From = JsonMapper.ToWire(kv.Value.SourceKind),
                        To = Core.Resolve.AddonRequirement.ConstraintText(kv.Value),
                    })
                    .ToList(),
            });

            return 0;
        }

        if (entries.Count == 0) {
            Console.WriteLine("No overrides. Add one with 'dotquay override <name> --version <tag>'.");
            return 0;
        }

        foreach (var (name, spec) in entries.OrderBy(kv => kv.Key, StringComparer.Ordinal)) {
            Console.WriteLine($"{name}  {JsonMapper.ToWire(spec.SourceKind)} {Core.Resolve.AddonRequirement.ConstraintText(spec)}  {spec.Git}");
        }

        return 0;
    }
}
