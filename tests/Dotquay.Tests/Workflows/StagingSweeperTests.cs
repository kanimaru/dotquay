using Dotquay.Core.Workflows;
using Dotquay.Tests.Support;
using Xunit;

namespace Dotquay.Tests.Workflows;

/// <summary>
/// Covers the sweep of <c>.dotquay/staging/</c> that <c>install</c> and <c>update</c> run first.
/// </summary>
/// <remarks>
/// Staging holds scratch trees that live for one command, with one exception: a patch conflict
/// leaves its staged tree for the user to edit until <c>patch --resolve</c> or <c>--abandon</c>.
/// Everything else found there is left over from a run that failed or was killed.
/// </remarks>
public class StagingSweeperTests
{
    private const string Project = "project";
    private const string Staging = "project/.dotquay/staging";

    private static ProjectLayout Layout => ProjectLayout.WithoutPatches(Project);

    [Fact]
    public void Sweep_LeftoverTrees_AreDeleted()
    {
        var fileSystem = new InMemoryFileSystem();
        fileSystem.WriteAllText($"{Staging}/twitcher/plugin.gd", "extends EditorPlugin");
        fileSystem.WriteAllText($"{Staging}/graph-discovery/r3/dotquay.toml", "[package]");
        fileSystem.WriteAllText($"{Staging}/gut-patch-base/plugin.cfg", "cfg");

        StagingSweeper.Sweep(fileSystem, Layout);

        Assert.False(fileSystem.DirectoryExists($"{Staging}/twitcher"));
        Assert.False(fileSystem.DirectoryExists($"{Staging}/graph-discovery"));
        Assert.False(fileSystem.DirectoryExists($"{Staging}/gut-patch-base"));
    }

    [Fact]
    public void Sweep_PendingPatchConflict_IsKept()
    {
        // The user may be halfway through editing conflict markers in this tree.
        var fileSystem = new InMemoryFileSystem();
        fileSystem.WriteAllText($"{Staging}/gut/plugin.cfg", "<<<<<<< ours");
        fileSystem.WriteAllText($"{Staging}/gut.conflict", "2222\nv9.1.0\n");

        StagingSweeper.Sweep(fileSystem, Layout);

        Assert.Equal("<<<<<<< ours", fileSystem.ReadAllText($"{Staging}/gut/plugin.cfg"));
        Assert.True(fileSystem.FileExists($"{Staging}/gut.conflict"));
    }

    [Fact]
    public void Sweep_ConflictMarkerOfAnotherAddon_DoesNotProtectThisOne()
    {
        var fileSystem = new InMemoryFileSystem();
        fileSystem.WriteAllText($"{Staging}/gut/plugin.cfg", "<<<<<<< ours");
        fileSystem.WriteAllText($"{Staging}/gut.conflict", "2222\n");
        fileSystem.WriteAllText($"{Staging}/twitcher/plugin.gd", "extends EditorPlugin");

        StagingSweeper.Sweep(fileSystem, Layout);

        Assert.True(fileSystem.DirectoryExists($"{Staging}/gut"));
        Assert.False(fileSystem.DirectoryExists($"{Staging}/twitcher"));
    }

    [Fact]
    public void Sweep_StrayFiles_AreDeleted()
    {
        var fileSystem = new InMemoryFileSystem();
        fileSystem.WriteAllText($"{Staging}/half-written.tmp", "x");

        StagingSweeper.Sweep(fileSystem, Layout);

        Assert.False(fileSystem.FileExists($"{Staging}/half-written.tmp"));
    }

    [Fact]
    public void Sweep_NoStagingFolder_DoesNothing() =>
        StagingSweeper.Sweep(new InMemoryFileSystem(), Layout);
}
