using System.Security.Cryptography;
using System.Text;
using Dotquay.Core.Abstractions;

namespace Dotquay.Core.Sync;

/// <summary>
/// Computes a deterministic content hash of a directory tree — used to record <c>content_hash</c>
/// in the lockfile after install (SPEC.md §7.3) and to detect hand-modifications before
/// overwriting an addon (SPEC.md §9 step 4, requirement P0-6). Deterministic regardless of
/// filesystem enumeration order: relative paths are sorted before hashing, and each path is mixed
/// in alongside its content hash so a rename can never hash the same as the original layout.
/// </summary>
/// <remarks>
/// <para>
/// Editor-generated files are excluded (see <see cref="GodotArtifacts"/>). Hashing them made every
/// addon in every project read as hand-modified the moment Godot imported it.
/// </para>
/// <para>
/// <b>Line endings do not count (v0.23).</b> The recorded hash used to be taken over the files exactly as
/// git wrote them to disk, and git rewrites line endings per machine (<c>core.autocrlf</c>, <c>core.eol</c>):
/// the same commit is CRLF on a Windows box and LF on a Linux or macOS one. So the same addon at the same
/// commit had a different <c>content_hash</c> depending on who installed it, and every install on a machine
/// that disagreed with the last one rewrote the lockfile — a diff in every teammate's working tree, a merge
/// conflict waiting in every pull request, and a lockfile that could not tell "someone edited the addon" from
/// "someone has a different git config". The canonical hash therefore treats every text file as if it used LF.
/// What lands on disk is left exactly as git wrote it: only the <em>number</em> is normalised.
/// </para>
/// <para>
/// This costs no integrity. The hash is a local-edit detector for the folder on disk; what was downloaded is
/// pinned by the commit SHA, which is untouched.
/// </para>
/// </remarks>
public static class ContentHasher
{
    /// <summary>
    /// How many leading bytes decide whether a file is text. Git's own rule: a NUL in the first 8000
    /// bytes makes a file binary, and binary files are never line-ending converted — by git or here.
    /// </summary>
    private const int BinaryProbeBytes = 8000;

    /// <summary>
    /// Every form a lockfile might legitimately hold for one tree: the canonical hash, and the older ones
    /// written before line endings and editor files were taken out of it.
    /// </summary>
    /// <param name="Canonical">What is recorded for new installs: LF text, editor-generated files excluded.</param>
    /// <param name="Accepted">
    /// <see cref="Canonical"/> plus every form an older dotquay on any machine could have recorded for the same
    /// content: files as git wrote them (CRLF on Windows, LF elsewhere), and with or without the editor files.
    /// A recorded hash equal to any of these means the tree has not been edited.
    /// </param>
    public sealed record TreeHashes(string Canonical, IReadOnlyList<string> Accepted);

    /// <summary>Returns a <c>"sha256:&lt;hex&gt;"</c> string, matching the lockfile's
    /// <c>content_hash</c> format (SPEC.md §7.3). Line-ending independent: see the class remarks.</summary>
    public static string HashDirectory(IFileSystem fileSystem, string path) =>
        HashTree(fileSystem, path).Canonical;

    /// <summary>
    /// The pre-v0.9 hash, over every file including editor-generated ones, with files as git wrote them.
    /// </summary>
    /// <remarks>
    /// Kept for one purpose: an addon that <em>ships</em> its own <c>.uid</c> files had them folded
    /// into the hash recorded at install time, so excluding them now would change that addon's hash
    /// and report it modified — on content nobody touched. <see cref="DirtyChecker"/> accepts either
    /// form, and the next install records the current one. See SPEC.md §7.3.
    /// </remarks>
    public static string HashDirectoryIncludingArtifacts(IFileSystem fileSystem, string path) =>
        HashAsWrittenIncludingArtifacts(fileSystem, path);

