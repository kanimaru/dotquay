using System.Text;
using System.Text.RegularExpressions;

namespace Dotquay.Core.Manifest;

/// <summary>
/// Serializes manifests back to TOML text. Hand-written rather than routed through Tomlyn's
/// model-writer API so the exact output — dotted headers for dotnet deps, key ordering, the
/// commented scaffold <c>init</c> writes — is fully under our control and cheap to assert on in
/// tests byte-for-byte, instead of trusting a third-party formatter's whitespace choices.
/// </summary>
public static class ManifestWriter {
    /// <summary>The friendly, commented starting point <c>dotquay init</c> writes (SPEC.md §6).
    /// <paramref name="nuGetSource"/> is the V3 service-index URL of a private NuGet feed, if any —
    /// leave null to use the public default (nuget.org). Credentials never go here; see
    /// <see cref="Manifest.ProjectManifest.NuGetSources"/>.</summary>
    /// <remarks>
    /// Scaffolds the current <c>[nuget.sources.*]</c> form rather than the legacy
    /// <c>[project] nuget_source</c> key. Reading the legacy key stays supported indefinitely;
    /// writing it stops here, so nothing created from v0.7 onwards has to be migrated twice.
    /// </remarks>
    public static string ScaffoldProjectToml(string? nuGetSource = null) {
        var sb = new StringBuilder();
        sb.AppendLine("# Dotquay project manifest.");
        sb.AppendLine("# Run `dotquay add <git-url>` or `dotquay add <nuget-package-id>` to add a dependency.");

        // No section header unless there is something to put in it. An empty section is a prompt to
        // fill it in, and the only thing anyone ever filled it in with was a Godot version dotquay
        // never read.
        if (nuGetSource is not null) {
            sb.AppendLine();
            // Plain ASCII, like the rest of the scaffold: this file is committed and gets opened in
            // whatever editor or shell is to hand, where non-ASCII punctuation renders as mojibake in
            // anything reading it as ANSI.
            sb.AppendLine("# V3 service-index URL. Add more feeds as more [nuget.sources.<name>] blocks;");
            sb.AppendLine("# they are searched in the order written. Credentials come from `dotquay login`,");
            sb.AppendLine("# never from this file - it is meant to be committed.");
            sb.AppendLine($"[nuget.sources.{NuGetSourceSpec.LegacyName}]");
            sb.AppendLine($"url = {Quote(nuGetSource)}");
        }

        sb.AppendLine();
        sb.AppendLine("# [addons.dialogue_manager]");
        sb.AppendLine("# git = \"https://github.com/nathanhoad/godot_dialogue_manager\"");
        sb.AppendLine("# version = \"~2.44\"");
        sb.AppendLine();
        sb.AppendLine("# [dotnet.Newtonsoft.Json]");
        sb.AppendLine("# version = \"13.0.*\"");
        return sb.ToString();
    }

    /// <summary>The commented starting point <c>dotquay init --package</c> writes inside an addon
    /// folder, next to <c>plugin.cfg</c> (SPEC.md §6, §7.2).</summary>
    public static string ScaffoldPackageToml(string name) {
        var sb = new StringBuilder();
        sb.AppendLine("# Dotquay package manifest — ships inside this addon's folder, next to plugin.cfg.");
        sb.AppendLine("# It MUST live inside the folder consumers install, or its dependencies never reach them.");
        sb.AppendLine("# An addon repo is itself a valid dotquay project (SPEC.md §7.2).");
        sb.AppendLine();
        sb.AppendLine("[package]");
        sb.AppendLine($"name = {Quote(name)}");
        // No `version` line: plugin.cfg and the git tag already carry it, and a third copy only ever
        // drifts out of step with them.
        sb.AppendLine();
        sb.AppendLine("# [addons.kani_core]");
        sb.AppendLine("# git = \"https://github.com/kani/kani_core\"");
        sb.AppendLine("# version = \"~1.4\"");
        return sb.ToString();
    }

    /// <summary>Canonical round-trip serialization of a fully-populated manifest — used by future
    /// <c>add</c>/<c>remove</c> commands and exercised directly by round-trip tests.</summary>
    public static string Serialize(ProjectManifest manifest) {
        var sb = new StringBuilder();

        if (manifest.PatchesDirectory is not null) {
            sb.AppendLine("[project]");
            sb.AppendLine($"patches_dir = {Quote(manifest.PatchesDirectory)}");
        }

        AppendNuGet(sb, manifest);
        AppendAddons(sb, "addons", manifest.Addons);
        AppendDotnet(sb, "dotnet", manifest.Dotnet);
        AppendAddons(sb, "overrides", manifest.Overrides);

        return sb.ToString();
    }

