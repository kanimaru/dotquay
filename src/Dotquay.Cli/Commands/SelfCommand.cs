using System.CommandLine;
using Dotquay.Core.Json;
using Dotquay.Core.SelfUpdate;
using Dotquay.Core.Workflows;

namespace Dotquay.Cli.Commands;

/// <summary>
/// <c>dotquay self check|update|rollback|notify</c> (v0.17) — dotquay maintaining its own
/// installation (docs/DESIGN-v0.17-SELF-UPDATE.md).
/// </summary>
/// <remarks>
/// <para>A command group under <c>self</c> rather than a top-level <c>update</c> flag, because
/// <c>dotquay update</c> already means something and means it about the project's dependencies.
/// Every word in this group is about the executable, and the grouping is what keeps that
/// unambiguous.</para>
/// <para>Nothing here is interactive. The editor plugin drives dotquay through captured stdout and
/// cannot answer a prompt (SPEC.md §18), and "are you sure?" before replacing a binary that is
/// verified, checksummed, and kept for rollback would be ceremony rather than safety.</para>
/// </remarks>
internal static class SelfCommand
{
    private const string CheckCommandName = "self-check";
    private const string UpdateCommandName = "self-update";
    private const string RollbackCommandName = "self-rollback";

    /// <summary>Exit code of <c>self check</c> when a newer release exists. Deliberately not 1: a
    /// newer dotquay is not a failure, and a pipeline that checks should not go red for it.</summary>
    private const int UpdateAvailableExitCode = 3;

    public static Command Build()
    {
        var command = new Command("self", "Check for, install, or roll back a new version of dotquay itself.")
        {
            BuildCheck(),
            BuildUpdate(),
            BuildRollback(),
            BuildNotify(),
        };

        var jsonOption = JsonOutput.CreateOption();
        command.Options.Add(jsonOption);

        // A bare `dotquay self` checks, matching `dotquay source` listing: the most common thing
        // anyone wants here is the answer to "am I behind", and printing help instead is a wasted
        // round trip.
        command.SetAction((parseResult, cancellationToken) => RunCheck(parseResult.GetValue(jsonOption), cancellationToken));

        return command;
    }

    private static Command BuildCheck()
    {
        var jsonOption = JsonOutput.CreateOption();
        var command = new Command("check", $"Ask the release channel whether a newer dotquay exists (exit {UpdateAvailableExitCode} if so).")
        {
            jsonOption,
        };

        command.SetAction((parseResult, cancellationToken) => RunCheck(parseResult.GetValue(jsonOption), cancellationToken));

        return command;
    }

    private static async Task<int> RunCheck(bool json, CancellationToken cancellationToken)
    {
        try
        {
            var settings = SelfUpdateFactory.Settings();
            var result = await SelfUpdateFactory.Workflow(settings).CheckAsync(cancellationToken);

            // Asking explicitly refreshes what the passive notice reads, so 'self check' followed by
            // a normal command never contradicts itself.
            SelfUpdateFactory.Cache().Write(settings.ChannelUrl, result.LatestVersion);

            if (json)
            {
                Console.WriteLine(DotquayJson.Write(JsonEnvelope<SelfCheckJson>.Success(CheckCommandName, new SelfCheckJson
                {
                    CurrentVersion = result.CurrentVersion,
                    LatestVersion = result.LatestVersion,
                    UpdateAvailable = result.UpdateAvailable,
                    Channel = result.Channel,
                    Executable = result.ExecutablePath,
                    ReleasedAt = result.ReleasedAt?.ToString("O"),
                    AvailableRids = result.AvailableRids,
                })));

                return result.UpdateAvailable ? UpdateAvailableExitCode : 0;
            }

            if (result.LatestVersion is null)
            {
                Console.WriteLine($"{result.Channel} has published no releases yet. You are on {result.CurrentVersion}.");
                return 0;
            }

            if (!result.UpdateAvailable)
            {
                Console.WriteLine($"dotquay {result.CurrentVersion} is up to date ({result.Channel}).");
                return 0;
            }

            ConsoleOutput.WriteLine(
                ConsoleColor.Yellow,
                $"dotquay {result.LatestVersion} is available - you have {result.CurrentVersion}.");
            Console.WriteLine($"  run 'dotquay self update' to replace {result.ExecutablePath}");

            return UpdateAvailableExitCode;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return JsonOutput.Fail(json, CheckCommandName, ex);
        }
    }

