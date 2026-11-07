using Dotquay.Core.Sync;
using Dotquay.Tests.Support;
using Xunit;

namespace Dotquay.Tests.Sync;

public class DirtyCheckerTests {
    [Fact]
    public void Check_AbsentDestination_IsNotDirty() {
        var fileSystem = new InMemoryFileSystem();

        var result = DirtyChecker.Check(fileSystem, "addons/foo",
            expectedContentHash: "sha256:whatever");

        Assert.False(result.IsDirty);
    }

    [Fact]
    public void Check_MatchingHash_IsNotDirty() {
        var fileSystem = new InMemoryFileSystem();
        fileSystem.WriteAllText("addons/foo/plugin.cfg", "cfg");
        var actualHash = ContentHasher.HashDirectory(fileSystem, "addons/foo");

        var result = DirtyChecker.Check(fileSystem, "addons/foo", expectedContentHash: actualHash);

        Assert.False(result.IsDirty);
    }

    [Fact]
    public void Check_MismatchedHash_IsDirty() {
        var fileSystem = new InMemoryFileSystem();
        fileSystem.WriteAllText("addons/foo/plugin.cfg", "hand-edited by the user");

        var result = DirtyChecker.Check(fileSystem, "addons/foo",
            expectedContentHash: "sha256:not-what-is-on-disk");

        Assert.True(result.IsDirty);
    }

    [Fact]
    public void Check_ExistingContentWithNoPriorRecord_IsDirty() {
        var fileSystem = new InMemoryFileSystem();
        fileSystem.WriteAllText("addons/foo/plugin.cfg", "something already here");

        var result = DirtyChecker.Check(fileSystem, "addons/foo", expectedContentHash: null);

        Assert.True(result.IsDirty);
    }
}
