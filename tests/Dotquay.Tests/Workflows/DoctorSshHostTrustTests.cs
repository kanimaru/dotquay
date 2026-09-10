using Dotquay.Core.Workflows;
using Dotquay.Tests.Support;
using Xunit;

namespace Dotquay.Tests.Workflows;

/// <summary>
/// Covers <c>dotquay doctor</c>'s offline check that every ssh host in the lockfile has a trusted
/// host key on this machine.
/// </summary>
/// <remarks>
/// From the setup failure this whole change came out of: a developer cloning a project for the first
/// time hit "Host key verification failed" from inside a fetch, followed by git's generic advice
/// about access rights, and went looking for a credential problem that did not exist. Nothing in the
/// project was wrong, so nothing in the project could be edited to fix it.
/// </remarks>
public class DoctorSshHostTrustTests
{
    private const string ProjectDirectory = "project";
    private const string KnownHostsPath = "/home/kani/.ssh/known_hosts";

    private static string Sha => new('a', 40);

    private static InMemoryFileSystem BuildProject(params (string Name, string Url)[] addons)
    {
        var fileSystem = new InMemoryFileSystem();

        var entries = addons.Select(addon => $"""
            [[addon]]
            name = "{addon.Name}"
            git = "{addon.Url}"
            resolved = "{Sha}"
            path = "."
            """);

        fileSystem.WriteAllText(fileSystem.CombinePath(ProjectDirectory, "dotquay.lock"), string.Join("\n\n", entries));

        return fileSystem;
    }

    private static async Task<IReadOnlyList<DoctorIssue>> RunAsync(InMemoryFileSystem fileSystem)
    {
        var workflow = new DependencyWorkflow(fileSystem, new FakeGitClient(), cacheRoot: "cache")
        {
            // Pinned rather than inherited: whether this suite passes must not depend on whether the
            // machine running it happens to trust gitlab.kemomi.de.
            EnvironmentReader = new FakeEnvironmentReader { HomeDirectory = "/home/kani" },
        };

        var result = await workflow.DoctorAsync(ProjectDirectory);

        return result.Issues.Where(i => i.Kind == DoctorIssueKind.SshHostNotTrusted).ToList();
    }

    [Fact]
    public async Task SshAddonWithNoKnownHostsEntry_IsReported()
    {
        var fileSystem = BuildProject(("mtoon-shader", "git@gitlab.kemomi.de:wild-alliance/mtoon-shader.git"));

        var issue = Assert.Single(await RunAsync(fileSystem));

        Assert.Contains("gitlab.kemomi.de", issue.Message, StringComparison.Ordinal);
        Assert.Contains("mtoon-shader", issue.Message, StringComparison.Ordinal);
        // The exact wording git will produce, so searching the web for the error lands back here.
        Assert.Contains("Host key verification failed", issue.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SshAddonWithATrustedHost_IsSilent()
    {
        var fileSystem = BuildProject(("mtoon-shader", "git@gitlab.kemomi.de:wild-alliance/mtoon-shader.git"));
        fileSystem.WriteAllText(KnownHostsPath, "gitlab.kemomi.de ssh-ed25519 AAAAExample");

        Assert.Empty(await RunAsync(fileSystem));
    }

    [Fact]
    public async Task HttpsAddons_AreNeverReported()
    {
        // No host key is involved, so there is nothing to be untrusted. A check that complained here
        // would fire on the majority of projects and immediately stop being read.
        var fileSystem = BuildProject(("mtoon-shader", "https://gitlab.kemomi.de/wild-alliance/mtoon-shader"));

        Assert.Empty(await RunAsync(fileSystem));
    }

    [Fact]
    public async Task LocalPathAddons_AreNeverReported()
    {
        // Forward slashes because the lockfile is TOML and a backslash starts an escape there; the
        // drive-letter rule keys on the separator, not on which one it is.
        var fileSystem = BuildProject(("local-thing", "C:/repos/local-thing"));

        Assert.Empty(await RunAsync(fileSystem));
    }

    [Fact]
    public async Task ManyAddonsOnOneHost_ProduceOneIssue()
    {
        // The known_hosts entry is per host: ten addons on one GitLab need one fix, and ten copies of
        // it would bury whatever else doctor found.
        var fileSystem = BuildProject(
            ("mtoon-shader", "git@gitlab.kemomi.de:wild-alliance/mtoon-shader.git"),
            ("vbeami", "git@gitlab.kemomi.de:wild-alliance/vbeami.git"),
            ("core", "git@gitlab.kemomi.de:wild-alliance/core.git"));

        var issue = Assert.Single(await RunAsync(fileSystem));

        Assert.Contains("core", issue.Message, StringComparison.Ordinal);
        Assert.Contains("vbeami", issue.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ManyAddonsOnManyHosts_ProduceOneIssueEach()
    {
        var fileSystem = BuildProject(
            ("mtoon-shader", "git@gitlab.kemomi.de:wild-alliance/mtoon-shader.git"),
            ("gut", "git@github.com:bitwes/gut.git"));

        Assert.Equal(2, (await RunAsync(fileSystem)).Count);
    }

    [Fact]
    public async Task PartiallyTrusted_ReportsOnlyTheUntrustedHost()
    {
        var fileSystem = BuildProject(
            ("mtoon-shader", "git@gitlab.kemomi.de:wild-alliance/mtoon-shader.git"),
            ("gut", "git@github.com:bitwes/gut.git"));
        fileSystem.WriteAllText(KnownHostsPath, "github.com ssh-ed25519 AAAAExample");

        var issue = Assert.Single(await RunAsync(fileSystem));

        Assert.Contains("gitlab.kemomi.de", issue.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("github.com", issue.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RevokedHostKey_SaysSoRatherThanSuggestingAKeyscan()
    {
        // The remedy is the opposite of the ordinary case: do not go and trust it, go and find out
        // why it changed.
        var fileSystem = BuildProject(("mtoon-shader", "git@gitlab.kemomi.de:wild-alliance/mtoon-shader.git"));
        fileSystem.WriteAllText(KnownHostsPath, "@revoked gitlab.kemomi.de ssh-ed25519 AAAAExample");

        var issue = Assert.Single(await RunAsync(fileSystem));

        Assert.Contains("revoked", issue.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ssh-keyscan", issue.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NonDefaultPort_IsMatchedAgainstTheBracketedEntry()
    {
        var fileSystem = BuildProject(("mtoon-shader", "ssh://git@gitlab.kemomi.de:2222/wild-alliance/mtoon-shader"));
        fileSystem.WriteAllText(KnownHostsPath, "[gitlab.kemomi.de]:2222 ssh-ed25519 AAAAExample");

        Assert.Empty(await RunAsync(fileSystem));
    }

    [Fact]
    public async Task EmptyProject_IsSilent() =>
        Assert.Empty(await RunAsync(BuildProject()));
}
