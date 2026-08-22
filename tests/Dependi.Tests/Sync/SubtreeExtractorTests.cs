using Dependi.Core.Sync;
using Dependi.Tests.Support;
using Xunit;

namespace Dependi.Tests.Sync;

public class SubtreeExtractorTests
{
    [Fact]
    public void Extract_CopiesOnlyTheConfiguredSubtree()
    {
        var fileSystem = new InMemoryFileSystem();
        fileSystem.WriteAllText("repo/README.md", "not part of the addon");
        fileSystem.WriteAllText("repo/addons/foo/plugin.cfg", "cfg");
        fileSystem.WriteAllText("repo/addons/foo/scripts/main.gd", "gd");

        SubtreeExtractor.Extract(fileSystem, "repo", "addons/foo", "staging");

        Assert.True(fileSystem.FileExists("staging/plugin.cfg"));
        Assert.True(fileSystem.FileExists("staging/scripts/main.gd"));
        Assert.False(fileSystem.FileExists("staging/README.md"));
    }

    [Fact]
    public void Extract_WithDotPath_CopiesWholeRepoExceptGitMetadata()
    {
        var fileSystem = new InMemoryFileSystem();
        fileSystem.WriteAllText("repo/plugin.cfg", "cfg");
        fileSystem.WriteAllText("repo/.git/HEAD", "ref: refs/heads/main");
        fileSystem.WriteAllText("repo/.dependi-fetch-complete", "sha");

        SubtreeExtractor.Extract(fileSystem, "repo", ".", "staging");

        Assert.True(fileSystem.FileExists("staging/plugin.cfg"));
        Assert.False(fileSystem.FileExists("staging/.git/HEAD"));
        Assert.False(fileSystem.FileExists("staging/.dependi-fetch-complete"));
    }

    [Fact]
    public void Extract_PreservesBinaryContentByteForByte()
    {
        var fileSystem = new InMemoryFileSystem();
        byte[] binary = [0x00, 0xFF, 0x10, 0x80, 0x7F, 0xC3, 0x28]; // includes an invalid UTF-8 sequence
        fileSystem.WriteAllBytes("repo/addons/foo/icon.png", binary);

        SubtreeExtractor.Extract(fileSystem, "repo", "addons/foo", "staging");

        Assert.Equal(binary, fileSystem.ReadAllBytes("staging/icon.png"));
    }

    [Fact]
    public void Extract_ThrowsWhenConfiguredPathDoesNotExist()
    {
        var fileSystem = new InMemoryFileSystem();
        fileSystem.WriteAllText("repo/README.md", "hi");

        Assert.Throws<SyncException>(() => SubtreeExtractor.Extract(fileSystem, "repo", "addons/missing", "staging"));
    }

    [Fact]
    public void Extract_ThrowsWhenSubtreeIsEmptyAfterExclusions()
    {
        var fileSystem = new InMemoryFileSystem();
        fileSystem.CreateDirectory("repo/addons/empty");

        Assert.Throws<SyncException>(() => SubtreeExtractor.Extract(fileSystem, "repo", "addons/empty", "staging"));
    }

    [Fact]
    public void Extract_ReplacesRatherThanMergesExistingStaging()
    {
        var fileSystem = new InMemoryFileSystem();
        fileSystem.WriteAllText("staging/stale.txt", "from a previous run");
        fileSystem.WriteAllText("repoV1/addons/foo/plugin.cfg", "v1");
        fileSystem.WriteAllText("repoV2/addons/foo/plugin.cfg", "v2");

        SubtreeExtractor.Extract(fileSystem, "repoV2", "addons/foo", "staging");

        Assert.False(fileSystem.FileExists("staging/stale.txt"));
        Assert.Equal("v2", fileSystem.ReadAllText("staging/plugin.cfg"));
    }
}
