using Dotquay.Core.Local;
using Dotquay.Core.Sync;
using Dotquay.Tests.Support;
using Xunit;

namespace Dotquay.Tests.Sync;

/// <summary>
/// Covers the v0.16 rule that decides whether <c>addons/&lt;folder&gt;</c> is a folder dotquay may
/// write (docs/DESIGN-v0.16-LINK-SAFETY.md).
/// </summary>
/// <remarks>
/// These assertions are cheap and the failure they prevent is not: content written through a
/// junction lands in somebody's source repository, under a path the consuming project's git never
/// watches, and looks to its owner like edits they made themselves. The suite that ran on the day
/// that happened was 900 tests green — none of which asked what the destination actually was.
/// </remarks>
public class AddonLinkGuardTests {
    private const string Destination = "project/addons/my-addon";
    private const string Checkout = "elsewhere/my-addon";

    private static InMemoryFileSystem WithCheckout() {
        var fileSystem = new InMemoryFileSystem();
        fileSystem.WriteAllText(fileSystem.CombinePath(Checkout, "thing.gd"), "# live edit");
        return fileSystem;
    }

    [Fact]
    public void Inspect_APlainFolder_IsOursToWrite() {
        var fileSystem = WithCheckout();
        fileSystem.CreateDirectory(Destination);

        var verdict = AddonLinkGuard.Inspect(fileSystem, Destination, declared: null);

        Assert.Equal(AddonLinkState.NotLinked, verdict.State);
        Assert.False(verdict.ForbidsWriting);
    }

    [Fact]
    public void Inspect_ADeclarationWithNoLinkOnDisk_IsStillOursToWrite() {
        var fileSystem = WithCheckout();
        fileSystem.CreateDirectory(Destination);

        // A link that was declared but never installed is not a disagreement — installing is
        // exactly what turns the declaration into a link, and refusing here would deadlock that.
        var verdict = AddonLinkGuard.Inspect(fileSystem, Destination,
            new LocalLink("my-addon", Checkout));

        Assert.Equal(AddonLinkState.NotLinked, verdict.State);
        Assert.False(verdict.ForbidsWriting);
    }

    [Fact]
    public void Inspect_ALinkMatchingTheDeclaration_IsLinked() {
        var fileSystem = WithCheckout();
        fileSystem.CreateDirectoryLink(Destination, Checkout);

        var verdict = AddonLinkGuard.Inspect(fileSystem, Destination,
            new LocalLink("my-addon", Checkout));

        Assert.Equal(AddonLinkState.LinkedAsDeclared, verdict.State);
        Assert.False(verdict.ForbidsWriting);
    }

    [Fact]
    public void Inspect_ALinkPointingSomewhereElse_ForbidsWriting() {
        var fileSystem = WithCheckout();
        fileSystem.WriteAllText("somewhere-else/thing.gd", "# a different checkout");
        fileSystem.CreateDirectoryLink(Destination, "somewhere-else");

        var verdict = AddonLinkGuard.Inspect(fileSystem, Destination,
            new LocalLink("my-addon", Checkout));

        Assert.Equal(AddonLinkState.TargetMismatch, verdict.State);
        Assert.True(verdict.ForbidsWriting);
    }

    [Fact]
    public void Inspect_ALinkNothingDeclares_ForbidsWriting() {
        var fileSystem = WithCheckout();
        fileSystem.CreateDirectoryLink(Destination, Checkout);

        var verdict = AddonLinkGuard.Inspect(fileSystem, Destination, declared: null);

        Assert.Equal(AddonLinkState.Undeclared, verdict.State);
        Assert.True(verdict.ForbidsWriting);
    }

    [Fact]
    public void Inspect_ADeclarationPointingAtNothing_ForbidsWriting() {
        var fileSystem = WithCheckout();
        fileSystem.CreateDirectoryLink(Destination, Checkout);

        // The checkout was moved or deleted. Installing over the link would quietly replace a
        // developer's link with a copy on the strength of a path that no longer means anything.
        var verdict = AddonLinkGuard.Inspect(fileSystem, Destination,
            new LocalLink("my-addon", "gone/missing"));

        Assert.Equal(AddonLinkState.TargetMissing, verdict.State);
        Assert.True(verdict.ForbidsWriting);
    }

    [Fact]
    public void Inspect_TheSameFolderSpelledDifferently_StillMatches() {
        var fileSystem = WithCheckout();
        fileSystem.CreateDirectoryLink(Destination, Checkout);

        // Separators and a trailing slash are spelling, not meaning — the same discipline GitUrl
        // applies to remotes. A person who types the path by hand must not be told it is a
        // different folder because they used a backslash.
        var verdict = AddonLinkGuard.Inspect(
            fileSystem, Destination, new LocalLink("my-addon", @"elsewhere\my-addon/"));

        Assert.Equal(AddonLinkState.LinkedAsDeclared, verdict.State);
    }

    [Fact]
    public void SamePath_DiffersOnlyByCase_MatchesOnWindowsAndNotElsewhere() {
        var fileSystem = WithCheckout();

        // Asserted on the comparison itself rather than through Inspect, because the in-memory
        // filesystem is case-sensitive about whether a folder exists while Windows is not — so a
        // differently-cased declaration reads as "target missing" in the fake before the comparison
        // is ever reached. The rule under test is which spellings mean the same folder.
        Assert.Equal(
            OperatingSystem.IsWindows(),
            AddonLinkGuard.SamePath(fileSystem, Checkout, "Elsewhere/My-Addon"));
    }

    [Fact]
    public void Explain_NamesBothStatementsAndBothWaysOut() {
        var fileSystem = WithCheckout();
        fileSystem.WriteAllText("somewhere-else/thing.gd", "# a different checkout");
        fileSystem.CreateDirectoryLink(Destination, "somewhere-else");

        var verdict = AddonLinkGuard.Inspect(fileSystem, Destination,
            new LocalLink("my-addon", Checkout));
        var message = AddonLinkGuard.Explain("my-addon", "my-addon", "v1.2.3", verdict);

        // Either statement can be the stale one, so the message reports both and picks neither.
        Assert.Contains("somewhere-else", message);
        Assert.Contains(Checkout, message);
        Assert.Contains("v1.2.3", message);
        Assert.Contains("dotquay link my-addon", message);
        Assert.Contains("--remove", message);
        Assert.Contains("Nothing was changed", message);
    }

    /// <summary>
    /// The last gate. <see cref="AddonLinkGuard"/> classifies before an install starts, which is
    /// where a human-readable refusal belongs — but that check sits at one call site and a future
    /// call path could route around it. This one is the only door content goes through.
    /// </summary>
    [Fact]
    public void SwapAndHash_OntoALink_IsRefusedAsADotquayBug() {
        var fileSystem = WithCheckout();
        fileSystem.WriteAllText("staging/thing.gd", "# incoming version");
        fileSystem.CreateDirectoryLink(Destination, Checkout);

        // force is set deliberately: the flag that discards local modifications must not also buy a
        // write into a folder dotquay does not own.
        var exception = Assert.Throws<SyncException>(() =>
            new SyncEngine(fileSystem)
            .SwapAndHash("staging", Destination, expectedContentHash: null, force: true));

        Assert.Contains("bug in dotquay", exception.Message);
        Assert.Equal("# live edit",
            fileSystem.ReadAllText(fileSystem.CombinePath(Checkout, "thing.gd")));
    }
}
