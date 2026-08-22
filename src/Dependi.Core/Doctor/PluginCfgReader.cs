using Dependi.Core.Abstractions;
using Tomlyn;
using Tomlyn.Model;

namespace Dependi.Core.Doctor;

/// <summary>
/// Reads the <c>version</c> field out of a Godot addon's <c>plugin.cfg</c> — backs <c>dependi
/// doctor</c>'s "plugin.cfg version drift" check (SPEC.md §6, requirement P1-4). Godot's own
/// ConfigFile format for <c>plugin.cfg</c> (<c>[section]</c> headers plus <c>key="value"</c> pairs)
/// happens to be valid TOML for every real-world example surveyed (name/description/author/version/
/// script are all plain quoted strings), so this reuses Tomlyn — the same library
/// <see cref="Manifest.ManifestReader"/> and <see cref="Lockfile.LockfileReader"/> already depend
/// on — rather than writing a second bespoke parser for a format that's a strict subset of one
/// dependi already speaks.
/// </summary>
/// <remarks>
/// Deliberately lenient everywhere: <c>doctor</c> is a sanity check, not a gate. A missing file, a
/// <c>plugin.cfg</c> that isn't valid TOML (a genuinely non-standard addon, or a GDScript-flavored
/// value TOML doesn't accept), a missing <c>[plugin]</c> section, or a missing/non-string
/// <c>version</c> key all report "nothing to say" (null) rather than throwing — one addon's
/// unreadable metadata should never crash the whole <c>doctor</c> run.
/// </remarks>
public static class PluginCfgReader
{
    public static string? TryReadVersion(IFileSystem fileSystem, string pluginCfgPath)
    {
        if (!fileSystem.FileExists(pluginCfgPath))
        {
            return null;
        }

        TomlTable table;
        try
        {
            table = TomlSerializer.Deserialize<TomlTable>(fileSystem.ReadAllText(pluginCfgPath)) ?? new TomlTable();
        }
        catch (TomlException)
        {
            return null;
        }

        if (!table.TryGetValue("plugin", out var pluginObj) || pluginObj is not TomlTable pluginTable)
        {
            return null;
        }

        return pluginTable.TryGetValue("version", out var versionObj) && versionObj is string version ? version : null;
    }
}
