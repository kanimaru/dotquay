using System.Xml.Linq;
using Dotquay.Core.Abstractions;
using Dotquay.Core.Dotnet;

namespace Dotquay.Core.Local;

/// <summary>
/// Generates <c>dotquay.local.targets</c> — the per-machine MSBuild overlay that swaps a linked
/// package's <c>PackageReference</c> for a <c>ProjectReference</c>
/// (docs/DESIGN-v0.11-LOCAL-DOTNET-LINK.md) — and installs the one-line hook that picks it up.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why an overlay and not an edit.</strong> The consuming <c>.csproj</c> is committed. A
/// <c>ProjectReference</c> to <c>F:\…\generator.csproj</c> written into it is a machine-local
/// absolute path in a tracked file — the exact failure <c>dotquay.local.toml</c> is gitignored to
/// prevent. With the overlay the <c>.csproj</c> keeps its <c>PackageReference</c> verbatim and
/// unlinking is just deleting a file, so there is no path by which a link can be committed.
/// </para>
/// <para>
/// <strong>Why <c>.targets</c> and not <c>.props</c>.</strong> MSBuild evaluates a project in
/// order, and <c>&lt;PackageReference Remove="…"/&gt;</c> deletes an item from the list <em>as it
/// exists at that point</em>. Removing something not yet declared is a legal, silent no-op.
/// Imported early — which is what <c>Directory.Build.props</c> is — the removal would do nothing,
/// the <c>ProjectReference</c> would sit beside a live <c>PackageReference</c>, and the build would
/// go on quietly using the published package. No error, no warning, wrong answer. This is this
/// design's counterpart to the addon link's "never follow a directory link when deleting" rule, and
/// <c>LocalTargetsTests</c> pins the ordering so it fails loudly if the hook ever moves.
/// </para>
/// <para>
/// <strong>Why the analyzer staging target lives in here too.</strong> A linked analyzer is the one
/// kind of link whose build output another process holds open: every Roslyn host loads an analyzer
/// assembly in place and keeps a file lock on it for as long as it lives, which stops the linked
/// generator from ever being rebuilt. The fix is a build-time copy, and it belongs in this file for
/// the same reason the swap does — it is a consequence of a link that exists on one machine, so
/// putting it in the committed <c>.csproj</c> would ask a whole team to carry one person's
/// development setup. See <see cref="RenderAnalyzerStaging"/>.
/// </para>
/// <para>
/// <strong>Why an own-named file.</strong> MSBuild imports only the <em>first</em>
/// <c>Directory.Build.targets</c> it finds walking up the tree and then stops, so generating one
/// could silently shadow a parent the repository already relies on. A dotquay-named file collides
/// with nothing and is greppable.
/// </para>
/// </remarks>
public static class LocalTargetsWriter {
    public const string FileName = "dotquay.local.targets";

    /// <summary>The build-time analyzer copy described in
    /// <see cref="RenderAnalyzerStaging"/>.</summary>
    public const string AnalyzerStagingTarget = "DotquayStageLinkedAnalyzers";

    private const string HookComment =
        " dotquay: per-machine local .NET links. Inert unless dotquay.local.targets exists, so "
        + "this line is safe to commit. ";

    /// <summary>
    /// Brings the overlay in line with <paramref name="links"/>: written whole when there is
    /// anything to say, deleted when there is not.
    /// </summary>
    /// <remarks>
    /// Regenerated wholesale rather than edited, because dotquay is the only writer of this file —
    /// which is also why none of v0.10's "did dotquay write this, or did the user" bookkeeping is
    /// needed here.
    /// </remarks>
    public static void Sync(
        IFileSystem fileSystem, string projectDirectory,
        IReadOnlyDictionary<string, DotnetLink> links) {
        var path = fileSystem.CombinePath(projectDirectory, FileName);

        if (links.Count == 0) {
            fileSystem.DeleteFile(path);

            // The hook stays. It is inert without this file (Condition="Exists(…)"), and removing
            // it would churn a committed file on every link/unlink cycle — including for teammates,
            // who would see a tracked line appear and disappear for a reason that exists only on
            // one machine.
            return;
        }

        fileSystem.WriteAllText(path, Render(links));
        EnsureHook(fileSystem, projectDirectory);
    }

