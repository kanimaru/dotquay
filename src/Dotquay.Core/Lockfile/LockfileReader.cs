using System.Text.RegularExpressions;
using Tomlyn;
using Tomlyn.Model;

namespace Dotquay.Core.Lockfile;

/// <summary>Parses <c>dotquay.lock</c> text into <see cref="DotquayLockfile"/>.</summary>
public static partial class LockfileReader
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

        VerifyNothingWasDropped(text, sourcePath, addons.Count, dotnet.Count);

        return new DotquayLockfile { Addons = addons, Dotnet = dotnet };
    }

    /// <summary>
    /// Guards against silently reading fewer entries than the file contains.
    /// </summary>
    /// <remarks>
    /// Tomlyn does not merge a repeated array-of-tables header that is interrupted by a different
    /// one: given <c>[[addon]] … [[dotnet]] … [[addon]]</c>, the second <c>[[addon]]</c> group
    /// <em>replaces</em> the first rather than appending to it, so entries vanish with no error.
    /// (TOML itself says all blocks with the same header belong to one array, regardless of order.)
    /// <para>
    /// dotquay's own <see cref="LockfileWriter"/> always emits contiguous groups, so this can't happen
    /// to a file dotquay wrote — but <c>dotquay.lock</c> is committed to git (SPEC.md §7.3), and a
    /// hand-resolved merge conflict interleaving the blocks is entirely plausible. Losing addons
    /// silently from the one artifact whose whole job is reproducibility (goal #1) is the worst
    /// available outcome, so this counts the headers in the raw text and refuses to return a
    /// short read.
    /// </para>
    /// <para>
    /// Counting the raw text can only over-count if a header-shaped line appears inside a multi-line
    /// string. No lockfile field is multi-line — every value is a SHA, path, URL, or hash on one line
    /// — and the consequence of being wrong is a clear error rather than lost data.
    /// </para>
    /// </remarks>
    private static void VerifyNothingWasDropped(string text, string sourcePath, int addonCount, int dotnetCount)
    {
        var expectedAddons = ArrayHeaderPattern("addon").Count(text);
        var expectedDotnet = ArrayHeaderPattern("dotnet").Count(text);

        if (expectedAddons != addonCount)
        {
            throw new LockfileParseException(BuildMessage(sourcePath, "addon", expectedAddons, addonCount));
        }

        if (expectedDotnet != dotnetCount)
        {
            throw new LockfileParseException(BuildMessage(sourcePath, "dotnet", expectedDotnet, dotnetCount));
        }
    }

    private static string BuildMessage(string sourcePath, string section, int expected, int actual) =>
        $"{sourcePath}: found {expected} [[{section}]] block(s) but could only read {actual}. This happens when "
        + $"[[{section}]] blocks are separated by a different section — most often after a hand-resolved merge "
        + $"conflict. Group all [[{section}]] blocks together and try again. Refusing to continue rather than "
        + "silently dropping locked entries.";

    private static Regex ArrayHeaderPattern(string section) => section == "addon" ? AddonHeader() : DotnetHeader();

    [GeneratedRegex(@"^\s*\[\[\s*addon\s*\]\]", RegexOptions.Multiline)]
    private static partial Regex AddonHeader();

    [GeneratedRegex(@"^\s*\[\[\s*dotnet\s*\]\]", RegexOptions.Multiline)]
    private static partial Regex DotnetHeader();

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
            Folder = GetString(entry, "folder"),
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
