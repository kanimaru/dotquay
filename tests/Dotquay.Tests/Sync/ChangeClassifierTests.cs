using System.Text;
using Dotquay.Core.Sync;
using Dotquay.Tests.Support;
using Xunit;

namespace Dotquay.Tests.Sync;

/// <summary>
/// Covers the classification behind <c>dotquay status --explain</c>
/// (docs/DESIGN-v0.12-MODIFIED-REASON.md): deciding whether one differing file is a real edit or
/// churn a reinstall would regenerate anyway.
/// </summary>
/// <remarks>
/// The asymmetry these tests exist to protect: calling a real edit benign is what loses someone's
/// work, so every uncertain case must land on <see cref="ChangeClass.Content"/>. The reverse
/// mistake only costs the reader a look.
/// </remarks>
public class ChangeClassifierTests {
    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    [Fact]
    public void Classify_OnlyTheUidValueChanged_IsUidOnly() {
        var expected =
            Bytes(
                """[ext_resource type="Script" uid="uid://bxwpwbdv4fm02" """
                    + """path="res://addons/x/x.gd"]""");
        var actual =
            Bytes(
                """[ext_resource type="Script" uid="uid://c7k2p9qq1a0zz" """
                    + """path="res://addons/x/x.gd"]""");

        Assert.Equal(ChangeClass.UidOnly, ChangeClassifier.Classify("main.tscn", expected, actual));
    }

    /// <summary>
    /// The masking hazard, stated as a test: <c>uid://abc</c> is a string-prefix of
    /// <c>uid://abc1</c>. A mask that stopped at the shorter match would swallow the rest of the
    /// line and call a real path change benign — the same prefix trap that sank the project-wide
    /// UID rewrite proposal.
    /// </summary>
    [Fact]
    public void Classify_UidAndSomethingElseChanged_IsContent() {
        var expected = Bytes("""[ext_resource uid="uid://abc" path="res://addons/x/old.gd"]""");
        var actual = Bytes("""[ext_resource uid="uid://abc1" path="res://addons/x/new.gd"]""");

        Assert.Equal(ChangeClass.Content, ChangeClassifier.Classify("main.tscn", expected, actual));
    }

    [Fact]
    public void Classify_OnlyLineEndingsChanged_IsLineEndingsOnly() {
        var expected = Bytes("extends Node\nfunc _ready():\n\tpass\n");
        var actual = Bytes("extends Node\r\nfunc _ready():\r\n\tpass\r\n");

        Assert.Equal(ChangeClass.LineEndingsOnly,
            ChangeClassifier.Classify("script.gd", expected, actual));
    }

    [Fact]
    public void Classify_LineEndingsAndUidBothChanged_IsStillUidOnly() =>
        Assert.Equal(
            ChangeClass.UidOnly,
            ChangeClassifier.Classify("main.tscn", Bytes("a\nuid://aaa\nb\n"),
                Bytes("a\r\nuid://bbb\r\nb\r\n")));

    /// <summary>The field report: an editor trimmed the tabs off blank lines, nothing
    /// else.</summary>
    [Fact]
    public void Classify_OnlyTrailingWhitespaceChanged_IsWhitespaceOnly() =>
        Assert.Equal(
            ChangeClass.WhitespaceOnly,
            ChangeClassifier.Classify(
                "ext.gd",
                Bytes("func a():\n\treturn OK\n\t\n\treturn 1 \n"),
                Bytes("func a():\n\treturn OK\n\n\treturn 1\n")));

    [Fact]
    public void Classify_BlankLinesAddedAtEndOfFile_IsWhitespaceOnly() =>
        Assert.Equal(
            ChangeClass.WhitespaceOnly,
            ChangeClassifier.Classify("ext.gd", Bytes("a\nb"), Bytes("a\nb\n\n")));

    [Fact]
    public void Classify_WhitespaceUidAndLineEndingsTogether_IsWhitespaceOnly() =>
        Assert.Equal(
            ChangeClass.WhitespaceOnly,
            ChangeClassifier.Classify("main.tscn", Bytes("a \nuid://aaa\nb\n"),
                Bytes("a\r\nuid://bbb\r\nb\r\n")));

    /// <summary>GDScript is indentation-sensitive: moving a line into or out of a block is a real
    /// edit.</summary>
    [Fact]
    public void Classify_LeadingWhitespaceChanged_IsContent() =>
        Assert.Equal(
            ChangeClass.Content,
            ChangeClassifier.Classify("ext.gd", Bytes("if x:\n\tfoo()\n"),
                Bytes("if x:\nfoo()\n")));

