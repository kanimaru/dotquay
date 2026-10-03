using System.Text;
using Dotquay.Core.Sync;
using Dotquay.Tests.Support;
using Xunit;

namespace Dotquay.Tests.Sync;

/// <summary>
/// The recorded content hash must not depend on which machine installed the addon.
/// </summary>
/// <remarks>
/// git rewrites line endings per machine, so one commit is CRLF on a Windows box and LF on Linux or macOS. The
/// hash used to be taken over the files as git wrote them, so the same addon at the same commit had a different
/// <c>content_hash</c> for each person who installed it, and every install on a machine that disagreed with the
/// last one rewrote the lockfile. See <see cref="ContentHasher"/>.
/// </remarks>
public class LineEndingHashTests
{
    private const string Script = "extends Node\nfunc _ready():\n\tprint(\"hi\")\n";
    private static byte[] Binary => [0x7f, 0x45, 0x4c, 0x46, 0x00, 0x0d, 0x0a, 0x00, 0x01, 0x0d, 0x0a];

    private static string Crlf(string s) => s.Replace("\n", "\r\n");

    /// <summary>The same addon as two machines would have written it.</summary>
    private static (InMemoryFileSystem Unix, InMemoryFileSystem Windows) TwoMachines()
    {
        var unix = new InMemoryFileSystem();
        unix.WriteAllText("a/plugin.cfg", "[plugin]\nname=\"x\"\n");
        unix.WriteAllText("a/x.gd", Script);
        unix.WriteAllBytes("a/lib.dll", Binary);

        var windows = new InMemoryFileSystem();
        windows.WriteAllText("a/plugin.cfg", Crlf("[plugin]\nname=\"x\"\n"));
        windows.WriteAllText("a/x.gd", Crlf(Script));
        windows.WriteAllBytes("a/lib.dll", Binary); // binary: git never converts it
        return (unix, windows);
    }

    // ---- the number --------------------------------------------------------------------------

    [Fact]
    public void TheSameCommit_HashesTheSame_WhateverTheMachinesLineEndings()
    {
        var (unix, windows) = TwoMachines();

        Assert.Equal(ContentHasher.HashDirectory(unix, "a"), ContentHasher.HashDirectory(windows, "a"));
    }

    [Fact]
    public void ARealEdit_StillChangesTheHash()
    {
        var (unix, windows) = TwoMachines();
        windows.WriteAllText("a/x.gd", Crlf(Script.Replace("hi", "bye")));

        Assert.NotEqual(ContentHasher.HashDirectory(unix, "a"), ContentHasher.HashDirectory(windows, "a"));
    }

    [Fact]
    public void ABinaryFile_IsNeverNormalised()
    {
        // Same bytes except a CRLF inside the binary. Normalising it would hide a real change.
        var (unix, _) = TwoMachines();
        var changed = new InMemoryFileSystem();
        changed.WriteAllText("a/plugin.cfg", "[plugin]\nname=\"x\"\n");
        changed.WriteAllText("a/x.gd", Script);
        var edited = Binary.ToArray();
        edited[9] = 0x20; // 0x0d -> space, inside a file that has NUL bytes
        changed.WriteAllBytes("a/lib.dll", edited);

        Assert.NotEqual(ContentHasher.HashDirectory(unix, "a"), ContentHasher.HashDirectory(changed, "a"));
    }

    [Fact]
    public void ALoneCarriageReturn_IsContent_NotALineEnding()
    {
        var a = new InMemoryFileSystem();
        a.WriteAllText("a/x.txt", "one\rtwo\n");
        var b = new InMemoryFileSystem();
        b.WriteAllText("a/x.txt", "one\ntwo\n");

        Assert.NotEqual(ContentHasher.HashDirectory(a, "a"), ContentHasher.HashDirectory(b, "a"));
    }

    [Fact]
    public void AFileWithMixedEndings_HashesLikeItsAllLfForm()
    {
        var mixed = new InMemoryFileSystem();
        mixed.WriteAllText("a/x.gd", "one\r\ntwo\nthree\r\n");
        var lf = new InMemoryFileSystem();
        lf.WriteAllText("a/x.gd", "one\ntwo\nthree\n");

        Assert.Equal(ContentHasher.HashDirectory(lf, "a"), ContentHasher.HashDirectory(mixed, "a"));
    }

    [Fact]
    public void TheCanonicalHash_OnALineFeedMachine_IsTheHashEveryOlderDotquayRecordedThere()
    {
        // The reason nothing needs migrating on Linux and macOS: their old hashes ARE the canonical ones.
        var (unix, _) = TwoMachines();
        var tree = ContentHasher.HashTree(unix, "a");

        Assert.Equal(tree.Canonical, ContentHasher.HashDirectoryIncludingArtifacts(unix, "a")); // no editor files here
    }

    // ---- the lockfile's old values still count as unedited ------------------------------------

    [Fact]
    public void ALockfileWrittenOnWindows_IsCleanOnALinuxCheckout_AndTheOtherWayRound()
    {
        var (unix, windows) = TwoMachines();
        // What the OLD dotquay recorded on each: the files exactly as that machine's git wrote them.
        var recordedOnWindows = ContentHasher.HashDirectoryIncludingArtifacts(windows, "a");
        var recordedOnUnix = ContentHasher.HashDirectoryIncludingArtifacts(unix, "a");
        Assert.NotEqual(recordedOnWindows, recordedOnUnix); // the bug, stated

        Assert.False(DirtyChecker.Check(unix, "a", recordedOnWindows).IsDirty, "a Windows lockfile on a Linux checkout");
        Assert.False(DirtyChecker.Check(windows, "a", recordedOnUnix).IsDirty, "a Linux lockfile on a Windows checkout");
        Assert.False(DirtyChecker.Check(windows, "a", recordedOnWindows).IsDirty, "and the plain case still works");
        Assert.False(DirtyChecker.Check(unix, "a", recordedOnUnix).IsDirty, "and the plain case still works");
    }

