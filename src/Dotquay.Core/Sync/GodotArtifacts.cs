namespace Dotquay.Core.Sync;

/// <summary>
/// Decides whether a path inside an addon folder is something the Godot editor generated rather than
/// something the addon ships.
/// </summary>
/// <remarks>
/// Godot writes into the folders dotquay manages, which puts it in direct conflict with requirement
/// P0-6's "the installed tree must match what we locked". Opening a project generates a
/// <c>&lt;script&gt;.gd.uid</c> next to every script (Godot 4.4+) and rewrites an
/// <c>&lt;asset&gt;.import</c> next to every imported asset — so a freshly installed addon reads as
/// hand-modified the first time the editor sees it, before the user has touched anything.
/// <para>
/// Left unhandled, that is worse than a cosmetic annoyance: <c>install</c> and <c>update</c> refuse to
/// overwrite a modified addon, so the fix people find is <c>--force</c>. A guard that fires on every
/// project teaches everyone to bypass it, and then it is not protecting the hand-edits it exists for.
/// </para>
/// <para>
/// A fixed list rather than a configurable one. These are facts about Godot, not preferences, and
/// making them a setting means every user has to discover and get it right before install works.
/// </para>
/// </remarks>
public static class GodotArtifacts
{
    /// <summary>Sidecars written next to the file they describe.</summary>
    /// <remarks>
    /// <c>.uid</c> is Godot 4.4+'s stable resource identifier for a script; <c>.import</c> is the
    /// import settings and cache pointer for an asset. Both are per-machine derivatives — and both
    /// are also committed by some addons, which is why the rule is about the <em>path</em>, not about
    /// who produced it.
    /// </remarks>
    private static readonly string[] Extensions = [".uid", ".import"];

    /// <summary>Directories whose entire contents are editor state.</summary>
    /// <remarks>
    /// <c>.godot/</c> is the project-level cache and should never appear inside an addon, but an addon
    /// whose repository is itself a Godot project can ship one (see the package-manifest-location
    /// rule in SPEC.md §7.2). <c>.import/</c> is Godot 3's equivalent, which still turns up in older
    /// addons.
    /// </remarks>
    private static readonly string[] Directories = [".godot", ".import"];

    /// <summary>Marks a file name as a temporary copy the editor made of a native library.</summary>
    /// <remarks>
    /// When the editor loads a GDExtension it does not load the library in place: it copies
    /// <c>GDMP.windows.dll</c> to <c>~GDMP.windows.dll</c> beside it and loads the copy, so the
    /// original can be rebuilt or replaced while the editor is open (hot reload). The copy is
    /// byte-identical to the original and exists only on the machine that opened the project.
    /// </remarks>
    private const char TemporaryPrefix = '~';

    /// <summary>
    /// True when <paramref name="relativePath"/> — forward-slashed and relative to the addon folder —
    /// is editor-generated.
    /// </summary>
    public static bool IsArtifact(string relativePath)
    {
        if (IsEditorTemporary(relativePath))
        {
            return true;
        }

        var normalized = relativePath.Replace('\\', '/');

        foreach (var extension in Extensions)
        {
            if (normalized.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        // Matched segment-wise rather than by substring: a legitimate folder named "my.godot-stuff"
        // must not be swept up because its name happens to start with one of these.
        foreach (var segment in normalized.Split('/'))
        {
            foreach (var directory in Directories)
            {
                if (string.Equals(segment, directory, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// True when <paramref name="relativePath"/> names a temporary copy the editor made of a native
    /// library (<c>~name.dll</c>): a file whose name — not a folder above it — starts with
    /// <c>~</c>.
    /// </summary>
    /// <remarks>
    /// A subset of <see cref="IsArtifact"/> with one extra property: it is <em>not</em> worth
    /// preserving. A <c>.uid</c> carries an identity other files reference, so the swap carries it
    /// over. A <c>~</c> copy carries nothing — Godot recreates it on the next load — and carrying
    /// it over would put a stale copy of the <em>old</em> library beside the new one. Reading it
    /// can also fail outright while the editor has it mapped.
    /// </remarks>
    public static bool IsEditorTemporary(string relativePath)
    {
        var fileName = relativePath.Substring(relativePath.LastIndexOfAny(['/', '\\']) + 1);

        return fileName.Length > 1 && fileName[0] == TemporaryPrefix;
    }
}