    /// <summary>
    /// The canonical hash and every older form, in one pass over the tree: each file is read once, and the
    /// extra work is hashing the (small) text files in their other line-ending forms.
    /// </summary>
    public static TreeHashes HashTree(IFileSystem fileSystem, string path)
    {
        var relativePaths = fileSystem.EnumerateFiles(path, recursive: true)
            .Select(file => NormalizeRelative(file, path))
            .OrderBy(relative => relative, StringComparer.Ordinal)
            .ToList();

        // One incremental hash per (line-ending form, with/without editor files).
        var forms = new (LineEndings Endings, bool IncludeArtifacts, IncrementalHash Hash)[]
        {
            (LineEndings.Lf, false, IncrementalHash.CreateHash(HashAlgorithmName.SHA256)),       // canonical
            (LineEndings.AsWritten, false, IncrementalHash.CreateHash(HashAlgorithmName.SHA256)),
            (LineEndings.Crlf, false, IncrementalHash.CreateHash(HashAlgorithmName.SHA256)),
            (LineEndings.Lf, true, IncrementalHash.CreateHash(HashAlgorithmName.SHA256)),
            (LineEndings.AsWritten, true, IncrementalHash.CreateHash(HashAlgorithmName.SHA256)),
            (LineEndings.Crlf, true, IncrementalHash.CreateHash(HashAlgorithmName.SHA256)),
        };

        try
        {
            foreach (var relative in relativePaths)
            {
                var bytes = fileSystem.ReadAllBytes(fileSystem.CombinePath(path, relative));
                var isArtifact = GodotArtifacts.IsArtifact(relative);
                var name = Encoding.UTF8.GetBytes(relative);

                // The three byte variants of this file. Binary files share one digest between all three
                // rather than being hashed again.
                var isText = IsText(bytes);
                var asWritten = SHA256.HashData(bytes);
                var lf = isText ? SHA256.HashData(ToLf(bytes)) : asWritten;
                var crlf = isText ? SHA256.HashData(ToCrlf(bytes)) : asWritten;

                foreach (var (endings, includeArtifacts, hash) in forms)
                {
                    if (isArtifact && !includeArtifacts)
                    {
                        continue;
                    }

                    hash.AppendData(name);
                    hash.AppendData([0]); // separator: prevents "ab" + "c" colliding with "a" + "bc"
                    hash.AppendData(endings switch
                    {
                        LineEndings.Lf => lf,
                        LineEndings.Crlf => crlf,
                        _ => asWritten,
                    });
                }
            }

            var all = forms
                .Select(form => "sha256:" + Convert.ToHexString(form.Hash.GetHashAndReset()).ToLowerInvariant())
                .ToList();

            return new TreeHashes(all[0], all.Distinct(StringComparer.Ordinal).ToList());
        }
        finally
        {
            foreach (var (_, _, hash) in forms)
            {
                hash.Dispose();
            }
        }
    }

    private enum LineEndings
    {
        /// <summary>Exactly the bytes on disk.</summary>
        AsWritten,
        /// <summary>Text files with every line ending as LF.</summary>
        Lf,
        /// <summary>Text files with every line ending as CRLF.</summary>
        Crlf,
    }

    private static string HashAsWrittenIncludingArtifacts(IFileSystem fileSystem, string path)
    {
        var relativePaths = fileSystem.EnumerateFiles(path, recursive: true)
            .Select(file => NormalizeRelative(file, path))
            .OrderBy(relative => relative, StringComparer.Ordinal)
            .ToList();

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        foreach (var relative in relativePaths)
        {
            hash.AppendData(Encoding.UTF8.GetBytes(relative));
            hash.AppendData([0]);
            hash.AppendData(SHA256.HashData(fileSystem.ReadAllBytes(fileSystem.CombinePath(path, relative))));
        }

        return "sha256:" + Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    /// <summary>Git's rule for "is this text": no NUL byte in the first 8000.</summary>
    private static bool IsText(byte[] bytes)
    {
        var probe = Math.Min(bytes.Length, BinaryProbeBytes);
        return bytes.AsSpan(0, probe).IndexOf((byte)0) < 0;
    }

    /// <summary>CRLF becomes LF. A lone CR is left alone: it is not a line ending git rewrites.</summary>
    private static byte[] ToLf(byte[] bytes)
    {
        if (bytes.AsSpan().IndexOf((byte)'\r') < 0)
        {
            return bytes; // the common case on Linux and macOS: nothing to do, nothing allocated
        }

        var result = new byte[bytes.Length];
        var length = 0;
        for (var i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] == (byte)'\r' && i + 1 < bytes.Length && bytes[i + 1] == (byte)'\n')
            {
                continue; // drop the CR of a CRLF pair
            }

            result[length++] = bytes[i];
        }

        return result.AsSpan(0, length).ToArray();
    }

    /// <summary>Every line ending becomes CRLF (through LF first, so a mixed file ends up uniform).</summary>
    private static byte[] ToCrlf(byte[] bytes)
    {
        var lf = ToLf(bytes);
        var newlines = lf.AsSpan().Count((byte)'\n');
        if (newlines == 0)
        {
            return lf;
        }

        var result = new byte[lf.Length + newlines];
        var length = 0;
        foreach (var b in lf)
        {
            if (b == (byte)'\n')
            {
                result[length++] = (byte)'\r';
            }

            result[length++] = b;
        }

        return result;
    }

    private static string NormalizeRelative(string filePath, string root)
    {
        var relative = filePath[root.Length..].TrimStart('/', '\\');
        return relative.Replace('\\', '/');
    }
}
