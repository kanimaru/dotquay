using Dotquay.Core.Doctor;
using Dotquay.Tests.Support;
using Xunit;

namespace Dotquay.Tests.Doctor;

/// <summary>
/// Covers reading OpenSSH <c>known_hosts</c>.
/// </summary>
/// <remarks>
/// Dense because the failure mode is asymmetric. Missing a host that <em>is</em> trusted makes
/// <c>doctor</c> confidently wrong about a working machine, which is how a check earns a reputation
/// for noise and stops being read. The hashed-entry cases matter most: hashing is the default on
/// most Linux distributions, so getting it wrong means being wrong about every host on those
/// machines.
/// </remarks>
public class KnownHostsReaderTests {
    // Real `ssh-keyscan` output shape, key material truncated.
    private const string PlainEntry = "gitlab.kemomi.de ssh-ed25519 "
        + "AAAAC3NzaC1lZDI1NTE5AAAAIExample";

    // A genuine hashed entry for "gitlab.kemomi.de", produced by HMAC-SHA1 over the salt below.
    // Regenerating it by hand is the point: a hardcoded pair proves the algorithm, not just the
    // parser.
    private static string HashedEntryFor(string host) {
        var salt = Convert.FromBase64String("F1Fe0RS+7l/JsG2rL7uYlH8DbCQ=");
        var hash = System.Security.Cryptography.HMACSHA1.HashData(salt,
            System.Text.Encoding.UTF8.GetBytes(host));

        return $"|1|{Convert.ToBase64String(salt)}|{Convert.ToBase64String(hash)} ssh-ed25519 "
            + $"AAAAC3NzaC1Example";
    }

    // -----------------------------------------------------------------------------------------
    // Plain entries
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void PlainEntry_IsFound() =>
        Assert.Equal(KnownHostStatus.Trusted,
            KnownHostsReader.LookupIn(PlainEntry, "gitlab.kemomi.de"));

    [Fact]
    public void MissingHost_IsNotFound() =>
        Assert.Equal(KnownHostStatus.NotFound, KnownHostsReader.LookupIn(PlainEntry, "github.com"));

    [Fact]
    public void HostnameMatching_IsCaseInsensitive() =>
        Assert.Equal(KnownHostStatus.Trusted,
            KnownHostsReader.LookupIn(PlainEntry, "GitLab.Kemomi.DE"));

    [Fact]
    public void CommaSeparatedAliases_AllMatch() {
        const string content = "gitlab.kemomi.de,10.0.0.7 ssh-ed25519 AAAAExample";

        Assert.Equal(KnownHostStatus.Trusted, KnownHostsReader.LookupIn(content, "10.0.0.7"));
    }

    [Fact]
    public void CommentsAndBlankLines_AreIgnored() {
        // ssh-keyscan writes a "# host:22 SSH-2.0-..." banner comment before each key. A parser
        // that matched inside comments would find every host it was asked about.
        const string content = "\n# gitlab.kemomi.de:22 SSH-2.0-OpenSSH_9.6p1\n\n";

        Assert.Equal(KnownHostStatus.NotFound,
            KnownHostsReader.LookupIn(content, "gitlab.kemomi.de"));
    }

    // -----------------------------------------------------------------------------------------
    // Ports
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void NonDefaultPort_UsesTheBracketedForm() {
        var fileSystem = new InMemoryFileSystem();
        fileSystem.WriteAllText("known_hosts", "[gitlab.kemomi.de]:2222 ssh-ed25519 AAAAExample");

        Assert.Equal(KnownHostStatus.Trusted,
            KnownHostsReader.Lookup(fileSystem, ["known_hosts"], "gitlab.kemomi.de", 2222));

        // And the bracketed entry is not trust for the same host on 22 -- a different port is a
        // different service, which is exactly why ssh records it separately.
        Assert.Equal(KnownHostStatus.NotFound,
            KnownHostsReader.Lookup(fileSystem, ["known_hosts"], "gitlab.kemomi.de"));
    }

