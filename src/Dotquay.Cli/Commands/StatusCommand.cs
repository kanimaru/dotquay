using System.CommandLine;
using Dotquay.Core.Json;
using Dotquay.Core.Sync;
using Dotquay.Core.Workflows;

namespace Dotquay.Cli.Commands;

internal static class StatusCommand
{
    private const string CommandName = "status";

    public static Command Build()
    {
        var explainOption = new Option<bool>("--explain")
        {
            Description =
                "For each modified addon, compare it against its locked commit and report what differs "
                + "and whether --force would discard anything. May fetch.",
        };

        var jsonOption = JsonOutput.CreateOption();
        var command = new Command(CommandName, "Diff disk against the lockfile: clean / patched as expected / modified.")
        {
            explainOption,
            jsonOption,
        };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var explain = parseResult.GetValue(explainOption);
            var json = parseResult.GetValue(jsonOption);

            // Progress only matters for --explain, which can fetch; the plain path is pure local I/O
            // and finishes before a spinner would draw its first frame.
            var (workflow, progress) = explain
                ? WorkflowFactory.CreateWithProgress(json)
                : WorkflowFactory.CreateQuiet();

            try
            {
                var result = await workflow.StatusAsync(GlobalOptions.Resolve(parseResult), explain, cancellationToken);

                progress.Dispose();

                if (json)
                {
                    Console.WriteLine(DotquayJson.Write(JsonEnvelope<StatusJson>.Success(CommandName, JsonMapper.ToJson(result))));
                    return 0;
                }

                if (result.Addons.Count == 0)
                {
                    Console.WriteLine("Nothing locked yet. Run 'dotquay add <git-url>' to add one.");
                    return 0;
                }

                var ordered = result.Addons.OrderBy(a => a.Name, StringComparer.Ordinal).ToList();

                foreach (var addon in ordered)
                {
                    Print(addon);
                }

                // Offered only when there is something to explain, and only when the user isn't
                // already looking at the explanation.
                if (!explain && ordered.Any(a => a.Kind == AddonStatusKind.Modified))
                {
                    Console.WriteLine();
                    Console.WriteLine("Run 'dotquay status --explain' to see what changed and whether --force is safe.");
                }

                return 0;
            }
            catch (Exception ex) when (JsonOutput.IsExpectedFailure(ex))
            {
                progress.Dispose();
                return JsonOutput.Fail(json, CommandName, ex);
            }
        });

        return command;
    }

    private static void Print(AddonStatusInfo addon)
    {
        switch (addon.Kind)
        {
            case AddonStatusKind.Clean:
                Console.WriteLine($"{addon.Name}  clean");
                break;
            case AddonStatusKind.PatchedAsExpected:
                ConsoleOutput.WriteLine(ConsoleColor.Cyan, $"{addon.Name}  patched as expected");
                break;
            case AddonStatusKind.Modified:
                ConsoleOutput.WriteLine(ConsoleColor.Red, $"{addon.Name}  modified (hand-edited, not captured as a patch)");
                PrintExplanation(addon.Explanation);
                break;
            case AddonStatusKind.Missing:
                ConsoleOutput.WriteLine(ConsoleColor.Yellow, $"{addon.Name}  missing (run 'dotquay install')");
                break;
            case AddonStatusKind.Linked:
                ConsoleOutput.WriteLine(ConsoleColor.Cyan, $"{addon.Name}  linked to a local folder (dotquay link --remove to undo)");
                break;
        }
    }

    private static void PrintExplanation(AddonExplanation? explanation)
    {
        if (explanation is null)
        {
            return;
        }

        if (explanation.Verdict == ExplainVerdict.Unavailable)
        {
            ConsoleOutput.WriteLine(ConsoleColor.Yellow, "    could not compare against the locked commit:");

            // Indented line by line rather than printed raw: the reason is usually git's own multi-line
            // failure, and the detail is worth keeping — it says whether this is "you're offline" or
            // "that commit is gone". Unindented it breaks out of the addon's block and reads like the
            // command itself crashed.
            foreach (var line in (explanation.UnavailableReason ?? "no reason given").ReplaceLineEndings("\n").Split('\n'))
            {
                ConsoleOutput.WriteLine(ConsoleColor.DarkGray, $"      {line}");
            }

            return;
        }

        if (explanation.Counts.Total == 0)
        {
            // Byte-identical to its own locked commit, yet the recorded hash disagrees — the case
            // SyncEngine.SwapAndHash also handles by comparing trees rather than numbers. Saying so
            // is the whole answer here; "0 files differ" alone would read as a bug.
            Console.WriteLine("    nothing differs from the locked commit — the recorded hash predates the current");
            Console.WriteLine("    hashing rules. 'dotquay update --force' rerecords it and discards nothing.");
            return;
        }

        foreach (var change in explanation.Changes)
        {
            var color = change.Class == ChangeClass.Content ? ConsoleColor.Red : ConsoleColor.DarkGray;
            ConsoleOutput.WriteLine(color, $"    {Describe(change.Kind)} {change.Path}  [{Describe(change.Class)}]");
        }

        if (explanation.IsTruncated)
        {
            ConsoleOutput.WriteLine(
                ConsoleColor.DarkGray,
                $"    ... and {explanation.Counts.Total - explanation.Changes.Count} more");
        }

        if (explanation.PatchCouldNotBeApplied)
        {
            // Said only when a patch was declared and did not make it into the baseline — never for
            // an ordinary unpatched addon, which is most of them.
            ConsoleOutput.WriteLine(
                ConsoleColor.Yellow,
                "    this addon's patch could not be reconstructed, so its own edits are listed above too");
        }

        Console.WriteLine();

        if (explanation.Verdict == ExplainVerdict.SafeToForce)
        {
            ConsoleOutput.WriteLine(
                ConsoleColor.Green,
                $"    {Summarize(explanation.Counts)} — nothing here survives a reinstall anyway.");
            Console.WriteLine("    'dotquay update --force' is safe.");
            return;
        }

        var real = explanation.Counts.Content;
        var one = real == 1;

        ConsoleOutput.WriteLine(
            ConsoleColor.Red, $"    {real} real content change{(one ? "" : "s")}. --force would discard {(one ? "it" : "them")}.");
        Console.WriteLine(
            $"    Capture {(one ? "it" : "them")} first with 'dotquay patch <name>', or inspect the file{(one ? "" : "s")} above.");
    }

    /// <summary>Names what the benign changes actually were, so "safe" is a conclusion the reader can
    /// check rather than one they have to take on trust.</summary>
    private static string Summarize(ChangeCounts counts)
    {
        var parts = new List<string>(4);

        if (counts.UidOnly > 0)
        {
            parts.Add($"{counts.UidOnly} uid-only rewrite{(counts.UidOnly == 1 ? "" : "s")}");
        }

        if (counts.WhitespaceOnly > 0)
        {
            parts.Add($"{counts.WhitespaceOnly} whitespace-only change{(counts.WhitespaceOnly == 1 ? "" : "s")}");
        }

        if (counts.LineEndingsOnly > 0)
        {
            parts.Add($"{counts.LineEndingsOnly} line-ending change{(counts.LineEndingsOnly == 1 ? "" : "s")}");
        }

        if (counts.EditorArtifact > 0)
        {
            parts.Add($"{counts.EditorArtifact} editor artifact{(counts.EditorArtifact == 1 ? "" : "s")}");
        }

        return parts.Count == 0 ? "No real content changes" : string.Join(", ", parts);
    }

    private static string Describe(ChangeKind kind) => kind switch
    {
        ChangeKind.Added => "added  ",
        ChangeKind.Removed => "removed",
        _ => "changed",
    };

    private static string Describe(ChangeClass @class) => @class switch
    {
        ChangeClass.EditorArtifact => "editor artifact",
        ChangeClass.LineEndingsOnly => "line endings only",
        ChangeClass.UidOnly => "uid only",
        ChangeClass.WhitespaceOnly => "whitespace only",
        _ => "real content",
    };
}