    private static string Render(IReadOnlyDictionary<string, DotnetLink> links) {
        var itemGroup = new XElement("ItemGroup");

        foreach (var link in links.Values.OrderBy(l => l.PackageId, StringComparer.Ordinal)) {
            // Order matters for readability only — MSBuild applies both regardless — but keeping
            // the Remove beside its ProjectReference makes the swap obvious to anyone who opens the
            // file.
            itemGroup.Add(new XElement("PackageReference",
                new XAttribute("Remove", link.PackageId)));

            var reference = new XElement(
                "ProjectReference", new XAttribute("Include", link.ProjectPath));

            if (link.Analyzer) {
                reference.Add(new XAttribute("OutputItemType", "Analyzer"));
                reference.Add(new XAttribute("ReferenceOutputAssembly", "false"));
            }

            itemGroup.Add(reference);
        }

        var project = new XElement("Project", itemGroup);

        if (links.Values.Any(l => l.Analyzer)) {
            project.Add(RenderAnalyzerStaging());
        }

        var document = new XDocument(
            new XComment(
                $" Generated by dotquay from {LocalLinks.FileName}. Per-machine — do not commit."
                + $"{Environment.NewLine}     Edits here are overwritten on the next 'dotquay "
                + $"link'. "),
            project);

        return document.ToString(SaveOptions.None) + Environment.NewLine;
    }