    /// <summary>
    /// Emits <c>[nuget]</c> and every <c>[nuget.sources.*]</c> block.
    /// </summary>
    /// <remarks>
    /// <para>Not optional, and not cosmetic. <c>ProjectFileStore.WriteManifest</c> rewrites
    /// <c>dotquay.toml</c> wholesale from this method's output, so anything the model carries and
    /// this doesn't emit is <em>deleted</em> the next time an unrelated <c>add</c>, <c>remove</c>, or
    /// <c>override</c> runs. A reader that understood feeds while this didn't would silently erase a
    /// hand-written multi-feed configuration and surface it later as an unexplained
    /// <c>NU1101</c>.</para>
    /// <para>Ordering is load-bearing too: bare <c>[nuget]</c> keys must come before any
    /// <c>[nuget.sources.*]</c> sub-table, because writing the sub-table first implicitly defines
    /// <c>[nuget]</c> and a later explicit header is then a redefinition TOML rejects. Sources keep
    /// declaration order, since that order is the resolution rule (SPEC.md §11.1) — sorting them
    /// alphabetically, as this file does for addons, would silently change which feed wins.</para>
    /// </remarks>
    private static void AppendNuGet(StringBuilder sb, ProjectManifest manifest) {
        if (!manifest.NuGetSourceMapping || manifest.TrustedHosts.Count > 0) {
            StartBlock(sb);
            sb.AppendLine("[nuget]");

            if (!manifest.NuGetSourceMapping) {
                sb.AppendLine("source_mapping = false");
            }

            if (manifest.TrustedHosts.Count > 0) {
                sb.AppendLine($"trusted_hosts = [{string.Join(", ", manifest.TrustedHosts.Select(Quote))}]");
            }
        }

        foreach (var source in manifest.NuGetSources) {
            StartBlock(sb);

            // Attribution is regenerated from the model, never preserved as text: the whole file is
            // rewritten from scratch on every mutating command (see DESIGN-v0.7-AUTH.md §2.7), so a
            // comment that only existed in the previous file's bytes would vanish on the next write.
            if (source.DeclaredByAddon is not null) {
                sb.AppendLine($"# Adopted from addon '{source.DeclaredByAddon}'. Delete this block to stop using it.");
            }

            sb.AppendLine($"[nuget.sources.{FormatKey(source.Name)}]");
            sb.AppendLine($"url = {Quote(source.Url)}");

            if (source.Packages.Count > 0) {
                sb.AppendLine($"packages = [{string.Join(", ", source.Packages.Select(Quote))}]");
            }

            // A real key, not just the comment above it: the comment is for humans and the reader
            // cannot recover it, so attribution kept only as a comment would survive exactly one
            // rewrite and then silently become an unexplained feed.
            if (source.DeclaredByAddon is not null) {
                sb.AppendLine($"declared_by = {Quote(source.DeclaredByAddon)}");
            }
        }
    }

    private static void AppendAddons(StringBuilder sb, string sectionKey, IReadOnlyDictionary<string, AddonSpec> addons) {
        foreach (var name in addons.Keys.OrderBy(k => k, StringComparer.Ordinal)) {
            var addon = addons[name];
            StartBlock(sb);

            sb.AppendLine($"[{sectionKey}.{FormatKey(name)}]");
            sb.AppendLine($"git = {Quote(addon.Git)}");

            switch (addon.SourceKind) {
                case AddonSourceKind.Version:
                    sb.AppendLine($"version = {Quote(addon.Version!)}");
                    break;
                case AddonSourceKind.Branch:
                    sb.AppendLine($"branch = {Quote(addon.Branch!)}");
                    break;
                case AddonSourceKind.Rev:
                    sb.AppendLine($"rev = {Quote(addon.Rev!)}");
                    break;
            }

            if (addon.Path is not null) {
                sb.AppendLine($"path = {Quote(addon.Path)}");
            }

            if (addon.InstallAs is not null) {
                sb.AppendLine($"install_as = {Quote(addon.InstallAs)}");
            }
        }
    }

    private static void AppendDotnet(StringBuilder sb, string sectionKey, IReadOnlyDictionary<string, DotnetSpec> dependencies) {
        foreach (var name in dependencies.Keys.OrderBy(k => k, StringComparer.Ordinal)) {
            var dependency = dependencies[name];
            StartBlock(sb);

            // Dotted headers (e.g. [dotnet.Newtonsoft.Json]) are plain TOML table-header syntax —
            // no need to build a nested object graph to emit them. See ManifestReader's matching
            // recursive read for why parsing this shape back out is the harder direction.
            var header = string.Join('.', dependency.Name.Split('.').Select(FormatKey));
            sb.AppendLine($"[{sectionKey}.{header}]");
            sb.AppendLine($"version = {Quote(dependency.Version)}");

            if (dependency.Source is not null) {
                sb.AppendLine($"source = {Quote(dependency.Source)}");
            }

            // Fixed order regardless of how they were declared, so a read/write round-trip is
            // byte-stable and a manifest doesn't churn in git just for being rewritten.
            AppendAssetTags(sb, ManifestReader.AssetKeys.Include, dependency.Assets.Include);
            AppendAssetTags(sb, ManifestReader.AssetKeys.Exclude, dependency.Assets.Exclude);
            AppendAssetTags(sb, ManifestReader.AssetKeys.Private, dependency.Assets.Private);
        }
    }

    private static void AppendAssetTags(StringBuilder sb, string key, IReadOnlyList<string>? tags) {
        if (tags is null) {
            return;
        }

        sb.AppendLine($"{key} = [{string.Join(", ", tags.Select(Quote))}]");
    }

    private static void StartBlock(StringBuilder sb) {
        if (sb.Length > 0) {
            sb.AppendLine();
        }
    }

    private static readonly Regex BareKeyPattern = new("^[A-Za-z0-9_-]+$", RegexOptions.Compiled);

    private static string FormatKey(string key) => BareKeyPattern.IsMatch(key) ? key : Quote(key);

    private static string Quote(string value) =>
        "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
}
