using Dotquay.Core.Patch;
using Dotquay.Tests.Support;
using Xunit;

namespace Dotquay.Tests.Patch;

/// <summary>Exercises every presence-combination branch <see cref="PatchMerger"/> documents (its own
/// remarks list all eight) directly against three hand-built directory trees — no addon
/// install/patch-capture machinery involved, so a failure here points straight at the merge logic
/// itself.</summary>
public class PatchMergerTests
{
    private const string BaseDir = "base";
    private const string OursDir = "ours";
    private const string TheirsDir = "theirs";
    private const string DestDir = "dest";

    private static (PatchMerger Merger, InMemoryFileSystem FileSystem, FakePatchTool PatchTool) Build()
    {
        var fileSystem = new InMemoryFileSystem();
        var patchTool = new FakePatchTool(fileSystem);
        return (new PatchMerger(fileSystem, patchTool), fileSystem, patchTool);
    }

    [Fact]
    public async Task MergeAsync_AddedOnlyInOurs_KeptAsIs()
    {
        var (merger, fileSystem, _) = Build();
        fileSystem.WriteAllText($"{OursDir}/new.txt", "ours-added");

        var result = await merger.MergeAsync(BaseDir, OursDir, TheirsDir, DestDir);

        Assert.False(result.HasConflicts);
        Assert.Equal("ours-added", fileSystem.ReadAllText($"{DestDir}/new.txt"));
    }

    [Fact]
    public async Task MergeAsync_AddedOnlyInTheirs_KeptAsIs()
    {
        var (merger, fileSystem, _) = Build();
        fileSystem.WriteAllText($"{TheirsDir}/new.txt", "theirs-added");

        var result = await merger.MergeAsync(BaseDir, OursDir, TheirsDir, DestDir);

        Assert.False(result.HasConflicts);
        Assert.Equal("theirs-added", fileSystem.ReadAllText($"{DestDir}/new.txt"));
    }

    [Fact]
    public async Task MergeAsync_AddedIdenticallyInBoth_NoConflict()
    {
        var (merger, fileSystem, _) = Build();
        fileSystem.WriteAllText($"{OursDir}/new.txt", "same content");
        fileSystem.WriteAllText($"{TheirsDir}/new.txt", "same content");

        var result = await merger.MergeAsync(BaseDir, OursDir, TheirsDir, DestDir);

        Assert.False(result.HasConflicts);
        Assert.Equal("same content", fileSystem.ReadAllText($"{DestDir}/new.txt"));
    }

    [Fact]
    public async Task MergeAsync_AddedDifferentlyInBoth_AddAddConflict()
    {
        var (merger, fileSystem, patchTool) = Build();
        fileSystem.WriteAllText($"{OursDir}/new.txt", "ours-version");
        fileSystem.WriteAllText($"{TheirsDir}/new.txt", "theirs-version");
        patchTool.DefaultConflictCount = 1;

        var result = await merger.MergeAsync(BaseDir, OursDir, TheirsDir, DestDir);

        Assert.True(result.HasConflicts);
        Assert.Equal(["new.txt"], result.ConflictedPaths);
    }

    [Fact]
    public async Task MergeAsync_DeletedByOursUpstreamLeftAlone_CleanDelete()
    {
        var (merger, fileSystem, _) = Build();
        fileSystem.WriteAllText($"{BaseDir}/gone.txt", "original");
        fileSystem.WriteAllText($"{TheirsDir}/gone.txt", "original");
        // Not present in OursDir -> deleted by our patch.

        var result = await merger.MergeAsync(BaseDir, OursDir, TheirsDir, DestDir);

        Assert.False(result.HasConflicts);
        Assert.False(fileSystem.FileExists($"{DestDir}/gone.txt"));
    }

    [Fact]
    public async Task MergeAsync_DeletedByOursButUpstreamModifiedIt_DeleteModifyConflictKeepsTheirs()
    {
        var (merger, fileSystem, _) = Build();
        fileSystem.WriteAllText($"{BaseDir}/contested.txt", "original");
        fileSystem.WriteAllText($"{TheirsDir}/contested.txt", "upstream-changed");
        // Not present in OursDir -> deleted by our patch, but upstream touched it since base.

        var result = await merger.MergeAsync(BaseDir, OursDir, TheirsDir, DestDir);

        Assert.True(result.HasConflicts);
        Assert.Equal(["contested.txt"], result.ConflictedPaths);
        Assert.Equal("upstream-changed", fileSystem.ReadAllText($"{DestDir}/contested.txt"));
    }

    [Fact]
    public async Task MergeAsync_DeletedUpstreamOursLeftAlone_CleanDelete()
    {
        var (merger, fileSystem, _) = Build();
        fileSystem.WriteAllText($"{BaseDir}/gone.txt", "original");
        fileSystem.WriteAllText($"{OursDir}/gone.txt", "original");
        // Not present in TheirsDir -> deleted upstream.

        var result = await merger.MergeAsync(BaseDir, OursDir, TheirsDir, DestDir);

        Assert.False(result.HasConflicts);
        Assert.False(fileSystem.FileExists($"{DestDir}/gone.txt"));
    }

