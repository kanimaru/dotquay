using Tomlyn;
using Tomlyn.Model;

namespace Dotquay.Core.Lockfile;

/// <summary>Parses <c>dotquay.lock</c> text into <see cref="DotquayLockfile"/>.</summary>
public static class LockfileReader
{
    public static DotquayLockfile Read(string text, string sourcePath = "dotquay.lock")
    {
        var table = Parse(text, sourcePath);

        var addons = new List<LockedAddon>();
        if (table.TryGetValue("addon", out var addonRaw) && addonRaw is TomlTableArray addonArray)
        {
            addons.AddRange(addonArray.Select(entry => ReadAddon(entry, sourcePath)));
        }

        var dotnet = new List<LockedDotnet>();
        if (table.TryGetValue("dotnet", out var dotnetRaw) && dotnetRaw is TomlTableArray dotnetArray)
        {
            dotnet.AddRange(dotnetArray.Select(entry => ReadDotnet(entry, sourcePath)));
        }

        return new DotquayLockfile { Addons = addons, Dotnet = dotnet };
    }

    private static TomlTable Parse(string text, string sourcePath)
    {
        var options = new TomlSerializerOptions { SourceName = sourcePath };
        try
        {
            return TomlSerializer.Deserialize<TomlTable>(text, options) ?? new TomlTable();
        }
        catch (TomlException ex)
        {
            throw new LockfileParseException($"{sourcePath}: invalid TOML.{Environment.NewLine}{ex.Message}", ex);
        }
    }

    private static LockedAddon ReadAddon(TomlTable entry, string sourcePath)
    {
        var name = GetString(entry, "name") ?? throw Missing(sourcePath, "addon", "name");
        var git = GetString(entry, "git") ?? throw Missing(sourcePath, "addon", "git");
        var resolved = GetString(entry, "resolved") ?? throw Missing(sourcePath, "addon", "resolved");
        var path = GetString(entry, "path") ?? throw Missing(sourcePath, "addon", "path");

        if (!LooksLikeFullSha(resolved))
        {
            throw new LockfileParseException(
                $"{sourcePath}: [[addon]] '{name}' has resolved = \"{resolved}\", which is not a full "
                + "40-character commit SHA (SPEC.md §7.3: \"resolved is always a full commit SHA\").");
        }

        return new LockedAddon
        {
            Name = name,
            Git = git,
            Resolved = resolved,
            Version = GetString(entry, "version"),
            Path = path,
            ContentHash = GetString(entry, "content_hash"),
            PatchHash = GetString(entry, "patch_hash"),
            PatchBaseSha = GetString(entry, "patch_base_sha"),
            Dependencies = GetStringList(entry, "dependencies"),
        };
    }

    private static LockedDotnet ReadDotnet(TomlTable entry, string sourcePath)
    {
        var name = GetString(entry, "name") ?? throw Missing(sourcePath, "dotnet", "name");
        var resolved = GetString(entry, "resolved") ?? throw Missing(sourcePath, "dotnet", "resolved");
        return new LockedDotnet { Name = name, Resolved = resolved };
    }

    private static bool LooksLikeFullSha(string value) => value.Length == 40 && value.All(Uri.IsHexDigit);

    private static LockfileParseException Missing(string sourcePath, string section, string key) =>
        new($"{sourcePath}: [[{section}]] entry is missing required key '{key}'.");

    private static string? GetString(TomlTable table, string key) =>
        table.TryGetValue(key, out var value) && value is string s ? s : null;

    private static List<string> GetStringList(TomlTable table, string key)
    {
        if (!table.TryGetValue(key, out var raw) || raw is not TomlArray array)
        {
            return [];
        }

        return array.OfType<string>().ToList();
    }
}
