using Dotquay.Core.Manifest;
using Dotquay.Core.Workflows;
using Dotquay.Tests.Support;
using Xunit;

namespace Dotquay.Tests.Workflows;

/// <summary>
/// Covers where patches live (SPEC.md §10) and the layout rules around them.
/// </summary>
public class PatchLocationTests
{
    private static ProjectManifest Manifest(string? patchesDir) =>
        new() { PatchesDirectory = patchesDir };

    [Fact]
    public void For_NoSettingAndNoLegacyFolder_UsesTheHiddenDefault()
    {
        var layout = ProjectLayout.For(new InMemoryFileSystem(), "project", Manifest(null));

        Assert.Equal(ProjectLayout.DefaultPatchesDirectory, layout.PatchesDirectory);
        Assert.True(layout.PatchesAreInsideDotquay);
    }

    [Fact]
    public void For_ExistingLegacyFolder_KeepsUsingIt()
    {
        var fileSystem = new InMemoryFileSystem();
        fileSystem.WriteAllText("project/patches/thing.patch", "diff");

        var layout = ProjectLayout.For(fileSystem, "project", Manifest(null));

        // Not tidiness — patches are re-applied on every install and update (SPEC.md §10 step 3).
        // If changing the default made dotquay stop looking where a project's patches actually are,
        // those projects would silently start installing unpatched code and status would call it fine.
        Assert.Equal(ProjectLayout.LegacyPatchesDirectory, layout.PatchesDirectory);
    }

    [Fact]
    public void For_ExplicitSetting_WinsOverBoth()
    {
        var fileSystem = new InMemoryFileSystem();
        fileSystem.WriteAllText("project/patches/thing.patch", "diff");

        var layout = ProjectLayout.For(fileSystem, "project", Manifest("vendor/patches"));

        Assert.Equal("vendor/patches", layout.PatchesDirectory);
        Assert.False(layout.PatchesAreInsideDotquay);
    }

    [Fact]
    public void PatchesPath_NormalisesSeparators()
    {
        var fileSystem = new InMemoryFileSystem();
        var layout = new ProjectLayout("project", ".dotquay/patches");

        // A configured value is written with forward slashes in TOML; combining it whole would give a
        // mixed-separator path in every message the user reads.
        Assert.Equal("project/.dotquay/patches", layout.PatchesPath(fileSystem));
        Assert.Equal("project/.dotquay/patches/leaf.patch", layout.PatchFilePath(fileSystem, "leaf"));
    }

    [Fact]
    public void Manifest_RoundTripsPatchesDir()
    {
        var original = ManifestReader.ReadProject("""
            [project]
            patches_dir = "vendor/patches"
            """);

        var reparsed = ManifestReader.ReadProject(ManifestWriter.Serialize(original));

        Assert.Equal("vendor/patches", reparsed.PatchesDirectory);
    }

    [Fact]
    public void For_NoManifestAtAll_StillWorks()
    {
        // `patch` runs off the lockfile and disk; refusing to name a directory because dotquay.toml
        // is missing would be a rule with no purpose.
        var layout = ProjectLayout.For(new InMemoryFileSystem(), "project", manifest: null);

        Assert.Equal(ProjectLayout.DefaultPatchesDirectory, layout.PatchesDirectory);
    }
}
