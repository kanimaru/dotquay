using Dotquay.Core.Sync;
using Dotquay.Tests.Support;
using Xunit;

namespace Dotquay.Tests.Sync;

/// <summary>
/// Covers the rule every project-wide scan uses to decide whether Godot would see a path at all.
/// </summary>
public class GodotVisibilityTests {
    private const string Root = "project";

    [Theory]
    [InlineData("game/boot.gd", true)]
    [InlineData(".dotquay/staging/twitcher/plugin.gd", false)]
    [InlineData("tools/.cache/boot.gd", false)]
    [InlineData(".hidden.gd", false)]
    [InlineData("my.godot-stuff/boot.gd", true)]
    [InlineData("game\\.cache\\boot.gd", false)]
    public void IsVisible_DotSegments_AreHidden(string relative, bool visible) =>
        Assert.Equal(visible, new GodotVisibility(new InMemoryFileSystem(), Root)
            .IsVisible(relative));

    [Fact]
    public void IsVisible_BelowAGdignore_IsHidden() {
        var fileSystem = new InMemoryFileSystem();
        fileSystem.WriteAllText($"{Root}/vendor/.gdignore", "");
        var visibility = new GodotVisibility(fileSystem, Root);

        Assert.False(visibility.IsVisible("vendor/lib/boot.gd"));
        Assert.False(visibility.IsVisible("vendor/boot.gd"));
        Assert.True(visibility.IsVisible("game/boot.gd"));
    }

    [Fact]
    public void IsVisible_TheGdignoredFolderItself_IsHidden() {
        // Asked of a directory rather than a file: the folder that holds the .gdignore is hidden
        // too.
        var fileSystem = new InMemoryFileSystem();
        fileSystem.WriteAllText($"{Root}/addons/parked/.gdignore", "");

        Assert.False(new GodotVisibility(fileSystem, Root)
            .IsVisible("addons/parked", isDirectory: true));
    }
}
