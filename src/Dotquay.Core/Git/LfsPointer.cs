using System.Text;

namespace Dotquay.Core.Git;

/// <summary>
/// Recognises a Git LFS pointer: the few lines of text git keeps in a repository in place of a large
/// file, and which land in the working tree instead of the real file whenever LFS did not run.
/// </summary>
/// <remarks>
/// <para>
/// Why dotquay cares: it fetches with a plain <c>git fetch</c> and <c>git checkout</c>, so a repository
/// that stores its binaries in LFS arrives as real files only if the machine has git-lfs installed
/// <em>and</em> activated (<c>git lfs install</c>). Without it git does not fail — it writes the pointer
/// text and carries on, and the addon is installed, hashed into the lockfile and reported <c>clean</c>
/// with a 130-byte stand-in where a 12 MB library should be. Nothing then points back to the cause:
/// Godot just cannot load the extension.
/// </para>
/// <para>
/// The format is fixed by the LFS spec (https://github.com/git-lfs/git-lfs/blob/main/docs/spec.md):
/// <code>
/// version https://git-lfs.github.com/spec/v1
/// oid sha256:&lt;64 hex&gt;
/// size &lt;bytes&gt;
/// </code>
/// </para>
/// </remarks>
public static class LfsPointer
{
    public const string VersionLine = "version https://git-lfs.github.com/spec/v1";

    /// <summary>
    /// A pointer is a few lines, about 130 bytes. Anything larger is a real file that happens to
    /// mention LFS (documentation, say), not a pointer, so it is never mistaken for one.
    /// </summary>
    public const int MaxPointerBytes = 1024;

    /// <summary>True when <paramref name="content"/> is a complete LFS pointer file.</summary>
    public static bool Matches(ReadOnlySpan<byte> content)
    {
        if (content.Length == 0 || content.Length > MaxPointerBytes)
        {
            return false;
        }

        // The spec requires the version line first. LF or CRLF endings both occur: a checkout with
        // core.autocrlf rewrites them.
        var text = Encoding.UTF8.GetString(content);
        var lines = text.Split('\n');
        if (lines[0].TrimEnd('\r') != VersionLine)
        {
            return false;
        }

        // Version line alone is not enough — require the object id as well, so a one-line text file
        // that merely begins with the same words is left alone.
        foreach (var line in lines)
        {
            if (line.StartsWith("oid sha256:", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
