namespace Dotquay.Core.Patch;

/// <summary>
/// Line-ending handling for patches, so a patch neither depends on how the trees it was captured
/// from or is applied to were checked out (an upstream with <c>* text=auto</c> lands with CRLF on
/// Windows, LF elsewhere) nor on the machine's <c>core.autocrlf</c>. Patches are captured from LF
/// copies, so they always hold LF; they are applied to an LF view of the target; and each file
/// the patch changed is written back in the line endings its original used, so no patched file
/// ends up with mixed endings and no file the patch left alone changes by a byte.
/// </summary>
internal static class LineEndings {
    /// <summary>The first bytes git itself looks at to tell binary from text.</summary>
    private const int BinarySniffLength = 8000;

    private const byte Cr = (byte)'\r';
    private const byte Lf = (byte)'\n';

    public enum Style {
        Lf,
        Crlf,
    }

    /// <summary>Git's own heuristic: a file is binary when a NUL byte occurs early on.</summary>
    public static bool IsText(byte[] content) =>
        content.AsSpan(0, Math.Min(content.Length, BinarySniffLength)).IndexOf((byte)0) < 0;

    public static bool HasCrlf(byte[] content) => content.AsSpan().IndexOf([Cr, Lf]) >= 0;

    /// <summary>The style most of the content's line breaks use; LF on a tie or none.</summary>
    public static Style Detect(byte[] content) {
        var crlf = 0;
        var lf = 0;
        for (var i = 0; i < content.Length; i++) {
            if (content[i] != Lf) {
                continue;
            }

            if (i > 0 && content[i - 1] == Cr) {
                crlf++;
            } else {
                lf++;
            }
        }

        return crlf > lf ? Style.Crlf : Style.Lf;
    }

    public static byte[] ToLf(byte[] content) {
        if (!HasCrlf(content)) {
            return content;
        }

        var result = new List<byte>(content.Length);
        for (var i = 0; i < content.Length; i++) {
            if (content[i] == Cr && i + 1 < content.Length && content[i + 1] == Lf) {
                continue;
            }

            result.Add(content[i]);
        }

        return result.ToArray();
    }

    public static byte[] To(Style style, byte[] content) {
        var lf = ToLf(content);
        if (style == Style.Lf) {
            return lf;
        }

        var result = new List<byte>(lf.Length + (lf.Length / 32));
        foreach (var b in lf) {
            if (b == Lf) {
                result.Add(Cr);
            }

            result.Add(b);
        }

        return result.ToArray();
    }

    /// <summary>Patch text with CRLF folded to LF, for patches captured before capture normalised
    /// (from CRLF trees on a machine without <c>core.autocrlf</c>). Base85 binary hunks hold no CR,
    /// so they pass through untouched.</summary>
    public static string ToLf(string text) => text.Replace("\r\n", "\n");
}
