using Dotquay.Core.Json;
using Dotquay.Core.Workflows;
using Dotquay.Tests.Support;
using Xunit;

namespace Dotquay.Tests.Workflows;

/// <summary>
/// Covers <c>dotquay doctor</c>'s wiring of <see cref="UidReferenceFix"/> onto a
/// <see cref="DoctorIssueKind.FragileUidReference"/> issue.
/// </summary>
/// <remarks>
/// <see cref="Doctor.UidReferenceScanner"/> itself is covered exhaustively in
/// <c>UidReferenceScannerTests</c>; this only checks that <see cref="DependencyWorkflow.DoctorAsync"/>
/// carries the scanner's three facts through onto the issue instead of discarding them once the
/// message is built.
/// </remarks>
public class DoctorFragileUidReferenceTests {
    private const string ProjectDirectory = "project";

    private static async Task<IReadOnlyList<DoctorIssue>> RunAsync(InMemoryFileSystem fileSystem) {
        var workflow = new DependencyWorkflow(fileSystem, new FakeGitClient(), cacheRoot: "cache");
        var result = await workflow.DoctorAsync(ProjectDirectory);

        return result.Issues.Where(i => i.Kind == DoctorIssueKind.FragileUidReference).ToList();
    }

    [Fact]
    public async Task DoctorAsync_ABareUidReferenceIntoAnAddon_CarriesTheFixData() {
        var fileSystem = new InMemoryFileSystem();
        fileSystem.WriteAllText($"{ProjectDirectory}/addons/r3/frame.cs", "class Frame {}");
        fileSystem.WriteAllText($"{ProjectDirectory}/addons/r3/frame.cs.uid", "uid://bxwpwbdv4fm02");
        fileSystem.WriteAllText($"{ProjectDirectory}/game/boot.gd", "var f = load(\"uid://bxwpwbdv4fm02\")");

        var issue = Assert.Single(await RunAsync(fileSystem));

        Assert.NotNull(issue.UidFix);
        Assert.Equal("game/boot.gd", issue.UidFix!.File);
        Assert.Equal("uid://bxwpwbdv4fm02", issue.UidFix.Uid);

        // The res:// path a fix would swap the bare uid for -- addons/ plus the scanner's
        // addon-relative target, resolved once here rather than by every consumer re-deriving it.
        Assert.Equal("res://addons/r3/frame.cs", issue.UidFix.ResPath);
    }

    [Fact]
    public async Task DoctorAsync_ABareUidReference_SurfacesTheFixDataOnTheWire() {
        var fileSystem = new InMemoryFileSystem();
        fileSystem.WriteAllText($"{ProjectDirectory}/addons/r3/frame.cs", "class Frame {}");
        fileSystem.WriteAllText($"{ProjectDirectory}/addons/r3/frame.cs.uid", "uid://bxwpwbdv4fm02");
        fileSystem.WriteAllText($"{ProjectDirectory}/game/boot.gd", "var f = load(\"uid://bxwpwbdv4fm02\")");

        var workflow = new DependencyWorkflow(fileSystem, new FakeGitClient(), cacheRoot: "cache");
        var result = await workflow.DoctorAsync(ProjectDirectory);

        var json = Assert.Single(JsonMapper.ToJson(result).Issues, i => i.Kind == "fragile_uid_reference");

        // The plugin's whole reason for wanting these: enough to say "this file, this uid, this path"
        // without re-deriving any of it from the sentence in Message.
        Assert.Equal("game/boot.gd", json.File);
        Assert.Equal("uid://bxwpwbdv4fm02", json.Uid);
        Assert.Equal("res://addons/r3/frame.cs", json.ResPath);
    }

    [Fact]
    public async Task DoctorAsync_EveryOtherIssueKind_SurfacesNoFixDataOnTheWire() {
        var fileSystem = new InMemoryFileSystem();
        fileSystem.WriteAllText($"{ProjectDirectory}/addons/orphan/plugin.cfg", "[plugin]");

        var workflow = new DependencyWorkflow(fileSystem, new FakeGitClient(), cacheRoot: "cache");
        var result = await workflow.DoctorAsync(ProjectDirectory);

        var json = Assert.Single(JsonMapper.ToJson(result).Issues, i => i.Kind == "orphaned_addon_folder");

        Assert.Null(json.File);
        Assert.Null(json.Uid);
        Assert.Null(json.ResPath);
    }

    [Fact]
    public async Task DoctorAsync_EveryOtherIssueKind_CarriesNoUidFix() {
        var fileSystem = new InMemoryFileSystem();
        fileSystem.WriteAllText($"{ProjectDirectory}/addons/orphan/plugin.cfg", "[plugin]");

        var workflow = new DependencyWorkflow(fileSystem, new FakeGitClient(), cacheRoot: "cache");
        var result = await workflow.DoctorAsync(ProjectDirectory);

        var issue = Assert.Single(result.Issues, i => i.Kind == DoctorIssueKind.OrphanedAddonFolder);
        Assert.Null(issue.UidFix);
    }
}