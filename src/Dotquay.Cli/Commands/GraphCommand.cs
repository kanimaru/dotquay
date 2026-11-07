using System.CommandLine;
using Dotquay.Core.Graph;
using Dotquay.Core.Json;
using Dotquay.Core.Resolve;

namespace Dotquay.Cli.Commands;

/// <summary>
/// <c>dotquay graph</c> (v0.5) — prints the project's dependency graph (SPEC.md §8). Read-only: it
/// never writes the manifest, the lockfile, or <c>addons/</c>, so it is always safe to run, including
/// on a project mid-conflict.
/// </summary>
/// <remarks>
/// Two modes, because they answer different questions. The default reads <c>dotquay.lock</c>'s
/// recorded graph edges — instant, offline, and true to what is actually installed. <c>--resolve</c>
/// re-walks live git refs, which is the only way to discover that the manifest currently has no
/// solution; the editor plugin opens with the fast mode and escalates to <c>--resolve</c> on demand
/// (SPEC.md §18).
/// </remarks>
internal static class GraphCommand {
    private const string CommandName = "graph";

    public static Command Build() {
        var resolveOption = new Option<bool>("--resolve") {
            Description = "Re-walk the live dependency graph against real git refs instead of reading "
                + "the lockfile. Slower and needs network, but the only mode that reports unresolved "
                + "conflicts and the candidate versions that would settle them.",
        };

        var jsonOption = JsonOutput.CreateOption();

        var command = new Command(CommandName, "Show the dependency graph: who requires what, and where it conflicts.")
        {
            resolveOption,
            jsonOption,
        };

        command.SetAction(async (parseResult, cancellationToken) => {
            var resolve = parseResult.GetValue(resolveOption);
            var json = parseResult.GetValue(jsonOption);

            try {
                var inspector = InspectorFactory.Create();
                var projectDirectory = GlobalOptions.Resolve(parseResult);

                var report = resolve
                    ? await inspector.InspectResolvedAsync(projectDirectory, cancellationToken)
                    : inspector.Inspect(projectDirectory);

                // Both output modes render the same payload, so the tree a human reads and the JSON a
                // plugin parses can never describe different graphs.
                var payload = JsonMapper.ToJson(report);

                // Warnings ride the envelope's existing `warnings` array rather than the payload:
                // they're the same class of thing `add` reports for an untagged repo, and a consumer
                // that already shows warnings gets these for free.
                var warnings = report.Warnings.Select(w => w.Message).ToList();

                if (json) {
                    Console.WriteLine(DotquayJson.Write(JsonEnvelope<GraphJson>.Success(CommandName, payload, warnings)));
                } else {
                    PrintTree(payload);

                    foreach (var warning in warnings) {
                        Console.WriteLine();
                        ConsoleOutput.WriteRedacted(ConsoleColor.Yellow, warning);
                    }
                }

                // A conflict is a real failure of the manifest, so the exit code says so — but the
                // payload is still emitted first, because describing the conflict *is* this command's
                // job. A caller that only checks the exit code and a caller that parses the JSON both
                // get the right answer.
                return report.Conflicts.Count > 0 ? 1 : 0;
            } catch (Exception ex) when (ex is not OperationCanceledException) {
                return JsonOutput.Fail(json, CommandName, ex);
            }
        });

        return command;
    }

    /// <summary>Why a NuGet package is in the project, in the same words the tree uses for addons.
    /// Names the addons that declared it rather than saying "an addon", since which one is the
    /// question someone reading this is actually asking.</summary>
    internal static string DescribeDotnetOrigin(DotnetJson dependency) {
        var addons = dependency.RequiredBy.Where(r => r != GraphConflict.ProjectRequirerName).ToList();

        return dependency.Origin switch {
            "orphan" => "  (locked but nothing declares it — see 'dotquay doctor')",
            _ when addons.Count == 0 => dependency.DeclaredByProject ? "" : "  (required by an addon)",
            "root" => $"  (also required by {string.Join(", ", addons)})",
            _ => $"  (required by {string.Join(", ", addons)})",
        };
    }

    /// <summary>An indented tree for humans. Each addon is printed once at its shallowest depth, with
    /// deeper repeats marked rather than re-expanded — a diamond (SPEC.md §8) is one installed copy,
    /// and printing it twice would suggest otherwise.</summary>
    private static void PrintTree(GraphJson report) {
        if (report.Addons.Count == 0 && report.Dotnet.Count == 0) {
            Console.WriteLine("Nothing declared yet. Run 'dotquay add <git-url>' or 'dotquay add <nuget-id>' to add one.");
            return;
        }

        var byName = report.Addons.ToDictionary(a => a.Name, StringComparer.Ordinal);
        var conflictedNames = report.Conflicts.Select(c => c.Name).ToHashSet(StringComparer.Ordinal);
        var printed = new HashSet<string>(StringComparer.Ordinal);

        foreach (var root in report.Roots) {
            PrintNode(root, byName, conflictedNames, printed, depth: 0);
        }

        foreach (var orphan in report.Addons.Where(a => a.Origin == "orphan" && printed.Add(a.Name))) {
            ConsoleOutput.WriteLine(ConsoleColor.Yellow, $"{orphan.Name}  (locked but no longer reachable — see 'dotquay doctor')");
        }

        foreach (var dependency in report.Dotnet) {
            Console.WriteLine($"{dependency.Name}  {dependency.Resolved ?? dependency.Constraint ?? "?"}  (NuGet){DescribeDotnetOrigin(dependency)}");
        }

        foreach (var conflict in report.Conflicts) {
            Console.WriteLine();
            ConsoleOutput.WriteRedacted(ConsoleColor.Red, conflict.Message);

            foreach (var candidate in conflict.Candidates.Take(5)) {
                var satisfies = candidate.Satisfies.Count == 0 ? "nothing" : string.Join(", ", candidate.Satisfies);
                Console.WriteLine($"    candidate: {candidate.Kind} {candidate.Value} — satisfies {satisfies}");
            }

            Console.WriteLine($"    fix: dotquay override {conflict.Name} --version <tag>");
        }
    }

    private static void PrintNode(
        string name,
        IReadOnlyDictionary<string, GraphNodeJson> byName,
        IReadOnlySet<string> conflictedNames,
        HashSet<string> printed,
        int depth) {
        var indent = new string(' ', depth * 2);

        if (!byName.TryGetValue(name, out var node)) {
            ConsoleOutput.WriteLine(ConsoleColor.Yellow, $"{indent}{name}  (not locked)");
            return;
        }

        if (!printed.Add(name)) {
            Console.WriteLine($"{indent}{name}  (already shown)");
            return;
        }

        var version = node.Version ?? node.ShortSha ?? "unresolved";
        var flags = string.Concat(
            node.IsOverridden ? " [override]" : "",
            node.IsPatched ? " [patched]" : "",
            node.Status is "modified" or "missing" ? $" [{node.Status}]" : "");

        if (conflictedNames.Contains(name)) {
            ConsoleOutput.WriteLine(ConsoleColor.Red, $"{indent}{name}  CONFLICT{flags}");
        } else {
            Console.WriteLine($"{indent}{name}  {version}{flags}");
        }

        foreach (var child in node.Dependencies) {
            PrintNode(child, byName, conflictedNames, printed, depth + 1);
        }
    }
}