    [Fact]
    public void TheNewCanonicalValue_IsCleanOnBothMachines()
    {
        var (unix, windows) = TwoMachines();
        var canonical = ContentHasher.HashDirectory(unix, "a");

        Assert.False(DirtyChecker.Check(unix, "a", canonical).IsDirty);
        Assert.False(DirtyChecker.Check(windows, "a", canonical).IsDirty);
    }

    [Fact]
    public void ARealEdit_IsStillDirty_AgainstEveryKindOfRecordedHash()
    {
        var (unix, windows) = TwoMachines();
        var kinds = new[]
        {
            ContentHasher.HashDirectory(unix, "a"),
            ContentHasher.HashDirectoryIncludingArtifacts(unix, "a"),
            ContentHasher.HashDirectoryIncludingArtifacts(windows, "a"),
        };
        windows.WriteAllText("a/x.gd", Crlf(Script.Replace("hi", "tampered")));

        foreach (var recorded in kinds)
        {
            Assert.True(DirtyChecker.Check(windows, "a", recorded).IsDirty, $"edit must show against {recorded[..16]}");
        }
    }

    [Fact]
    public void ALegacyHashThatIncludedEditorFiles_StillCountsAsClean()
    {
        // The v0.9 compatibility the new hashes must not lose.
        var fs = new InMemoryFileSystem();
        fs.WriteAllText("a/x.gd", Script);
        fs.WriteAllText("a/x.gd.uid", "uid://bxwpwbdv4fm02");
        var legacy = ContentHasher.HashDirectoryIncludingArtifacts(fs, "a");
        Assert.NotEqual(legacy, ContentHasher.HashDirectory(fs, "a"));

        Assert.False(DirtyChecker.Check(fs, "a", legacy).IsDirty);
    }

    [Fact]
    public void AnEditorFileThatGodotRewrote_StillDoesNotCount()
    {
        var (unix, windows) = TwoMachines();
        var recorded = ContentHasher.HashDirectory(unix, "a");
        windows.WriteAllText("a/x.gd.uid", "uid://something-godot-made");

        Assert.False(DirtyChecker.Check(windows, "a", recorded).IsDirty);
    }

    // ---- the install does not churn the lockfile ----------------------------------------------

    [Fact]
    public void AReinstallThatChangesNothing_KeepsTheRecordedHash_WhateverFormItIsIn()
    {
        var (unix, windows) = TwoMachines();
        var recordedOnWindows = ContentHasher.HashDirectoryIncludingArtifacts(windows, "a");
        Assert.NotEqual(recordedOnWindows, ContentHasher.HashDirectory(unix, "a"));

        // A Linux machine installs the same commit over its own copy, from a lockfile written on Windows.
        unix.WriteAllText("staging/plugin.cfg", "[plugin]\nname=\"x\"\n");
        unix.WriteAllText("staging/x.gd", Script);
        unix.WriteAllBytes("staging/lib.dll", Binary);

        var recorded = new SyncEngine(unix).SwapAndHash("staging", "a", recordedOnWindows, force: false);

        Assert.Equal(recordedOnWindows, recorded); // the lockfile line does not change, so it cannot flip back
    }

    [Fact]
    public void AnInstallThatChangesTheContent_RecordsTheNewCanonicalHash()
    {
        var (unix, _) = TwoMachines();
        var before = ContentHasher.HashDirectory(unix, "a");
        unix.WriteAllText("staging/plugin.cfg", "[plugin]\nname=\"x\"\nversion=\"2\"\n");
        unix.WriteAllText("staging/x.gd", Script);
        unix.WriteAllBytes("staging/lib.dll", Binary);

        var recorded = new SyncEngine(unix).SwapAndHash("staging", "a", before, force: false);

        Assert.NotEqual(before, recorded);
        Assert.Equal(ContentHasher.HashDirectory(unix, "a"), recorded);
    }

    [Fact]
    public void AFreshInstall_RecordsTheCanonicalHash_SoItIsTheSameOnEveryMachine()
    {
        var (unix, windows) = TwoMachines();
        windows.DeleteDirectory("a");
        windows.WriteAllText("staging/plugin.cfg", Crlf("[plugin]\nname=\"x\"\n"));
        windows.WriteAllText("staging/x.gd", Crlf(Script));
        windows.WriteAllBytes("staging/lib.dll", Binary);

        var onWindows = new SyncEngine(windows).SwapAndHash("staging", "a", expectedContentHash: null, force: false);

        Assert.Equal(ContentHasher.HashDirectory(unix, "a"), onWindows);
    }

    [Fact]
    public void Hashing_AnEmptyOrTextOnlyOrBinaryOnlyTree_DoesNotThrow()
    {
        var empty = new InMemoryFileSystem();
        empty.CreateDirectory("a");
        Assert.StartsWith("sha256:", ContentHasher.HashDirectory(empty, "a"));

        var only = new InMemoryFileSystem();
        only.WriteAllBytes("a/b.bin", Encoding.UTF8.GetBytes("\0\0\0"));
        Assert.StartsWith("sha256:", ContentHasher.HashDirectory(only, "a"));
    }
}
