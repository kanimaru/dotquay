using Dotquay.Core.Sync;
using Dotquay.Tests.Support;
using Xunit;

namespace Dotquay.Tests.Sync;

public class ContentHasherTests {
    [Fact]
    public void HashDirectory_IsStableForIdenticalContent() {
        var fileSystem = new InMemoryFileSystem();
        fileSystem.WriteAllText("a/plugin.cfg", "cfg");
        fileSystem.WriteAllText("a/scripts/main.gd", "gd");
        fileSystem.WriteAllText("b/plugin.cfg", "cfg");
        fileSystem.WriteAllText("b/scripts/main.gd", "gd");

        var hashA = ContentHasher.HashDirectory(fileSystem, "a");
        var hashB = ContentHasher.HashDirectory(fileSystem, "b");

        Assert.Equal(hashA, hashB);
        Assert.StartsWith("sha256:", hashA);
    }

    [Fact]
    public void HashDirectory_ChangesWhenFileContentChanges() {
        var fileSystem = new InMemoryFileSystem();
        fileSystem.WriteAllText("a/plugin.cfg", "cfg-v1");
        var before = ContentHasher.HashDirectory(fileSystem, "a");

        fileSystem.WriteAllText("a/plugin.cfg", "cfg-v2");
        var after = ContentHasher.HashDirectory(fileSystem, "a");

        Assert.NotEqual(before, after);
    }

    [Fact]
    public void HashDirectory_ChangesWhenAFileIsRenamedEvenWithSameContent() {
        var fileSystem = new InMemoryFileSystem();
        fileSystem.WriteAllText("a/one.txt", "same content");
        var hashA = ContentHasher.HashDirectory(fileSystem, "a");

        var fileSystem2 = new InMemoryFileSystem();
        fileSystem2.WriteAllText("a/two.txt", "same content");
        var hashB = ContentHasher.HashDirectory(fileSystem2, "a");

        Assert.NotEqual(hashA, hashB);
    }

    [Fact]
    public void HashDirectory_EmptyDirectoryProducesAStableHash() {
        var fileSystem = new InMemoryFileSystem();
        fileSystem.CreateDirectory("empty");

        var hash = ContentHasher.HashDirectory(fileSystem, "empty");

        Assert.StartsWith("sha256:", hash);
    }
}