    [Fact]
    public async Task MergeAsync_DeletedUpstreamButOursModifiedIt_DeleteModifyConflictKeepsOurs()
    {
        var (merger, fileSystem, _) = Build();
        fileSystem.WriteAllText($"{BaseDir}/contested.txt", "original");
        fileSystem.WriteAllText($"{OursDir}/contested.txt", "our-edit");
        // Not present in TheirsDir -> deleted upstream, but our patch touched it since base.

        var result = await merger.MergeAsync(BaseDir, OursDir, TheirsDir, DestDir);

        Assert.True(result.HasConflicts);
        Assert.Equal(["contested.txt"], result.ConflictedPaths);
        Assert.Equal("our-edit", fileSystem.ReadAllText($"{DestDir}/contested.txt"));
    }

    [Fact]
    public async Task MergeAsync_DeletedByBoth_CleanDelete()
    {
        var (merger, fileSystem, _) = Build();
        fileSystem.WriteAllText($"{BaseDir}/gone.txt", "original");
        // Absent from both OursDir and TheirsDir.

        var result = await merger.MergeAsync(BaseDir, OursDir, TheirsDir, DestDir);

        Assert.False(result.HasConflicts);
        Assert.False(fileSystem.FileExists($"{DestDir}/gone.txt"));
    }

    [Fact]
    public async Task MergeAsync_PresentInAllThreeIdentical_NoConflict()
    {
        var (merger, fileSystem, _) = Build();
        fileSystem.WriteAllText($"{BaseDir}/f.txt", "same");
        fileSystem.WriteAllText($"{OursDir}/f.txt", "same");
        fileSystem.WriteAllText($"{TheirsDir}/f.txt", "same");

        var result = await merger.MergeAsync(BaseDir, OursDir, TheirsDir, DestDir);

        Assert.False(result.HasConflicts);
        Assert.Equal("same", fileSystem.ReadAllText($"{DestDir}/f.txt"));
    }

    [Fact]
    public async Task MergeAsync_OnlyOursChanged_TakesOursWithoutCallingMergeTool()
    {
        var (merger, fileSystem, patchTool) = Build();
        fileSystem.WriteAllText($"{BaseDir}/f.txt", "original");
        fileSystem.WriteAllText($"{OursDir}/f.txt", "our-edit");
        fileSystem.WriteAllText($"{TheirsDir}/f.txt", "original");
        patchTool.DefaultConflictCount = 1; // would fail the test if the merge tool were invoked

        var result = await merger.MergeAsync(BaseDir, OursDir, TheirsDir, DestDir);

        Assert.False(result.HasConflicts);
        Assert.Equal("our-edit", fileSystem.ReadAllText($"{DestDir}/f.txt"));
    }

    [Fact]
    public async Task MergeAsync_OnlyTheirsChanged_TakesTheirsWithoutCallingMergeTool()
    {
        var (merger, fileSystem, patchTool) = Build();
        fileSystem.WriteAllText($"{BaseDir}/f.txt", "original");
        fileSystem.WriteAllText($"{OursDir}/f.txt", "original");
        fileSystem.WriteAllText($"{TheirsDir}/f.txt", "upstream-edit");
        patchTool.DefaultConflictCount = 1; // would fail the test if the merge tool were invoked

        var result = await merger.MergeAsync(BaseDir, OursDir, TheirsDir, DestDir);

        Assert.False(result.HasConflicts);
        Assert.Equal("upstream-edit", fileSystem.ReadAllText($"{DestDir}/f.txt"));
    }

    [Fact]
    public async Task MergeAsync_BothChangedIdentically_NoConflict()
    {
        var (merger, fileSystem, patchTool) = Build();
        fileSystem.WriteAllText($"{BaseDir}/f.txt", "original");
        fileSystem.WriteAllText($"{OursDir}/f.txt", "same-edit");
        fileSystem.WriteAllText($"{TheirsDir}/f.txt", "same-edit");
        patchTool.DefaultConflictCount = 1; // would fail the test if the merge tool were invoked

        var result = await merger.MergeAsync(BaseDir, OursDir, TheirsDir, DestDir);

        Assert.False(result.HasConflicts);
    }

    [Fact]
    public async Task MergeAsync_BothChangedDifferently_ClashesThroughMergeTool()
    {
        var (merger, fileSystem, patchTool) = Build();
        fileSystem.WriteAllText($"{BaseDir}/f.txt", "original");
        fileSystem.WriteAllText($"{OursDir}/f.txt", "our-edit");
        fileSystem.WriteAllText($"{TheirsDir}/f.txt", "their-edit");
        patchTool.DefaultConflictCount = 1;

        var result = await merger.MergeAsync(BaseDir, OursDir, TheirsDir, DestDir);

        Assert.True(result.HasConflicts);
        Assert.Equal(["f.txt"], result.ConflictedPaths);
    }

    [Fact]
    public async Task MergeAsync_BothChangedDifferentlyButMergeToolResolvesClean_NoConflict()
    {
        var (merger, fileSystem, patchTool) = Build();
        fileSystem.WriteAllText($"{BaseDir}/f.txt", "line1\nline2\nline3\n");
        fileSystem.WriteAllText($"{OursDir}/f.txt", "line1-ours\nline2\nline3\n");
        fileSystem.WriteAllText($"{TheirsDir}/f.txt", "line1\nline2\nline3-theirs\n");
        // FakePatchTool's default (conflict count 0) simulates a clean non-overlapping merge.

        var result = await merger.MergeAsync(BaseDir, OursDir, TheirsDir, DestDir);

        Assert.False(result.HasConflicts);
    }
}
