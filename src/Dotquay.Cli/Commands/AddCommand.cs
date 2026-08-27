using System.CommandLine;
using Dotquay.Core.Json;
using Dotquay.Core.Workflows;

namespace Dotquay.Cli.Commands;

/// <summary>
/// <c>dotquay add</c> — add a dependency, resolve it, lock it, install it (SPEC.md §6).
/// </summary>
/// <remarks>
/// With no source named, a git URL resolves to the newest release tag (or a pin to the default
/// branch's tip for a repo with no tags). Since v0.6 the source can be stated outright instead —
/// either with <c>--branch</c>/<c>--rev</c>/<c>--version</c>, or as <c>&lt;url&gt;#ref</c>, which asks
/// the remote whether the ref is a branch, a tag, or a commit. "Give me this repo on main" is the most
/// common thing the tag-seeking default doesn't do, and it previously required hand-editing the
/// manifest afterwards.
/// </remarks>
internal static class AddCommand
{
    private const string CommandName = "add";

    public static Command Build()
    {
        var sourceArgument = new Argument<string>("source")
        {
            Description = "A git URL (optionally with #branch, #tag, or #commit) or a NuGet package ID.",
        };

        var pathOption = new Option<string?>("--path")
        {
            Description = "Git sources only. Subtree to extract from the addon's repo, if it isn't at "
                + "addons/<name> (e.g. the repo is named godot_dialogue_manager but the addon folder "
                + "inside it is just dialogue_manager). Repos where the addon lives at the repo root: pass \".\".",
        };

        var installAsOption = new Option<string?>("--install-as")
        {
            Description = "Git sources only. Folder to install into under addons/, if it shouldn't be the "
                + "addon's own name — some addons are preloaded by an exact path and must land under it.",
        };

        var versionOption = new Option<string?>("--version")
        {
            Description = "Version to add. For git, a constraint matched against tags (e.g. \"~2.5\"). For NuGet, "
                + "an exact package version. Defaults to the latest release either way.",
        };

        var branchOption = new Option<string?>("--branch")
        {
            Description = "Git sources only. Track a branch instead of a tag; the lockfile still pins the "
                + "resolved commit, so teammates get exactly what you tested.",
        };

        var revOption = new Option<string?>("--rev")
        {
            Description = "Git sources only. Pin to an exact commit.",
        };

        var jsonOption = JsonOutput.CreateOption();

        var command = new Command(CommandName, "Add a dependency (git URL or NuGet package ID), resolve it, lock it, and install it.")
        {
            sourceArgument,
            pathOption,
            installAsOption,
            versionOption,
            branchOption,
            revOption,
            jsonOption,
        };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var source = parseResult.GetValue(sourceArgument)!;
            var json = parseResult.GetValue(jsonOption);
            var projectDirectory = GlobalOptions.Resolve(parseResult);
            var (workflow, progress) = WorkflowFactory.CreateWithProgress(json);

            var options = new AddonAddOptions
            {
                Version = parseResult.GetValue(versionOption),
                Branch = parseResult.GetValue(branchOption),
                Rev = parseResult.GetValue(revOption),
                Path = parseResult.GetValue(pathOption),
                InstallAs = parseResult.GetValue(installAsOption),
            };

            try
            {
                if (!LooksLikeGitUrl(source))
                {
                    return await AddDotnetAsync(workflow, projectDirectory, source, options, json, progress, cancellationToken);
                }

                var (gitUrl, reference) = SplitRef(source);

                if (reference is not null)
                {
                    options = await workflow.ResolveShorthandAsync(gitUrl, reference, options, cancellationToken);
                }

                return await AddGitAsync(workflow, projectDirectory, gitUrl, options, json, progress, cancellationToken);
            }
            catch (WorkflowException ex)
            {
                return JsonOutput.Fail(json, CommandName, ex);
            }
            finally
            {
                progress.Dispose();
            }
        });

        return command;
    }

    /// <summary>
    /// Splits <c>https://host/repo#main</c> into the URL and the ref.
    /// </summary>
    /// <remarks>
    /// Splits on the <em>last</em> <c>#</c>, and only when something follows it. A fragment has no
    /// meaning in a git URL, so this can't collide with a real one — but a trailing bare <c>#</c> is a
    /// typo, not a request for an empty ref.
    /// </remarks>
    private static (string GitUrl, string? Reference) SplitRef(string source)
    {
        var index = source.LastIndexOf('#');
        return index > 0 && index < source.Length - 1
            ? (source[..index], source[(index + 1)..])
            : (source, null);
    }

    private static async Task<int> AddGitAsync(
        DependencyWorkflow workflow,
        string projectDirectory,
        string gitUrl,
        AddonAddOptions options,
        bool json,
        IDisposable progress,
        CancellationToken cancellationToken)
    {
        var locked = await workflow.AddAsync(projectDirectory, gitUrl, options, cancellationToken);
        progress.Dispose();

        var summary = $"Added '{locked.Name}' @ {locked.Version} ({ShortSha(locked.Resolved)}) -> addons/{locked.EffectiveFolder}";

        // Pseudo-version means no explicit source was given and BuildDefaultSpecAsync fell back to
        // pinning the default branch's tip (SPEC.md §6: "No tags found -> ... print a hint"). Naming a
        // ref yourself is a deliberate choice, so the nudge would just be noise.
        var untagged = !options.HasExplicitSource
            && locked.Version is not null
            && locked.Version.StartsWith("0.0.0-", StringComparison.Ordinal);

        var hint = $"'{locked.Name}' has no git tags — pinned to its current default-branch commit instead of a "
            + $"version range. Add it with --branch <name> to track a branch instead, or run 'dotquay update "
            + $"{locked.Name}' later to move to a newer commit.";

        if (json)
        {
            JsonOutput.WriteOperation(
                CommandName,
                new OperationJson
                {
                    Changes =
                    [
                        new ChangeJson
                        {
                            Name = locked.Name,
                            Kind = JsonMapper.ChangeKind.Addon,
                            Action = JsonMapper.ChangeAction.Added,
                            To = locked.Version ?? ShortSha(locked.Resolved),
                        },
                    ],
                    Messages = [summary],
                },
                untagged ? [hint] : null);

            return 0;
        }

        ConsoleOutput.WriteLine(ConsoleColor.Green, summary);

        if (untagged)
        {
            ConsoleOutput.WriteLine(ConsoleColor.Yellow, hint);
        }

        return 0;
    }

    private static async Task<int> AddDotnetAsync(
        DependencyWorkflow workflow,
        string projectDirectory,
        string packageId,
        AddonAddOptions options,
        bool json,
        IDisposable progress,
        CancellationToken cancellationToken)
    {
        // Git-only options against a NuGet id are a misunderstanding worth naming, not something to
        // accept and quietly ignore — the user would be left wondering why their branch had no effect.
        var gitOnly = new (string Flag, string? Value)[]
        {
            ("--branch", options.Branch),
            ("--rev", options.Rev),
            ("--path", options.Path),
            ("--install-as", options.InstallAs),
        }.Where(o => o.Value is not null).Select(o => o.Flag).ToList();

        if (gitOnly.Count > 0)
        {
            throw new WorkflowException(
                $"{string.Join(" and ", gitOnly)} {(gitOnly.Count == 1 ? "applies" : "apply")} to git addons, but "
                + $"'{packageId}' is a NuGet package. NuGet packages have no branches, commits, or install folder.");
        }

        var locked = await workflow.AddDotnetAsync(projectDirectory, packageId, options.Version, cancellationToken);
        progress.Dispose();

        var summary = $"Added '{locked.Name}' @ {locked.Resolved} (NuGet) -> .csproj, restored";

        if (json)
        {
            JsonOutput.WriteOperation(CommandName, new OperationJson
            {
                Changes =
                [
                    new ChangeJson
                    {
                        Name = locked.Name,
                        Kind = JsonMapper.ChangeKind.Dotnet,
                        Action = JsonMapper.ChangeAction.Added,
                        To = locked.Resolved,
                    },
                ],
                Messages = [summary],
            });
            return 0;
        }

        ConsoleOutput.WriteLine(ConsoleColor.Green, summary);
        return 0;
    }

    /// <summary>Distinguishes a git URL from a bare NuGet package ID (e.g. "Newtonsoft.Json").
    /// Package IDs don't contain "://" or start with an SSH-style "git@" — good enough to route
    /// v0.1's two supported `add` sources without a full URL grammar.</summary>
    private static bool LooksLikeGitUrl(string source) =>
        source.Contains("://", StringComparison.Ordinal) || source.StartsWith("git@", StringComparison.Ordinal);

    private static string ShortSha(string sha) => sha.Length > 7 ? sha[..7] : sha;
}
