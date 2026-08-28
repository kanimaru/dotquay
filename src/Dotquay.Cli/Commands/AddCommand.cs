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

    /// <summary>
    /// The envelope's <c>command</c> for a preview run.
    /// </summary>
    /// <remarks>
    /// Deliberately not <c>"add"</c>. A consumer switches on this field to pick a parser, and
    /// <c>--preview</c> emits <see cref="AddPreviewJson"/> where <c>add</c> emits
    /// <see cref="OperationJson"/> — reporting both as "add" would hand the wrong parser a payload it
    /// would half-read rather than reject.
    /// </remarks>
    private const string PreviewCommandName = "add-preview";

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

        var previewOption = new Option<bool>("--preview")
        {
            Description = "Resolve what this add would do and report it without writing anything: the full "
                + "transitive addon graph, the NuGet packages it brings in, and any feeds its addons "
                + "would ask this project to trust. Git sources only.",
        };

        var adoptSourceOption = new Option<string[]>("--adopt-source")
        {
            Description = "Pre-approve an addon-declared NuGet feed by URL, as reported by --preview's "
                + "sources_needing_approval. Repeatable. Approval applies to this run only and never "
                + "widens [nuget] trusted_hosts.",
            AllowMultipleArgumentsPerToken = false,
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
            previewOption,
            adoptSourceOption,
            jsonOption,
        };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var source = parseResult.GetValue(sourceArgument)!;
            var json = parseResult.GetValue(jsonOption);
            var preview = parseResult.GetValue(previewOption);
            var adoptSources = parseResult.GetValue(adoptSourceOption) ?? [];
            var projectDirectory = GlobalOptions.Resolve(parseResult);

            // A preview must never adopt, so it gets no console prompt: the whole point is that it
            // reports the question instead of asking it. The real run replays the answer the user
            // already gave through --adopt-source, falling back to the console prompt when the flow
            // is a human at a terminal rather than the editor plugin.
            var (workflow, progress) = preview
                ? WorkflowFactory.CreateWithProgress(json)
                : WorkflowFactory.CreateWithProgress(json, allowSourceAdoption: true, preApprovedSourceUrls: adoptSources);

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
                    if (preview)
                    {
                        // A NuGet add pulls in no addons and declares no feeds, so a preview of one
                        // would be a dialog with nothing in it. Saying so beats emitting an empty
                        // payload the caller has to recognise as meaningless.
                        throw new WorkflowException(
                            $"--preview applies to git addons, but '{source}' is a NuGet package. A NuGet add "
                            + "resolves one version from feeds this project already declares — there is no "
                            + "dependency graph or feed adoption to preview.");
                    }

                    return await AddDotnetAsync(workflow, projectDirectory, source, options, json, progress, cancellationToken);
                }

                var (gitUrl, reference) = SplitRef(source);

                if (reference is not null)
                {
                    options = await workflow.ResolveShorthandAsync(gitUrl, reference, options, cancellationToken);
                }

                return preview
                    ? await PreviewGitAsync(workflow, projectDirectory, gitUrl, options, adoptSources, json, progress, cancellationToken)
                    : await AddGitAsync(workflow, projectDirectory, gitUrl, options, json, progress, cancellationToken);
            }
            catch (WorkflowException ex)
            {
                // Reported under the same name the success envelope would have used, so a consumer
                // that routes results by command handles a preview's failure in the dialog that asked
                // for it rather than in the panel's general log.
                return JsonOutput.Fail(json, preview ? PreviewCommandName : CommandName, ex);
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

    /// <summary>
    /// <c>add --preview</c>: resolves and reports, writes nothing.
    /// </summary>
    /// <remarks>
    /// Exit code 2, not 0 or 1, when a feed needs approval. Zero would tell a script the add is ready
    /// to run unattended when it isn't; one would say the preview failed, which it didn't — it
    /// succeeded at discovering a question. A caller that only checks for success still behaves
    /// correctly, because 2 is not 0.
    /// </remarks>
    private static async Task<int> PreviewGitAsync(
        DependencyWorkflow workflow,
        string projectDirectory,
        string gitUrl,
        AddonAddOptions options,
        IReadOnlyList<string> adoptSources,
        bool json,
        IDisposable progress,
        CancellationToken cancellationToken)
    {
        var preview = await workflow.PreviewAddAsync(projectDirectory, gitUrl, options, adoptSources, cancellationToken);
        progress.Dispose();

        var payload = JsonMapper.ToJson(preview);

        if (json)
        {
            Console.WriteLine(DotquayJson.Write(
                JsonEnvelope<AddPreviewJson>.Success(PreviewCommandName, payload, preview.Warnings)));

            return payload.NeedsApproval ? 2 : 0;
        }

        PrintPreview(payload);

        foreach (var warning in preview.Warnings)
        {
            ConsoleOutput.WriteLine(ConsoleColor.Yellow, warning);
        }

        return payload.NeedsApproval ? 2 : 0;
    }

    private static void PrintPreview(AddPreviewJson preview)
    {
        Console.WriteLine($"Adding '{preview.Name}' from {preview.Git} would resolve to:");
        Console.WriteLine();

        foreach (var addon in preview.Addons)
        {
            var version = addon.Version ?? addon.ShortSha;
            var origin = addon.RequiredBy.Contains(ProjectRequirer)
                ? ""
                : $"  (required by {string.Join(", ", addon.RequiredBy)})";

            var marker = addon switch
            {
                { IsNew: true, PreviousResolved: null } => "  +",
                { IsNew: true } => "  ~",
                _ => "   ",
            };

            var line = $"{marker} {addon.Name}  {version}{origin}";

            if (addon.Folder != addon.Name)
            {
                line += $"  -> addons/{addon.Folder}";
            }

            ConsoleOutput.WriteLine(addon.IsNew ? ConsoleColor.Green : ConsoleColor.Gray, line);
        }

        foreach (var package in preview.Dotnet.Where(d => d.IsNew))
        {
            var origin = package.DeclaredByAddon is null ? "" : $"  (required by {package.DeclaredByAddon})";
            ConsoleOutput.WriteLine(ConsoleColor.Green, $"  + {package.Name}  {package.Constraint ?? "latest"}  (NuGet){origin}");
        }

        PrintPreviewSources(
            "Feeds this project would take on automatically:",
            preview.SourcesToAdopt,
            ConsoleColor.Gray,
            explainWhy: true);

        PrintPreviewSources(
            "Feeds needing your approval before this add can complete:",
            preview.SourcesNeedingApproval,
            ConsoleColor.Yellow);

        if (preview.NeedsApproval)
        {
            Console.WriteLine();
            Console.WriteLine("Re-run with --adopt-source <url> for each feed you accept, or add its host to "
                + "[nuget] trusted_hosts in dotquay.toml.");
        }
    }

    /// <param name="explainWhy">Only the automatic list needs a reason — for the other list, the
    /// reason is the heading.</param>
    private static void PrintPreviewSources(
        string heading, IReadOnlyList<PreviewSourceJson> sources, ConsoleColor color, bool explainWhy = false)
    {
        if (sources.Count == 0)
        {
            return;
        }

        Console.WriteLine();
        ConsoleOutput.WriteLine(color, heading);

        foreach (var source in sources)
        {
            var reason = !explainWhy ? "" : source.IsTrustedHost ? " (trusted host)" : " (pre-approved)";
            ConsoleOutput.WriteLine(color, $"  {source.Url}");
            ConsoleOutput.WriteLine(color, $"    declared by addon '{source.DeclaredByAddon}'{reason}");

            if (source.Packages.Count > 0)
            {
                ConsoleOutput.WriteLine(color, $"    would own: {string.Join(", ", source.Packages)}");
            }

            if (source.PackageIds.Count > 0)
            {
                ConsoleOutput.WriteLine(color, $"    needed for: {string.Join(", ", source.PackageIds)}");
            }
        }
    }

    /// <summary>The synthetic requirer that means "you asked for this yourself". Referenced from the
    /// resolver's own constant rather than retyped, so the two can't drift apart.</summary>
    private const string ProjectRequirer = Dotquay.Core.Resolve.GraphConflict.ProjectRequirerName;

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