    private static Command BuildUpdate()
    {
        var toOption = new Option<string?>("--to")
        {
            Description = "Install this exact version instead of the newest. Older is allowed: pinning back to a "
                + "known-good build is what this is for.",
        };

        var dryRunOption = new Option<bool>("--dry-run")
        {
            Description = "Report what would be downloaded and replaced, and write nothing.",
        };

        var forceOption = new Option<bool>("--force")
        {
            Description = "Reinstall even when already current, and skip the build-output guard. Never skips the "
                + "checksum or the check that the new binary starts.",
        };

        var jsonOption = JsonOutput.CreateOption();

        var command = new Command("update", "Download and install the newest dotquay, replacing this executable.")
        {
            toOption, dryRunOption, forceOption, jsonOption,
        };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var json = parseResult.GetValue(jsonOption);

            try
            {
                var settings = SelfUpdateFactory.Settings();
                var result = await SelfUpdateFactory.Workflow(settings).UpdateAsync(
                    new SelfUpdateOptions(
                        parseResult.GetValue(toOption),
                        parseResult.GetValue(forceOption),
                        parseResult.GetValue(dryRunOption)),
                    cancellationToken);

                // Whatever just happened, the cached answer is now wrong in the useful direction:
                // leaving it would print "0.18.0 is available" at the end of the command that
                // installed 0.18.0.
                SelfUpdateFactory.Cache().Write(settings.ChannelUrl, result.Changed ? result.ToVersion : null);

                return Report(json, UpdateCommandName, result);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return JsonOutput.Fail(json, UpdateCommandName, ex);
            }
        });

        return command;
    }

    private static Command BuildRollback()
    {
        var jsonOption = JsonOutput.CreateOption();
        var command = new Command("rollback", "Put back the dotquay the last update replaced.") { jsonOption };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var json = parseResult.GetValue(jsonOption);

            try
            {
                var result = await SelfUpdateFactory.Workflow(SelfUpdateFactory.Settings()).RollbackAsync(cancellationToken);
                return Report(json, RollbackCommandName, result);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return JsonOutput.Fail(json, RollbackCommandName, ex);
            }
        });

        return command;
    }

    private static Command BuildNotify()
    {
        var stateArgument = new Argument<string>("state")
        {
            Description = "on or off.",
        };

        var command = new Command(
            "notify",
            "Turn the one-line 'a newer dotquay exists' notice on or off. Never affects 'self check' or 'self update'.")
        {
            stateArgument,
        };

        command.SetAction(parseResult =>
        {
            var state = parseResult.GetValue(stateArgument)!;

            try
            {
                var enabled = state.ToLowerInvariant() switch
                {
                    "on" or "true" or "yes" => true,
                    "off" or "false" or "no" => false,
                    _ => throw new WorkflowException($"'{state}' is not a state. Use 'on' or 'off'."),
                };

                var store = SelfUpdateFactory.ConfigStore();
                store.SetNoticeEnabled(enabled);

                ConsoleOutput.WriteLine(
                    ConsoleColor.Green,
                    $"Update notices are {(enabled ? "on" : "off")} ({store.Path}).");

                if (!enabled)
                {
                    Console.WriteLine("  'dotquay self check' still works, and still reaches the network when you ask it to.");
                }

                return 0;
            }
            catch (Exception ex)
            {
                return JsonOutput.Fail(json: false, "self-notify", ex);
            }
        });

        return command;
    }

    private static int Report(bool json, string command, SelfUpdateResult result)
    {
        if (json)
        {
            Console.WriteLine(DotquayJson.Write(JsonEnvelope<SelfUpdateJson>.Success(command, new SelfUpdateJson
            {
                From = result.FromVersion,
                To = result.ToVersion,
                Changed = result.Changed,
                Executable = result.ExecutablePath,
                PreviousBinary = result.PreviousBinary,
                Messages = result.Messages,
            })));

            return 0;
        }

        foreach (var message in result.Messages)
        {
            ConsoleOutput.WriteLine(result.Changed ? ConsoleColor.Green : ConsoleColor.Gray, message);
        }

        return 0;
    }
}