    [Fact]
    public void Port22_IsTreatedAsTheDefault() {
        var fileSystem = new InMemoryFileSystem();
        fileSystem.WriteAllText("known_hosts", PlainEntry);

        Assert.Equal(KnownHostStatus.Trusted,
            KnownHostsReader.Lookup(fileSystem, ["known_hosts"], "gitlab.kemomi.de", 22));
    }

    // -----------------------------------------------------------------------------------------
    // Hashed entries
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void HashedEntry_IsFound() {
        // HashKnownHosts is on by default on most Linux distributions. Skipping the HMAC would
        // report every trusted host on such a machine as untrusted.
        Assert.Equal(KnownHostStatus.Trusted,
            KnownHostsReader.LookupIn(HashedEntryFor("gitlab.kemomi.de"), "gitlab.kemomi.de"));
    }

    [Fact]
    public void HashedEntry_DoesNotMatchAnotherHost() =>
        Assert.Equal(KnownHostStatus.NotFound,
            KnownHostsReader.LookupIn(HashedEntryFor("gitlab.kemomi.de"), "github.com"));

    [Fact]
    public void HashedEntry_WithMangledBase64_IsIgnoredNotThrown() {
        // A hand-edited file is not this check's business to report, and must never be the reason a
        // command fails.
        const string content = "|1|not-base64!!|also-not-base64!! ssh-ed25519 AAAAExample";

        Assert.Equal(KnownHostStatus.NotFound,
            KnownHostsReader.LookupIn(content, "gitlab.kemomi.de"));
    }

    // -----------------------------------------------------------------------------------------
    // Markers and patterns
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void CertAuthorityEntry_CountsAsTrust() {
        // The host's key is signed by a CA the machine already trusts. Nothing will prompt.
        const string content = "@cert-authority *.kemomi.de ssh-ed25519 AAAAExample";

        Assert.Equal(KnownHostStatus.Trusted,
            KnownHostsReader.LookupIn(content, "gitlab.kemomi.de"));
    }

    [Fact]
    public void RevokedEntry_IsWorseThanMissing() {
        const string content = "@revoked gitlab.kemomi.de ssh-ed25519 AAAAExample";

        Assert.Equal(KnownHostStatus.Revoked,
            KnownHostsReader.LookupIn(content, "gitlab.kemomi.de"));
    }

    [Fact]
    public void RevocationWins_OverAnOrdinaryEntryAnywhereElse() {
        // The order they appear in must not decide it. A revoked key rescued by a stale entry is
        // the one outcome this whole marker exists to prevent.
        var content = $"{PlainEntry}\n@revoked gitlab.kemomi.de ssh-ed25519 AAAAOther";
        var reversed = $"@revoked gitlab.kemomi.de ssh-ed25519 AAAAOther\n{PlainEntry}";

        Assert.Equal(KnownHostStatus.Revoked,
            KnownHostsReader.LookupIn(content, "gitlab.kemomi.de"));
        Assert.Equal(KnownHostStatus.Revoked,
            KnownHostsReader.LookupIn(reversed, "gitlab.kemomi.de"));
    }

    [Fact]
    public void RevocationInOneFile_IsNotRescuedByAnother() {
        var fileSystem = new InMemoryFileSystem();
        fileSystem.WriteAllText("user", PlainEntry);
        fileSystem.WriteAllText("system", "@revoked gitlab.kemomi.de ssh-ed25519 AAAAOther");

        Assert.Equal(KnownHostStatus.Revoked,
            KnownHostsReader.Lookup(fileSystem, ["user", "system"], "gitlab.kemomi.de"));
    }

    [Fact]
    public void WildcardPattern_Matches() =>
        Assert.Equal(KnownHostStatus.Trusted,
            KnownHostsReader.LookupIn("*.kemomi.de ssh-ed25519 AAAAExample", "gitlab.kemomi.de"));

    [Fact]
    public void NegatedPattern_VetoesTheLine() {
        // "everything on this domain except that one" has to actually mean it.
        const string content = "*.kemomi.de,!gitlab.kemomi.de ssh-ed25519 AAAAExample";

        Assert.Equal(KnownHostStatus.NotFound,
            KnownHostsReader.LookupIn(content, "gitlab.kemomi.de"));
        Assert.Equal(KnownHostStatus.Trusted,
            KnownHostsReader.LookupIn(content, "other.kemomi.de"));
    }

