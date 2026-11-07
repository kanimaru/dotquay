using Xunit;

namespace Dotquay.Tests.Support;

/// <summary>Tests the test double itself. Worth the ceremony: every other test in this project
/// trusts <see cref="InMemoryFileSystem"/> to behave like a real filesystem, so a bug here would
/// silently invalidate everything that depends on it.</summary>
public class InMemoryFileSystemTests {
    [Fact]
    public void WriteAllText_ThenReadAllText_RoundTrips() {
        var fs = new InMemoryFileSystem();

        fs.WriteAllText("a/b/c.txt", "hello");

        Assert.True(fs.FileExists("a/b/c.txt"));
        Assert.Equal("hello", fs.ReadAllText("a/b/c.txt"));
    }

    [Fact]
    public void WriteAllText_ImplicitlyCreatesParentDirectories() {
        var fs = new InMemoryFileSystem();

        fs.WriteAllText("a/b/c.txt", "hello");

        Assert.True(fs.DirectoryExists("a"));
        Assert.True(fs.DirectoryExists("a/b"));
    }

    [Fact]
    public void ReadAllText_ThrowsForMissingFile() {
        var fs = new InMemoryFileSystem();

        Assert.Throws<FileNotFoundException>(() => fs.ReadAllText("missing.txt"));
    }

    [Fact]
    public void DeleteDirectory_RemovesNestedFilesAndSubdirectories() {
        var fs = new InMemoryFileSystem();
        fs.WriteAllText("addons/foo/plugin.cfg", "cfg");
        fs.WriteAllText("addons/foo/scripts/main.gd", "gd");

        fs.DeleteDirectory("addons/foo");

        Assert.False(fs.FileExists("addons/foo/plugin.cfg"));
        Assert.False(fs.FileExists("addons/foo/scripts/main.gd"));
        Assert.False(fs.DirectoryExists("addons/foo"));
    }

    [Fact]
    public void MoveDirectory_MovesNestedFilesAndReplacesExistingDestination() {
        var fs = new InMemoryFileSystem();
        fs.WriteAllText("staging/plugin.cfg", "new");
        fs.WriteAllText("addons/foo/plugin.cfg", "old");
        fs.WriteAllText("addons/foo/stale.txt", "stale");

        fs.MoveDirectory("staging", "addons/foo");

        Assert.Equal("new", fs.ReadAllText("addons/foo/plugin.cfg"));
        Assert.False(fs.FileExists("addons/foo/stale.txt"));
        Assert.False(fs.DirectoryExists("staging"));
    }

    [Fact]
    public void EnumerateFiles_NonRecursive_ExcludesNestedFiles() {
        var fs = new InMemoryFileSystem();
        fs.WriteAllText("addons/foo/plugin.cfg", "cfg");
        fs.WriteAllText("addons/foo/scripts/main.gd", "gd");

        var topLevel = fs.EnumerateFiles("addons/foo", recursive: false).ToList();
        var everything = fs.EnumerateFiles("addons/foo", recursive: true).ToList();

        Assert.Single(topLevel);
        Assert.Equal(2, everything.Count);
    }

    [Fact]
    public void EnumerateDirectories_DefaultsToTopLevelOnly() {
        var fs = new InMemoryFileSystem();
        fs.WriteAllText("addons/foo/plugin.cfg", "cfg");
        fs.WriteAllText("addons/foo/scripts/inner.gd", "gd");
        fs.WriteAllText("addons/bar/plugin.cfg", "cfg");

        var topLevel = fs.EnumerateDirectories("addons").ToList();

        Assert.Equal(2, topLevel.Count);
        Assert.Contains(topLevel, d => d.TrimEnd('/').EndsWith("foo", StringComparison.Ordinal));
        Assert.Contains(topLevel, d => d.TrimEnd('/').EndsWith("bar", StringComparison.Ordinal));
        Assert.DoesNotContain(topLevel, d => d.Contains("scripts", StringComparison.Ordinal));
    }

    [Fact]
    public void EnumerateDirectories_Recursive_IncludesNestedSubdirectories() {
        var fs = new InMemoryFileSystem();
        fs.WriteAllText("addons/foo/scripts/inner.gd", "gd");

        var everything = fs.EnumerateDirectories("addons", recursive: true).ToList();

        Assert.Contains(everything, d => d.TrimEnd('/').EndsWith("foo", StringComparison.Ordinal));
        Assert.Contains(everything, d => d.TrimEnd('/')
            .EndsWith("scripts", StringComparison.Ordinal));
    }

    [Fact]
    public void EnumerateDirectories_NoSubdirectories_ReturnsEmpty() {
        var fs = new InMemoryFileSystem();
        fs.WriteAllText("addons/foo.txt", "not a directory");

        var result = fs.EnumerateDirectories("addons").ToList();

        Assert.Empty(result);
    }
}
