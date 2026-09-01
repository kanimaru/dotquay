using Dotquay.Core.Doctor;
using Dotquay.Tests.Support;
using Xunit;

namespace Dotquay.Tests.Doctor;

/// <summary>
/// Covers the scan behind <c>doctor</c>'s <c>fragile_uid_reference</c> issue: project code that loads
/// an addon's resource by a bare <c>uid://</c>.
/// </summary>
/// <remarks>
/// The value of this check is entirely in its precision. It exists to say "these few lines would break
/// if an addon's uids changed" — so a scan that also reported the project's references to its own
/// files, or matched one uid inside another, would bury the answer in noise and get ignored.
/// </remarks>
public class UidReferenceScannerTests
{
    private const string Project = "project";
    private const string Addons = "project/addons";

    private static InMemoryFileSystem WithAddonScript(string uid = "uid://bxwpwbdv4fm02")
    {
        var fileSystem = new InMemoryFileSystem();
        fileSystem.WriteAllText($"{Addons}/r3/frame.cs", "class Frame {}");
        fileSystem.WriteAllText($"{Addons}/r3/frame.cs.uid", uid);
        return fileSystem;
    }

    [Fact]
    public void Scan_CodeLoadingAnAddonResourceByUid_IsReported()
    {
        var fileSystem = WithAddonScript();
        fileSystem.WriteAllText($"{Project}/game/boot.gd", "var f = load(\"uid://bxwpwbdv4fm02\")");

        var finding = Assert.Single(UidReferenceScanner.Scan(fileSystem, Project, Addons));

        Assert.Equal("game/boot.gd", finding.File);
        Assert.Equal("uid://bxwpwbdv4fm02", finding.Uid);

        // The addon-relative target, so the message can name what would break.
        Assert.Equal("r3/frame.cs", finding.Target);
    }

    [Fact]
    public void Scan_UidDeclaredByAnImportSidecar_IsAlsoResolved()
    {
        // Assets record their uid inside the .import file rather than in a .uid sidecar.
        var fileSystem = new InMemoryFileSystem();
        fileSystem.WriteAllText($"{Addons}/gut/icon.png", "binary");
        fileSystem.WriteAllText($"{Addons}/gut/icon.png.import", "[remap]\nimporter=\"texture\"\nuid=\"uid://cabc123\"");
        fileSystem.WriteAllText($"{Project}/ui/panel.gd", "var icon = load(\"uid://cabc123\")");

        Assert.Equal("gut/icon.png", Assert.Single(UidReferenceScanner.Scan(fileSystem, Project, Addons)).Target);
    }

    [Fact]
    public void Scan_SceneFiles_AreNotReported()
    {
        // A .tscn records a path beside every uid, so Godot falls back to it and repairs the uid on
        // the next save. Reporting these would flag every scene in every project.
        var fileSystem = WithAddonScript();
        fileSystem.WriteAllText(
            $"{Project}/game/main.tscn",
            "[ext_resource type=\"Script\" uid=\"uid://bxwpwbdv4fm02\" path=\"res://addons/r3/frame.cs\" id=\"1\"]");

        Assert.Empty(UidReferenceScanner.Scan(fileSystem, Project, Addons));
    }

    [Fact]
    public void Scan_ReferenceToTheProjectsOwnResource_IsNotReported()
    {
        // dotquay never touches the project's own files, so their uids are stable by construction.
        var fileSystem = WithAddonScript();
        fileSystem.WriteAllText($"{Project}/game/player.gd", "class_name Player");
        fileSystem.WriteAllText($"{Project}/game/player.gd.uid", "uid://mine1234");
        fileSystem.WriteAllText($"{Project}/game/boot.gd", "var p = load(\"uid://mine1234\")");

        Assert.Empty(UidReferenceScanner.Scan(fileSystem, Project, Addons));
    }

    [Fact]
    public void Scan_CodeInsideAnAddon_IsNotReported()
    {
        // An addon referencing its own resources moves in lockstep with the uids it names, and it is
        // the author's file, not the user's, so there is nothing here for them to act on.
        var fileSystem = WithAddonScript();
        fileSystem.WriteAllText($"{Addons}/r3/plugin.cs", "load(\"uid://bxwpwbdv4fm02\")");

        Assert.Empty(UidReferenceScanner.Scan(fileSystem, Project, Addons));
    }

    [Fact]
    public void Scan_AUidThatIsAPrefixOfAnother_IsNotConfused()
    {
        // uid://abc is a prefix of uid://abc1. Substring matching would report a reference to a
        // resource the code never mentions — a false alarm on the check whose only value is precision.
        var fileSystem = new InMemoryFileSystem();
        fileSystem.WriteAllText($"{Addons}/r3/frame.cs", "class Frame {}");
        fileSystem.WriteAllText($"{Addons}/r3/frame.cs.uid", "uid://abc");
        fileSystem.WriteAllText($"{Project}/game/boot.gd", "var f = load(\"uid://abc1\")");

        Assert.Empty(UidReferenceScanner.Scan(fileSystem, Project, Addons));
    }

    [Fact]
    public void Scan_GodotCacheFolder_IsSkipped()
    {
        // .godot holds uids in bulk and is regenerated wholesale; scanning it would report thousands
        // of "references" nobody wrote.
        var fileSystem = WithAddonScript();
        fileSystem.WriteAllText($"{Project}/.godot/generated.gd", "uid://bxwpwbdv4fm02");

        Assert.Empty(UidReferenceScanner.Scan(fileSystem, Project, Addons));
    }

    [Fact]
    public void Scan_NoAddonsDirectory_ReturnsNothing() =>
        Assert.Empty(UidReferenceScanner.Scan(new InMemoryFileSystem(), Project, Addons));

    [Fact]
    public void Scan_FindsEveryReferenceInOneFile()
    {
        var fileSystem = WithAddonScript();
        fileSystem.WriteAllText($"{Addons}/r3/other.cs", "class Other {}");
        fileSystem.WriteAllText($"{Addons}/r3/other.cs.uid", "uid://second999");
        fileSystem.WriteAllText(
            $"{Project}/game/boot.gd",
            "var a = load(\"uid://bxwpwbdv4fm02\")\nvar b = load(\"uid://second999\")");

        Assert.Equal(2, UidReferenceScanner.Scan(fileSystem, Project, Addons).Count);
    }
}
