using Dependi.Core.Sync;
using Dependi.Tests.Support;
using Xunit;

namespace Dependi.Tests.Sync;

public class SyncEngineTests
{
    [Fact]
    public void SwapAndHash_FreshInstall_MovesStagingAndReturnsHash()
    {
        var fileSystem = new InMemoryFileSystem();
        fileSystem.WriteAllText("staging/plugin.cfg", "cfg");
        var engine = new SyncEngine(fileSystem);

        var hash = engine.SwapAndHash("staging", "addons/foo", expectedContentHash: null, force: false);

        Assert.False(fileSystem.DirectoryExists("staging"));
        Assert.True(fileSystem.FileExists("addons/foo/plugin.cfg"));
        Assert.StartsWith("sha256:", hash);
    }

    [Fact]
    public void SwapAndHash_DirtyDestinationWithoutForce_ThrowsAndLeavesBothTreesUntouched()
    {
        var fileSystem = new InMemoryFileSystem();
        fileSystem.WriteAllText("staging/plugin.cfg", "new version");
        fileSystem.WriteAllText("addons/foo/plugin.cfg", "hand-edited");
        var engine = new SyncEngine(fileSystem);

        Assert.Throws<SyncException>(() => engine.SwapAndHash("staging", "addons/foo", expectedContentHash: "sha256:original", force: false));

        Assert.True(fileSystem.FileExists("staging/plugin.cfg"));
        Assert.Equal("hand-edited", fileSystem.ReadAllText("addons/foo/plugin.cfg"));
    }

    [Fact]
    public void SwapAndHash_DirtyDestinationWithForce_Overwrites()
    {
        var fileSystem = new InMemoryFileSystem();
        fileSystem.WriteAllText("staging/plugin.cfg", "new version");
        fileSystem.WriteAllText("addons/foo/plugin.cfg", "hand-edited");
        var engine = new SyncEngine(fileSystem);

        engine.SwapAndHash("staging", "addons/foo", expectedContentHash: "sha256:original", force: true);

        Assert.Equal("new version", fileSystem.ReadAllText("addons/foo/plugin.cfg"));
    }

    [Fact]
    public void SwapAndHash_MatchingExpectedHash_SwapsWithoutForce()
    {
        var fileSystem = new InMemoryFileSystem();
        fileSystem.WriteAllText("addons/foo/plugin.cfg", "unchanged");
        var expectedHash = ContentHasher.HashDirectory(fileSystem, "addons/foo");
        fileSystem.WriteAllText("staging/plugin.cfg", "updated");

        var engine = new SyncEngine(fileSystem);
        engine.SwapAndHash("staging", "addons/foo", expectedContentHash: expectedHash, force: false);

        Assert.Equal("updated", fileSystem.ReadAllText("addons/foo/plugin.cfg"));
    }
}