    /// <summary>
    /// The MSBuild target that compiles against a content-addressed copy of every locally built
    /// analyzer instead of the analyzer itself.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The failure it prevents.</strong> A Roslyn host — Rider, an IDE, a Roslyn MCP server
    /// — loads an analyzer assembly in place and holds a file lock on it for its whole lifetime.
    /// For a package analyzer out of <c>~/.nuget/packages</c> that costs nothing, because nobody
    /// ever rebuilds it. For a <em>linked</em> one the lock lands on the generator's own build
    /// output, and the generator can then never be rebuilt: MSBuild fails with
    /// MSB3061/MSB3026/MSB3027, naming the host that holds the file. That takes the consuming
    /// project down with it, so the developer loop this whole feature exists to shorten stops
    /// working entirely — from the moment an editor opens the workspace.
    /// </para>
    /// <para>
    /// <strong>Why the file name is kept and the hash is not in it.</strong> Roslyn resolves an
    /// analyzer by its assembly name, so a copy renamed to carry its hash loads as nothing at all —
    /// <c>warning CS8034: Unable to load Analyzer assembly</c>, and because that is a warning the
    /// build goes on producing less code than it should. The hash has to be in the
    /// <em>directory</em> name instead, and it has to be there rather than one fixed staging
    /// folder: a rebuilt generator must land somewhere no host has had the chance to lock yet, or
    /// the copy inherits the very problem it was made to avoid.
    /// </para>
    /// <para>
    /// <strong>Why it stages for design-time builds too.</strong> The target is not conditioned on
    /// a real compilation, which is deliberate — a design-time build is exactly how an IDE or an
    /// MCP server is told which analyzer to load, so that is the build whose answer has to be the
    /// copy.
    /// </para>
    /// <para>
    /// <strong>Why the sweep shells out.</strong> Every generator build leaves one staged directory
    /// behind, so without a sweep <c>obj\</c> grows for as long as the link lives. A directory a
    /// host still has open cannot be deleted, and that is the <em>normal</em> state while a
    /// generator is being developed, not a fault — but <c>RemoveDir</c> reports it as <c>warning
    /// MSB3231</c> on every single build. <c>rmdir</c>/<c>rm</c> under <c>IgnoreExitCode</c> take
    /// what they can get and say nothing; whatever a host is still holding is swept by a later
    /// build, once that host has gone.
    /// </para>
    /// <para>
    /// Scope is every analyzer built outside the NuGet cache and the SDK, not only the ones dotquay
    /// linked. A hand-written <c>&lt;ProjectReference OutputItemType="Analyzer"&gt;</c> in the same
    /// project carries exactly the same lock, and narrowing this to dotquay's own links would leave
    /// it broken for no reason. <c>DisableDotquayAnalyzerStaging=true</c> opts out.
    /// </para>
    /// </remarks>
    private static IEnumerable<XNode> RenderAnalyzerStaging() {
        const string stageRoot = "$(_DotquayAnalyzerStageRoot)";

        yield return new XComment(
            " Roslyn hosts (an IDE, a Roslyn MCP server) load an analyzer assembly in place and "
            + $"hold a{Environment.NewLine}       file lock on it for as long as they live. On a "
            + $"linked generator that lock lands on the{Environment.NewLine}       generator's own "
            + $"build output and its next build fails with MSB3061/MSB3026, which "
            + $"takes{Environment.NewLine}       this project's build down with it. Compiling "
            + $"against a copy under obj\\ keeps the lock{Environment.NewLine}       off the "
            + $"original, and naming the copy's directory after the analyzer's content "
            + $"hash{Environment.NewLine}       makes a rebuilt generator land somewhere nothing "
            + $"has had the chance to lock yet. ");

        var target = new XElement(
            "Target",
            new XAttribute("Name", AnalyzerStagingTarget),
            new XAttribute("AfterTargets", "ResolveReferences"),
            new XAttribute("BeforeTargets", "CoreCompile"),
            new XAttribute(
                "Condition",
                "'$(DisableDotquayAnalyzerStaging)' != 'true' and '$(NuGetPackageRoot)' != '' "
                + "and '$(NetCoreRoot)' != '' and '@(Analyzer)' != ''"));

        target.Add(
            new XComment(
                " IntermediateOutputPath is defined by the SDK's .targets, which are imported after"
                + $"{Environment.NewLine}         this file — so the stage root is computed in "
                + $"here, where it has a value, and not{Environment.NewLine}         in a "
                + $"PropertyGroup beside the item swap above, where it would still be empty. "),
            new XElement(
                "PropertyGroup",
                new XElement(
                    "_DotquayAnalyzerStageRoot",
                    "$([MSBuild]::NormalizePath('$(MSBuildProjectDirectory)', "
                    + "'$(IntermediateOutputPath)', 'linked-analyzers'))")));

        target.Add(
            new XComment(
                " Analyzers out of the NuGet cache and out of the SDK are left alone: nothing ever"
                + $"{Environment.NewLine}         rebuilds those, so nothing is waiting on their "
                + $"lock. "),
            new XElement(
                "ItemGroup",
                new XElement(
                    "_DotquayLinkedAnalyzer",
                    new XAttribute("Include", "@(Analyzer)"),
                    new XAttribute(
                        "Condition",
                        "!$([System.String]::Copy('%(FullPath)')"
                        + ".StartsWith('$(NuGetPackageRoot)')) "
                        + "and "
                        + "!$([System.String]::Copy('%(FullPath)')"
                        + ".StartsWith('$(NetCoreRoot)'))"))));

        target.Add(new XElement(
            "GetFileHash",
            new XAttribute("Files", "@(_DotquayLinkedAnalyzer)"),
            new XAttribute("Algorithm", "SHA256"),
            new XAttribute("HashEncoding", "hex"),
            new XAttribute("Condition", "'@(_DotquayLinkedAnalyzer)' != ''"),
            new XElement(
                "Output",
                new XAttribute("TaskParameter", "Items"),
                new XAttribute("ItemName", "_DotquayHashedAnalyzer"))));

        target.Add(new XElement(
            "ItemGroup",
            new XComment(
                " The file name survives the copy untouched — Roslyn resolves an analyzer by its"
                + $"{Environment.NewLine}           assembly name, and a hash worked into the file "
                + $"name yields CS8034 instead. "),
            new XElement(
                "_DotquayStagedAnalyzer",
                new XAttribute(
                    "Include",
                    $"@(_DotquayHashedAnalyzer->'{stageRoot}\\%(Filename)-%(FileHash)"
                    + "\\%(Filename)%(Extension)')")),
            new XComment(
                " Everything beside the analyzer travels with it, so it can still resolve its own"
                + $"{Environment.NewLine}           dependencies out of the directory it was "
                + $"loaded from. "),
            new XElement(
                "_DotquayStagePayload",
                new XAttribute(
                    "Include",
                    "%(_DotquayHashedAnalyzer.RootDir)%(_DotquayHashedAnalyzer.Directory)*"),
                new XElement(
                    "StageDir",
                    $"{stageRoot}\\%(_DotquayHashedAnalyzer.Filename)"
                    + "-%(_DotquayHashedAnalyzer.FileHash)\\"))));

        target.Add(new XElement(
            "Copy",
            new XAttribute("SourceFiles", "@(_DotquayStagePayload)"),
            new XAttribute("DestinationFolder", "%(_DotquayStagePayload.StageDir)"),
            new XAttribute("SkipUnchangedFiles", "true"),
            new XAttribute("Condition", "'@(_DotquayStagePayload)' != ''")));

        target.Add(new XElement(
            "ItemGroup",
            new XAttribute("Condition", "'@(_DotquayStagedAnalyzer)' != ''"),
            new XElement("Analyzer", new XAttribute("Remove", "@(_DotquayLinkedAnalyzer)")),
            new XElement("Analyzer", new XAttribute("Include", "@(_DotquayStagedAnalyzer)"))));

        target.Add(
            new XComment(
                " Everything an earlier build of the generator staged — one directory per build, "
                + $"which{Environment.NewLine}         nothing would otherwise ever reclaim. "),
            new XElement(
                "ItemGroup",
                new XAttribute("Condition", $"Exists('{stageRoot}')"),
                new XElement(
                    "_DotquayStaleStage",
                    new XAttribute("Include",
                        $"$([System.IO.Directory]::GetDirectories('{stageRoot}'))"),
                    new XAttribute(
                        "Exclude",
                        $"@(_DotquayHashedAnalyzer->'{stageRoot}\\%(Filename)-%(FileHash)')"))));

        target.Add(new XComment(
            " Swept through the shell rather than with RemoveDir: a directory a host still has open"
            + $"{Environment.NewLine}         cannot be deleted, which is the normal state while a "
            + $"generator is being developed,{Environment.NewLine}         and RemoveDir reports "
            + $"exactly that as warning MSB3231 on every build. These two "
            + $"take{Environment.NewLine}         what they can get and say nothing; the rest goes "
            + $"once the host holding it has gone. "));

        foreach (var (platform, command) in new[]
        {
            ("==", "rmdir /s /q \"%(_DotquayStaleStage.FullPath)\""),
            ("!=", "rm -rf '%(_DotquayStaleStage.FullPath)'"),
        }) {
            target.Add(new XElement(
                "Exec",
                new XAttribute(
                    "Condition",
                    $"'@(_DotquayStaleStage)' != '' and '$(OS)' {platform} 'Windows_NT'"),
                new XAttribute("Command", command),
                new XAttribute("IgnoreExitCode", "true"),
                new XAttribute("StandardOutputImportance", "low"),
                new XAttribute("StandardErrorImportance", "low")));
        }

        yield return target;
    }

