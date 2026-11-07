using Dotquay.Core.IO;
using Xunit;

namespace Dotquay.Tests.IO;

/// <summary>
/// The one type in Dotquay.Core allowed to touch the real disk gets the one suite that touches
/// the real disk. Everything runs inside a fresh temp directory and cleans up after itself, so
/// nothing here depends on git, the network, or the machine's state.
/// </summary>
public sealed class SystemFileSystemTests : IDisposable {
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dotquay-fs-tests-" + Guid.NewGuid().ToString("N"));
    private readonly SystemFileSystem _fs = new();

    public SystemFileSystemTests() => Directory.CreateDirectory(_root);

    public void Dispose() {
        // Undo any read-only bits our own fixtures set, then remove the temp tree.
        if (Directory.Exists(_root)) {
            foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories)) {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(_root, recursive: true);
        }
    }

    /// <summary>
    /// Regression: git marks every pack file it writes read-only, and Directory.Delete refuses
    /// read-only files on Windows. RepoCache clears a half-fetched cache entry before retrying,
    /// so a single failed fetch used to make every later `update` of that addon crash with
    /// UnauthorizedAccessException on `.git/objects/pack/*.idx` until the cache was deleted by hand.
    /// </summary>
    [Fact]
    public void DeleteDirectory_RemovesATreeContainingReadOnlyFiles() {
        var target = Path.Combine(_root, "cache-entry");
        var pack = Path.Combine(target, ".git", "objects", "pack");
        Directory.CreateDirectory(pack);
        var idx = Path.Combine(pack, "pack-abc.idx");
        File.WriteAllText(idx, "idx");
        File.SetAttributes(idx, FileAttributes.ReadOnly);
        // A read-only directory too, since git can leave those behind as well.
        File.SetAttributes(pack, File.GetAttributes(pack) | FileAttributes.ReadOnly);

        _fs.DeleteDirectory(target);

        Assert.False(Directory.Exists(target), "a read-only pack file must not block deleting the cache entry");
    }

    [Fact]
    public void DeleteDirectory_OnAMissingPath_IsANoOp() {
        _fs.DeleteDirectory(Path.Combine(_root, "never-created"));
    }

    /// <summary>
    /// The highest-consequence invariant in the codebase (SPEC.md §7.4): `addons/&lt;folder&gt;`
    /// may be a link into a developer's own checkout. Deleting it must remove the link and
    /// nothing behind it — and that has to survive the read-only pass added for the pack-file
    /// bug, which is why this case sets a read-only file on the link's target: if the walk
    /// followed the link, the attribute on the target file would be stripped.
    /// </summary>
    [Fact]
    public void DeleteDirectory_OnALink_RemovesTheLinkAndLeavesTheTargetUntouched() {
        var target = Path.Combine(_root, "checkout");
        Directory.CreateDirectory(target);
        var kept = Path.Combine(target, "work.gd");
        File.WriteAllText(kept, "uncommitted work");
        File.SetAttributes(kept, FileAttributes.ReadOnly);

        var link = Path.Combine(_root, "addons", "vrm");
        _fs.CreateDirectoryLink(link, target);
        Assert.True(_fs.IsDirectoryLink(link));

        _fs.DeleteDirectory(link);

        Assert.False(Directory.Exists(link), "the link itself is removed");
        Assert.True(File.Exists(kept), "the target's content survives");
        Assert.Equal("uncommitted work", File.ReadAllText(kept));
        Assert.True((File.GetAttributes(kept) & FileAttributes.ReadOnly) != 0,
            "the read-only pass must not have walked through the link into the target");
    }

    /// <summary>A link nested *inside* a tree being deleted is removed as an entry of its own;
    /// the folder it points at is not entered.</summary>
    [Fact]
    public void DeleteDirectory_WithALinkNestedInside_DoesNotFollowIt() {
        var elsewhere = Path.Combine(_root, "elsewhere");
        Directory.CreateDirectory(elsewhere);
        var kept = Path.Combine(elsewhere, "keep.txt");
        File.WriteAllText(kept, "keep");
        File.SetAttributes(kept, FileAttributes.ReadOnly);

        var tree = Path.Combine(_root, "tree");
        Directory.CreateDirectory(tree);
        File.WriteAllText(Path.Combine(tree, "own.txt"), "own");
        _fs.CreateDirectoryLink(Path.Combine(tree, "linked"), elsewhere);

        _fs.DeleteDirectory(tree);

        Assert.False(Directory.Exists(tree));
        Assert.True(File.Exists(kept), "content behind a nested link must survive");
        Assert.True((File.GetAttributes(kept) & FileAttributes.ReadOnly) != 0,
            "the read-only pass must stop at the nested link");
    }
}
