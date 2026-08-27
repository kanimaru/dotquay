using System.Text;
using System.Text.RegularExpressions;

namespace Dotquay.Core.Manifest;

/// <summary>
/// Serializes manifests back to TOML text. Hand-written rather than routed through Tomlyn's
/// model-writer API so the exact output — dotted headers for dotnet deps, key ordering, the
/// commented scaffold <c>init</c> writes — is fully under our control and cheap to assert on in
/// tests byte-for-byte, instead of trusting a third-party formatter's whitespace choices.
/// </summary>
public static class ManifestWriter
{
    /// <summary>The friendly, commented starting point <c>dotquay init</c> writes (SPEC.md §6).
    /// <paramref name="nuGetSource"/> is the V3 service-index URL of a private NuGet feed, if any —
    /// leave null to use the public default (nuget.org). Credentials never go here; see
    /// <see cref="Manifest.ProjectManifest.NuGetSource"/>.</summary>
    public static string ScaffoldProjectToml(string? godotVersion, string? nuGetSource = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Dotquay project manifest.");
        sb.AppendLine("# Run `dotquay add <git-url>` or `dotquay add <nuget-package-id>` to add a dependency.");
        sb.AppendLine();
        sb.AppendLine("[project]");
        sb.AppendLine($"godot = {Quote(godotVersion ?? "4.3")}  # informational in v1 (SPEC.md §6)");

        if (nuGetSource is not null)
        {
            sb.AppendLine($"nuget_source = {Quote(nuGetSource)}  # V3 service-index URL; credentials come from");
            sb.AppendLine("                                    # DOTQUAY_NUGET_USERNAME / DOTQUAY_NUGET_TOKEN, never here");
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
    public static string ScaffoldPackageToml(string name, string version)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Dotquay package manifest — ships inside this addon's folder, next to plugin.cfg.");
        sb.AppendLine("# An addon repo is itself a valid dotquay project (SPEC.md §7.2).");
        sb.AppendLine();
        sb.AppendLine("[package]");
        sb.AppendLine($"name = {Quote(name)}");
        sb.AppendLine($"version = {Quote(version)}");
        sb.AppendLine();
        sb.AppendLine("# [addons.kani_core]");
        sb.AppendLine("# git = \"https://github.com/kani/kani_core\"");
        sb.AppendLine("# version = \"~1.4\"");
        return sb.ToString();
    }

    /// <summary>Canonical round-trip serialization of a fully-populated manifest — used by future
    /// <c>add</c>/<c>remove</c> commands and exercised directly by round-trip tests.</summary>
    public static string Serialize(ProjectManifest manifest)
    {
        var sb = new StringBuilder();

        if (manifest.Godot is not null || manifest.NuGetSource is not null)
        {
            sb.AppendLine("[project]");

            if (manifest.Godot is not null)
            {
                sb.AppendLine($"godot = {Quote(manifest.Godot)}");
            }

            if (manifest.NuGetSource is not null)
            {
                sb.AppendLine($"nuget_source = {Quote(manifest.NuGetSource)}");
            }
        }

        AppendAddons(sb, "addons", manifest.Addons);
        AppendDotnet(sb, "dotnet", manifest.Dotnet);
        AppendAddons(sb, "overrides", manifest.Overrides);

        return sb.ToString();
    }

    private static void AppendAddons(StringBuilder sb, string sectionKey, IReadOnlyDictionary<string, AddonSpec> addons)
    {
        foreach (var name in addons.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            var addon = addons[name];
            StartBlock(sb);

            sb.AppendLine($"[{sectionKey}.{FormatKey(name)}]");
            sb.AppendLine($"git = {Quote(addon.Git)}");

            switch (addon.SourceKind)
            {
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

            if (addon.Path is not null)
            {
                sb.AppendLine($"path = {Quote(addon.Path)}");
            }

            if (addon.InstallAs is not null)
            {
                sb.AppendLine($"install_as = {Quote(addon.InstallAs)}");
            }
        }
    }

    private static void AppendDotnet(StringBuilder sb, string sectionKey, IReadOnlyDictionary<string, DotnetSpec> dependencies)
    {
        foreach (var name in dependencies.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            var dependency = dependencies[name];
            StartBlock(sb);

            // Dotted headers (e.g. [dotnet.Newtonsoft.Json]) are plain TOML table-header syntax —
            // no need to build a nested object graph to emit them. See ManifestReader's matching
            // recursive read for why parsing this shape back out is the harder direction.
            var header = string.Join('.', dependency.Name.Split('.').Select(FormatKey));
            sb.AppendLine($"[{sectionKey}.{header}]");
            sb.AppendLine($"version = {Quote(dependency.Version)}");
        }
    }

    private static void StartBlock(StringBuilder sb)
    {
        if (sb.Length > 0)
        {
            sb.AppendLine();
        }
    }

    private static readonly Regex BareKeyPattern = new("^[A-Za-z0-9_-]+$", RegexOptions.Compiled);

    private static string FormatKey(string key) => BareKeyPattern.IsMatch(key) ? key : Quote(key);

    private static string Quote(string value) =>
        "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
}