    [Fact]
    public void MissingFiles_AreSkipped() {
        // A machine that has never used ssh has no ~/.ssh at all. That is a normal state, not an
        // error to surface.
        Assert.Equal(KnownHostStatus.NotFound,
            KnownHostsReader.Lookup(new InMemoryFileSystem(), ["nope"], "gitlab.kemomi.de"));
    }

    [Fact]
    public void CrlfLineEndings_AreHandled() {
        // known_hosts written on Windows, or copied from it. Trailing \r would otherwise be part of
        // the last field on every line.
        Assert.Equal(KnownHostStatus.Trusted,
            KnownHostsReader.LookupIn($"{PlainEntry}\r\n", "gitlab.kemomi.de"));
    }

    // -----------------------------------------------------------------------------------------
    // Where the files live
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void DefaultPaths_OnUnix_PreferTheUsersFileAndIncludeTheSystemOne() {
        var environment = new FakeEnvironmentReader { HomeDirectory = "/home/kani" };

        var paths = KnownHostsReader.DefaultPaths(environment, new InMemoryFileSystem(),
            isWindows: false);

        Assert.Contains(paths, p => p.Contains(".ssh", StringComparison.Ordinal)
            && p.EndsWith("known_hosts", StringComparison.Ordinal));
        Assert.Contains(paths, p => p.Contains("/etc/ssh/", StringComparison.Ordinal));
    }

    [Fact]
    public void DefaultPaths_OnWindows_UseProgramDataForTheSystemFile() {
        var environment =
            new FakeEnvironmentReader { HomeDirectory = @"C:\Users\kani" }.Set("ProgramData",
                @"D:\ProgramData");

        var paths = KnownHostsReader.DefaultPaths(environment, new InMemoryFileSystem(),
            isWindows: true);

        // Substring, not the whole path: InMemoryFileSystem normalises separators to forward
        // slashes, which is a fact about the fake and not about what is being asserted.
        Assert.Contains(paths, p => p.Contains("D:", StringComparison.Ordinal)
            && p.Contains("ProgramData", StringComparison.Ordinal));
        Assert.DoesNotContain(paths, p => p.Contains("/etc/ssh/", StringComparison.Ordinal));
    }

    [Fact]
    public void DefaultPaths_OnWindowsWithoutProgramDataSet_StillDoesNotNameAUnixPath() {
        // Caught by running the real binary, not by this suite: the variable is set in every normal
        // Windows session and empty in some stripped-down ones, and the first version keyed the
        // platform off it -- so a Windows machine was told its known_hosts lived in /etc/ssh.
        var paths = KnownHostsReader.DefaultPaths(
            new FakeEnvironmentReader { HomeDirectory = @"C:\Users\kani" },
            new InMemoryFileSystem(), isWindows: true);

        Assert.DoesNotContain(paths, p => p.Contains("/etc/ssh/", StringComparison.Ordinal));
        Assert.Contains(paths, p => p.Contains("ProgramData", StringComparison.Ordinal));
    }

    [Fact]
    public void DefaultPaths_NoHomeDirectory_StillReturnsTheSystemFile() {
        // HomeDirectory can be null, and that is not a reason to check nothing.
        var paths = KnownHostsReader.DefaultPaths(
            new FakeEnvironmentReader { HomeDirectory = null }, new InMemoryFileSystem(),
            isWindows: false);

        Assert.NotEmpty(paths);
    }

    [Fact]
    public void LeadingByteOrderMark_DoesNotCostTheFirstEntryItsTrust() {
        // 'ssh-keyscan host >> known_hosts' in PowerShell 5.1 writes UTF-16, BOM included. Found on
        // a real machine while verifying this check -- with the first entry's hostname mangled by
        // it.
        Assert.Equal(KnownHostStatus.Trusted,
            KnownHostsReader.LookupIn("\uFEFF" + PlainEntry, "gitlab.kemomi.de"));
    }
}