    [Fact]
    public void Classify_WhitespaceInsideALineChanged_IsContent() =>
        Assert.Equal(
            ChangeClass.Content,
            ChangeClassifier.Classify("ext.gd", Bytes("var a = [1, 2]\n"),
                Bytes("var a = [1,  2]\n")));

    [Fact]
    public void Classify_RealEdit_IsContent() =>
        Assert.Equal(
            ChangeClass.Content,
            ChangeClassifier.Classify("script.gd", Bytes("func _ready():\n\tpass\n"),
                Bytes("func _ready():\n\tqueue_free()\n")));

    /// <summary>A binary asset has no benign-rewrite story; normalisation must not even be
    /// attempted over bytes that are not text.</summary>
    [Fact]
    public void Classify_InvalidUtf8_IsContent() =>
        Assert.Equal(
            ChangeClass.Content,
            ChangeClassifier.Classify("icon.png", [0x89, 0x50, 0xFF, 0xFE],
                [0x89, 0x50, 0xFF, 0xFD]));

    /// <summary>Decodes cleanly and is still not text. A NUL byte is the tell.</summary>
    [Fact]
    public void Classify_NulBytes_IsContent() =>
        Assert.Equal(
            ChangeClass.Content,
            ChangeClassifier.Classify("data.bin", Bytes("a\0b"), Bytes("a\0c")));

    [Fact]
    public void Classify_ArtifactPath_IsEditorArtifactWhateverTheContent() =>
        Assert.Equal(
            ChangeClass.EditorArtifact,
            ChangeClassifier.Classify("script.gd.uid", Bytes("uid://aaa"),
                Bytes("totally different")));

    /// <summary>
    /// A file that exists only on disk is exactly what a force-update deletes without trace, so
    /// "added" is never benign unless the path itself is editor output.
    /// </summary>
    [Fact]
    public void ClassifyByPath_SeparatesAddedArtifactsFromAddedContent() {
        Assert.Equal(ChangeClass.Content, ChangeClassifier.ClassifyByPath("my-notes.gd"));
        Assert.Equal(ChangeClass.EditorArtifact,
            ChangeClassifier.ClassifyByPath("my-notes.gd.uid"));
    }

    // -- TreeComparer -----------------------------------------------------------------------

    [Fact]
    public void Compare_ReportsAddedRemovedAndChanged() {
        var fileSystem = new InMemoryFileSystem();
        fileSystem.WriteAllText("expected/kept.gd", "same");
        fileSystem.WriteAllText("expected/edited.gd", "before");
        fileSystem.WriteAllText("expected/gone.gd", "deleted later");
        fileSystem.WriteAllText("actual/kept.gd", "same");
        fileSystem.WriteAllText("actual/edited.gd", "after");
        fileSystem.WriteAllText("actual/extra.gd", "new");

        var changes = TreeComparer.Compare(fileSystem, "expected", "actual");

        Assert.DoesNotContain(changes, change => change.Path == "kept.gd");
        Assert.Equal(ChangeKind.Changed, Assert.Single(changes, c => c.Path == "edited.gd").Kind);
        Assert.Equal(ChangeKind.Removed, Assert.Single(changes, c => c.Path == "gone.gd").Kind);
        Assert.Equal(ChangeKind.Added, Assert.Single(changes, c => c.Path == "extra.gd").Kind);
    }

    /// <summary>
    /// The reported list is capped, so ordering decides which changes survive truncation. A list
    /// that dropped the one real edit in favour of forty artifact files would be worse than no list
    /// at all.
    /// </summary>
    [Fact]
    public void Compare_PutsRealContentChangesFirst() {
        var fileSystem = new InMemoryFileSystem();
        fileSystem.WriteAllText("expected/a.gd.uid", "uid://aaa");
        fileSystem.WriteAllText("expected/z.gd", "before");
        fileSystem.WriteAllText("actual/a.gd.uid", "uid://bbb");
        fileSystem.WriteAllText("actual/z.gd", "after");

        var changes = TreeComparer.Compare(fileSystem, "expected", "actual");

        Assert.Equal("z.gd", changes[0].Path);
        Assert.Equal(ChangeClass.Content, changes[0].Class);
        Assert.Equal(ChangeClass.EditorArtifact, changes[1].Class);
    }

    [Fact]
    public void Compare_IdenticalTrees_ReportNothing() {
        var fileSystem = new InMemoryFileSystem();
        fileSystem.WriteAllText("expected/a.gd", "same");
        fileSystem.WriteAllText("actual/a.gd", "same");

        Assert.Empty(TreeComparer.Compare(fileSystem, "expected", "actual"));
    }
}