    /// <summary>
    /// Adds the conditional <c>&lt;Import&gt;</c> to the consuming <c>.csproj</c> if it isn't
    /// already there, as the <em>last</em> element of <c>&lt;Project&gt;</c>.
    /// </summary>
    /// <remarks>
    /// The only permanent change this feature makes to a tracked file, which is why it is
    /// idempotent and why it is one line. Position is not cosmetic: see the class remarks.
    /// </remarks>
    public static void EnsureHook(IFileSystem fileSystem, string projectDirectory) {
        string csprojPath;
        try {
            csprojPath = CsprojLocator.Locate(fileSystem, projectDirectory);
        } catch (DotnetSyncException ex) {
            // Linking a NuGet package in a project with no C# project file is a coherent thing to
            // ask for and an impossible thing to do; say which it is rather than surfacing a
            // PackageReference-flavoured message from a link command.
            throw new LocalLinkException(
                $"A local .NET link needs a project to import the overlay into. {ex.Message}", ex);
        }

        XDocument document;
        try {
            document = XDocument.Parse(fileSystem.ReadAllText(csprojPath));
        } catch (System.Xml.XmlException ex) {
            throw new LocalLinkException($"{csprojPath}: not valid "
                + $"XML.{Environment.NewLine}{ex.Message}", ex);
        }

        var project = document.Root;
        if (project is null || project.Name.LocalName != "Project") {
            throw new LocalLinkException($"{csprojPath}: expected a <Project> root element.");
        }

        if (HasHook(project)) {
            return;
        }

        project.Add(new XComment(HookComment));
        project.Add(new XElement(
            "Import",
            new XAttribute("Project", FileName),
            new XAttribute("Condition", $"Exists('{FileName}')")));

        fileSystem.WriteAllText(csprojPath, document.ToString(SaveOptions.None)
            + Environment.NewLine);
    }

    /// <summary>Whether the project already imports the overlay, however the line was
    /// written.</summary>
    public static bool HasHook(XElement project) => project.Descendants("Import").Any(IsHook);

    /// <summary>Whether <paramref name="import"/> is the overlay's <c>&lt;Import&gt;</c>.</summary>
    public static bool IsHook(XElement import) =>
        import.Name.LocalName == "Import"
        && string.Equals((string?)import.Attribute("Project"), FileName,
            StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether the project at <paramref name="projectDirectory"/> imports the overlay.
    /// Used by <c>doctor</c> to catch the overlay-without-hook case, which is a link that silently
    /// does nothing.</summary>
    public static bool HasHook(IFileSystem fileSystem, string csprojPath) {
        try {
            var root = XDocument.Parse(fileSystem.ReadAllText(csprojPath)).Root;
            return root is not null && HasHook(root);
        } catch (System.Xml.XmlException) {
            return false;
        }
    }
}
