using Dotquay.Core.Json;
using Dotquay.Core.Workflows;
using Dotquay.Tests.Support;
using Xunit;

namespace Dotquay.Tests.Workflows;

/// <summary>
/// <c>dotquay doctor</c> reporting an installed addon whose binaries are Git LFS placeholders. The state is
/// otherwise invisible: the lockfile hash was taken over the placeholders, so <c>status</c> says clean.
/// </summary>
public class DoctorLfsPointerTests {
    private const string ProjectDirectory = "project";
    private const string Url = "git@example.com:wild-alliance/vbeami_gdmp.git";
    private const string Pointer = "version https://git-lfs.github.com/spec/v1\noid sha256:968c222503ef1c9e27e1071361187c210ee72457ee6c6afebfd46aa845adca77\nsize 2500000\n";

    private static InMemoryFileSystem Project(Action<InMemoryFileSystem> addonFiles) {
        var fs = new InMemoryFileSystem();
        fs.WriteAllText(fs.CombinePath(ProjectDirectory, "dotquay.toml"), $"""
            [project]

            [addons.GDMP]
            git = "{Url}"
            version = "~0.6"
            path = "addons/GDMP"
            """);
        fs.WriteAllText(fs.CombinePath(ProjectDirectory, "dotquay.lock"), $"""
            [[addon]]
            name = "GDMP"
            git = "{Url}"
            resolved = "{new string('a', 40)}"
            path = "addons/GDMP"
            """);
        addonFiles(fs);
        return fs;
    }

    private static async Task<DoctorResult> RunAsync(InMemoryFileSystem fs)
        => await new DependencyWorkflow(fs, new FakeGitClient(), cacheRoot: "cache").DoctorAsync(ProjectDirectory);

    [Fact]
    public async Task DoctorAsync_AnAddonFullOfPlaceholders_IsReported_WithTheFix() {
        var fs = Project(f => {
            f.WriteAllText($"{ProjectDirectory}/addons/GDMP/plugin.cfg", "[plugin]");
            f.WriteAllText($"{ProjectDirectory}/addons/GDMP/libs/x86_64/GDMP.windows.dll", Pointer);
            f.WriteAllText($"{ProjectDirectory}/addons/GDMP/libs/x86_64/libGDMP.linux.so", Pointer);
        });

        var issue = Assert.Single((await RunAsync(fs)).Issues, i => i.Kind == DoctorIssueKind.LfsPointerFile);

        Assert.Contains("'GDMP'", issue.Message);
        Assert.Contains("2 files", issue.Message);
        Assert.Contains("addons/GDMP/libs/x86_64/GDMP.windows.dll", issue.Message);
        Assert.Contains("git lfs install", issue.Message);
        Assert.Contains("dotquay install", issue.Message);
    }

    [Fact]
    public async Task DoctorAsync_AnAddonWithItsRealFiles_ReportsNothing() {
        var fs = Project(f => {
            f.WriteAllText($"{ProjectDirectory}/addons/GDMP/plugin.cfg", "[plugin]");
            f.WriteAllBytes($"{ProjectDirectory}/addons/GDMP/libs/x86_64/GDMP.windows.dll", new byte[20_000]);
        });

        Assert.DoesNotContain((await RunAsync(fs)).Issues, i => i.Kind == DoctorIssueKind.LfsPointerFile);
    }

    [Fact]
    public async Task DoctorAsync_TheIssueReachesTheEditorPluginUnderItsOwnWireName() {
        var fs = Project(f => f.WriteAllText($"{ProjectDirectory}/addons/GDMP/libs/a.dll", Pointer));

        var json = JsonMapper.ToJson(await RunAsync(fs));

        // The plugin groups and words issues by this exact string; it must not collapse into "unknown".
        Assert.Contains(json.Issues, i => i.Kind == "lfs_pointer_file");
    }
}
