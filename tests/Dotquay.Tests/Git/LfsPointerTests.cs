using System.Text;
using Dotquay.Core.Git;
using Xunit;

namespace Dotquay.Tests.Git;

public class LfsPointerTests {
    private const string Oid = "968c222503ef1c9e27e1071361187c210ee72457ee6c6afebfd46aa845adca77";

    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    [Fact]
    public void Matches_ARealPointer()
        => Assert.True(LfsPointer.Matches(Bytes($"version https://git-lfs.github.com/spec/v1\noid sha256:{Oid}\nsize 2500000\n")));

    [Fact]
    public void Matches_APointerWithWindowsLineEndings_BecauseAutocrlfRewritesThem()
        => Assert.True(LfsPointer.Matches(Bytes($"version https://git-lfs.github.com/spec/v1\r\noid sha256:{Oid}\r\nsize 2500000\r\n")));

    [Fact]
    public void Matches_APointerWithNoTrailingNewline()
        => Assert.True(LfsPointer.Matches(Bytes($"version https://git-lfs.github.com/spec/v1\noid sha256:{Oid}\nsize 12")));

    [Fact]
    public void DoesNotMatch_AFileThatOnlyMentionsTheVersionLine() {
        // The one-line text file, and a README quoting the line mid-sentence: neither is a pointer, and
        // calling them one would make dotquay "repair" or refuse a perfectly good addon.
        Assert.False(LfsPointer.Matches(Bytes("version https://git-lfs.github.com/spec/v1\n")));
        Assert.False(LfsPointer.Matches(Bytes($"A pointer starts with `version https://git-lfs.github.com/spec/v1`.\noid sha256:{Oid}\n")));
    }

    [Fact]
    public void DoesNotMatch_TheVersionLineNotBeingFirst()
        => Assert.False(LfsPointer.Matches(Bytes($"# notes\nversion https://git-lfs.github.com/spec/v1\noid sha256:{Oid}\nsize 1\n")));

    [Fact]
    public void DoesNotMatch_ALargeFileEvenIfItBeginsLikeAPointer() {
        // Pointers are ~130 bytes. A big file that happens to start the same way is documentation.
        var big = $"version https://git-lfs.github.com/spec/v1\noid sha256:{Oid}\nsize 1\n" + new string('x', LfsPointer.MaxPointerBytes);
        Assert.False(LfsPointer.Matches(Bytes(big)));
    }

    [Fact]
    public void DoesNotMatch_EmptyOrBinaryContent() {
        Assert.False(LfsPointer.Matches([]));
        Assert.False(LfsPointer.Matches(new byte[] { 0x7f, 0x45, 0x4c, 0x46, 0x02, 0x01, 0x01, 0x00 }));
    }
}
