using System.Text;
using Dotquay.Core.Auth;
using Dotquay.Core.Doctor;
using Dotquay.Core.IO;
using Dotquay.Core.Local;
using Dotquay.Core.Lockfile;
using Dotquay.Core.Manifest;
using Dotquay.Core.SelfUpdate;
using Dotquay.Core.Toml;
using Dotquay.Tests.Support;
using Tomlyn;
using Xunit;

namespace Dotquay.Tests.Toml;

/// <summary>
/// Pins that every TOML file dotquay reads still reads the same after the readers moved from
/// Tomlyn's reflection-based <c>Deserialize&lt;TomlTable&gt;</c> to the source-generated
/// <see cref="DotquayToml"/>, and that what dotquay writes still reads back.
/// </summary>
/// <remarks>
/// The fixtures are written to a temp folder through the real <see cref="SystemFileSystem"/>, with
/// the bytes files in the wild carry (CRLF line endings, a UTF-8 BOM from an editor, comments,
/// inline tables, literal strings, unquoted dotted keys), because those are what a parser change
/// could read differently. Home is the temp folder too: no test here reads the owner's real
/// <c>~/.dotquay</c> or any real credential.
/// </remarks>
public sealed class TomlFileCompatibilityTests : IDisposable
{
    private const string Sha = "0123456789abcdef0123456789abcdef01234567";
    private const string OtherSha = "fedcba9876543210fedcba9876543210fedcba98";
    private const string FeedUrl = "https://gitlab.example.test/api/v4/projects/7/packages/nuget/index.json";

    private readonly string _root;
    private readonly SystemFileSystem _fileSystem = new();

    public TomlFileCompatibilityTests()
    {
        _root = Directory.CreateTempSubdirectory("dotquay-toml-compat-").FullName;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Best effort: a cleanup wrinkle never fails a test whose assertions passed.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public void ProjectManifest_HandWrittenWithBomAndCrlf_ReadsEveryShape()
    {
        const string Text = """
            # A hand-edited manifest: comments, inline tables, literal strings.
            [project]
            godot = "4.4"            # legacy key, read and discarded
            patches_dir = 'local/patches'

            [nuget]
            trusted_hosts = ["gitlab.example.test"]

            [nuget.sources.private]
            url = "https://gitlab.example.test/api/v4/projects/7/packages/nuget/index.json"
            packages = [
                "Kani.*",
                "VBeami.*",   # trailing comma below is valid TOML
            ]

            [addons]
            inline = { git = "https://example.test/inline.git", rev = "0123456789abcdef0123456789abcdef01234567" }

            [addons.widget]
            git = "git@example.test:team/widget.git"
            branch = "main"
            path = "addons/widget"
            install_as = "Widget"

            [dotnet.Kani.Core]
            version = "1.2.3"
            source = "private"

            [dotnet."Newtonsoft.Json"]
            version = "13.0.3"

            [overrides.widget]
            git = "https://example.test/fork.git"
            rev = "fedcba9876543210fedcba9876543210fedcba98"
            """;
        var path = WriteFixture("dotquay.toml", Text, withBom: true, crlf: true);

        var manifest = ManifestReader.ReadProject(_fileSystem.ReadAllText(path), path);

        Assert.Equal("local/patches", manifest.PatchesDirectory);
        Assert.Equal(["gitlab.example.test"], manifest.TrustedHosts);
        var source = Assert.Single(manifest.NuGetSources);
        Assert.Equal("private", source.Name);
        Assert.Equal(FeedUrl, source.Url);
        Assert.Equal(["Kani.*", "VBeami.*"], source.Packages);

        Assert.Equal(["inline", "widget"], manifest.Addons.Keys.Order(StringComparer.Ordinal));
        Assert.Equal("https://example.test/inline.git", manifest.Addons["inline"].Git);
        Assert.Equal(Sha, manifest.Addons["inline"].Rev);
        var widget = manifest.Addons["widget"];
        Assert.Equal("git@example.test:team/widget.git", widget.Git);
        Assert.Equal("main", widget.Branch);
        Assert.Equal("addons/widget", widget.Path);
        Assert.Equal("Widget", widget.InstallAs);

        Assert.Equal("1.2.3", manifest.Dotnet["Kani.Core"].Version);
        Assert.Equal("private", manifest.Dotnet["Kani.Core"].Source);
        Assert.Equal("13.0.3", manifest.Dotnet["Newtonsoft.Json"].Version);
        Assert.Null(manifest.Dotnet["Newtonsoft.Json"].Source);

        Assert.Equal(OtherSha, manifest.Overrides["widget"].Rev);
    }

    [Fact]
    public void ProjectManifest_WrittenByDotquay_ReadsBackUnchanged()
    {
        var original = ManifestReader.ReadProject("""
            [nuget.sources.private]
            url = "https://gitlab.example.test/api/v4/projects/7/packages/nuget/index.json"
            packages = ["Kani.*"]

            [addons.widget]
            git = "https://example.test/widget.git"
            version = "^1.2"
            install_as = "Widget"

            [dotnet."Kani.Core"]
            version = "1.2.3"
            source = "private"
            """);
        var path = _fileSystem.CombinePath(_root, "dotquay.toml");
        _fileSystem.WriteAllText(path, ManifestWriter.Serialize(original));

        var reread = ManifestReader.ReadProject(_fileSystem.ReadAllText(path), path);

        var source = Assert.Single(reread.NuGetSources);
        Assert.Equal(("private", FeedUrl), (source.Name, source.Url));
        Assert.Equal(["Kani.*"], source.Packages);
        Assert.Equal("^1.2", reread.Addons["widget"].Version);
        Assert.Equal("Widget", reread.Addons["widget"].InstallAs);
        Assert.Equal("1.2.3", reread.Dotnet["Kani.Core"].Version);
        Assert.Equal("private", reread.Dotnet["Kani.Core"].Source);
    }

    [Fact]
    public void PackageManifest_HandWritten_ReadsNameAddonsAndDotnet()
    {
        const string Text = """
            [package]
            name = "kani_core"
            version = "0.3.0"

            [addons.helper]
            git = "https://example.test/helper.git"
            version = "~0.2"

            [dotnet.Kani.Core]
            version = "1.2.3"
            """;
        var path = WriteFixture("dotquay.toml", Text, withBom: false, crlf: true);

        var manifest = ManifestReader.ReadPackage(_fileSystem.ReadAllText(path), path);

        Assert.Equal("kani_core", manifest.Name);
        Assert.Equal("0.3.0", manifest.Version);
        Assert.Equal("~0.2", manifest.Addons["helper"].Version);
        Assert.Equal("1.2.3", manifest.Dotnet["Kani.Core"].Version);
    }

    [Fact]
    public void Lockfile_HandWritten_ReadsEveryField()
    {
        const string Text = """
            # This file is generated by dotquay. Do not edit by hand.

            [[addon]]
            name = "widget"
            git = "https://example.test/widget.git"
            resolved = "0123456789abcdef0123456789abcdef01234567"
            version = "1.2.0"
            path = "addons/widget"
            folder = "Widget"
            content_hash = "sha256:abc"
            patch_hash = "sha256:def"
            patch_base_sha = "fedcba9876543210fedcba9876543210fedcba98"
            dependencies = [
                "helper",
            ]

            [[addon]]
            name = "helper"
            git = "https://example.test/helper.git"
            resolved = "fedcba9876543210fedcba9876543210fedcba98"
            path = "addons/helper"
            dependencies = []

            [[dotnet]]
            name = "Kani.Core"
            resolved = "1.2.3"
            source = "private"
            include_assets = ["compile", "runtime"]

            [[dotnet]]
            name = "Newtonsoft.Json"
            resolved = "13.0.3"
            """;
        var path = WriteFixture("dotquay.lock", Text, withBom: true, crlf: true);

        var lockfile = LockfileReader.Read(_fileSystem.ReadAllText(path), path);

        Assert.Equal(["widget", "helper"], lockfile.Addons.Select(addon => addon.Name));
        var widget = lockfile.Addons[0];
        Assert.Equal(Sha, widget.Resolved);
        Assert.Equal("1.2.0", widget.Version);
        Assert.Equal("addons/widget", widget.Path);
        Assert.Equal("Widget", widget.Folder);
        Assert.Equal("sha256:abc", widget.ContentHash);
        Assert.Equal("sha256:def", widget.PatchHash);
        Assert.Equal(OtherSha, widget.PatchBaseSha);
        Assert.Equal(["helper"], widget.Dependencies);
        Assert.Null(lockfile.Addons[1].Folder);
        Assert.Empty(lockfile.Addons[1].Dependencies);

        Assert.Equal(["Kani.Core", "Newtonsoft.Json"], lockfile.Dotnet.Select(package => package.Name));
        Assert.Equal("private", lockfile.Dotnet[0].Source);
        Assert.Equal(["compile", "runtime"], lockfile.Dotnet[0].Assets.Include);
        Assert.Null(lockfile.Dotnet[1].Source);
        Assert.Null(lockfile.Dotnet[1].Assets.Include);
    }

    [Fact]
    public void Lockfile_WrittenByDotquay_ReadsBackUnchanged()
    {
        var original = new DotquayLockfile
        {
            Addons =
            [
                new LockedAddon
                {
                    Name = "widget",
                    Git = "https://example.test/widget.git",
                    Resolved = Sha,
                    Version = "1.2.0",
                    Path = "addons/widget",
                    Folder = "Widget",
                    ContentHash = "sha256:abc",
                    Dependencies = ["helper"],
                },
            ],
            Dotnet = [new LockedDotnet { Name = "Kani.Core", Resolved = "1.2.3", Source = "private" }],
        };
        var path = _fileSystem.CombinePath(_root, "dotquay.lock");
        _fileSystem.WriteAllText(path, LockfileWriter.Serialize(original));

        var reread = LockfileReader.Read(_fileSystem.ReadAllText(path), path);

        Assert.Equal(original.Addons, reread.Addons, LockedAddonComparer.Instance);
        var package = Assert.Single(reread.Dotnet);
        Assert.Equal("Kani.Core", package.Name);
        Assert.Equal("1.2.3", package.Resolved);
        Assert.Equal("private", package.Source);
    }

    [Fact]
    public void LocalLinks_LegacyAndNamespacedShapes_ReadAsBefore()
    {
        const string Text = """
            # v0.6 flat shape, still read as an addon link
            [link.old_addon]
            path = 'C:\dev\old_addon'

            [link.addon.widget]
            path = "C:\\dev\\widget"

            [link.dotnet.Kani.Core]
            path = "C:/dev/Kani.Core/Kani.Core.csproj"
            analyzer = true

            [link.dotnet."Newtonsoft.Json"]
            path = "C:/dev/Json/Json.csproj"
            """;
        WriteFixture(LocalLinks.FileName, Text, withBom: true, crlf: true);

        var links = new LocalLinks(_fileSystem).ReadAll(_root);

        Assert.Equal(@"C:\dev\old_addon", links.Addons["old_addon"].Path);
        Assert.Equal(@"C:\dev\widget", links.Addons["widget"].Path);
        Assert.Equal("C:/dev/Kani.Core/Kani.Core.csproj", links.Dotnet["Kani.Core"].ProjectPath);
        Assert.True(links.Dotnet["Kani.Core"].Analyzer);
        Assert.False(links.Dotnet["Newtonsoft.Json"].Analyzer);
    }

    [Fact]
    public void LocalLinks_WrittenByDotquay_ReadBackUnchanged()
    {
        // Real folders, since setting a link checks its target exists. The space and the
        // platform's separators (backslashes on Windows) are what the writer has to escape.
        var addonFolder = Directory.CreateDirectory(Path.Combine(_root, "my widget", "addons", "widget")).FullName;
        var projectFolder = Directory.CreateDirectory(Path.Combine(_root, "Kani.Core")).FullName;
        var projectFile = Path.Combine(projectFolder, "Kani.Core.csproj");
        File.WriteAllText(projectFile, "<Project />");
        var links = new LocalLinks(_fileSystem);
        links.Set(_root, "widget", addonFolder);
        links.SetDotnet(_root, "Kani.Core", projectFile, analyzer: true);

        var reread = new LocalLinks(_fileSystem).ReadAll(_root);

        Assert.Equal(links.Read(_root)["widget"].Path, reread.Addons["widget"].Path);
        Assert.Equal(
            Path.GetFullPath(addonFolder).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(reread.Addons["widget"].Path).TrimEnd(Path.DirectorySeparatorChar));
        Assert.Equal(Path.GetFullPath(projectFile), Path.GetFullPath(reread.Dotnet["Kani.Core"].ProjectPath));
        Assert.True(reread.Dotnet["Kani.Core"].Analyzer);
    }

    [Fact]
    public void CredentialStore_HandWrittenFile_FindsEachScope()
    {
        // Placeholder secrets only: these never were and never will be real tokens.
        const string Text = """
            # dotquay credentials - written by 'dotquay login'.
            version = 1

            [credential."gitlab.example.test"]
            username = "ci-bot"
            token = "fake-token-host"

            [credential."gitlab.example.test/team"]
            username = 'team-bot'
            token = "fake-\"quoted\"\\token"
            """;
        var path = WriteFixture(TomlCredentialStore.FileName, Text, withBom: true, crlf: true);
        var store = new TomlCredentialStore(_fileSystem, path);

        var host = store.Find(CredentialScope.Parse("gitlab.example.test/other/repo"));
        var team = store.Find(CredentialScope.Parse("gitlab.example.test/team/repo"));

        Assert.Equal(new Credential("ci-bot", "fake-token-host"), host);
        Assert.Equal(new Credential("team-bot", "fake-\"quoted\"\\token"), team);
        Assert.Equal(
            ["gitlab.example.test", "gitlab.example.test/team"],
            store.List().Select(entry => entry.Scope));
    }

    [Fact]
    public void CredentialStore_WrittenByDotquay_ReadsBackWithoutBom()
    {
        var path = _fileSystem.CombinePath(_root, TomlCredentialStore.FileName);
        var store = new TomlCredentialStore(_fileSystem, path);
        var tricky = new Credential("bot", "fake\ttoken\"with\\escapes\u0001");

        store.Set(CredentialScope.Parse("gitlab.example.test"), tricky);

        Assert.Equal(tricky, new TomlCredentialStore(_fileSystem, path).Find(
            CredentialScope.Parse("gitlab.example.test")));
        var bytes = File.ReadAllBytes(path);
        Assert.False(bytes is [0xEF, 0xBB, 0xBF, ..], "the credential file must not start with a BOM");
    }

    [Fact]
    public void CredentialStore_NewerFileVersion_IsStillRefused()
    {
        var path = WriteFixture(TomlCredentialStore.FileName, "version = 2\n", withBom: false, crlf: false);

        var error = Assert.Throws<CredentialsFileException>(
            () => new TomlCredentialStore(_fileSystem, path).List());

        Assert.Contains("newer dotquay", error.Message);
    }

    [Fact]
    public void UserConfig_HandWrittenFile_ReadsEverySetting()
    {
        const string Text = """
            # dotquay settings
            [update]
            channel = "https://releases.example.test/dotquay/"
            check = false
            interval_hours = 48
            """;
        var dotquayHome = _fileSystem.CombinePath(_root, ".dotquay");
        Directory.CreateDirectory(dotquayHome);
        WriteFixture(Path.Combine(".dotquay", TomlUserConfigStore.FileName), Text, withBom: true, crlf: true);
        var store = new TomlUserConfigStore(_fileSystem, new FakeEnvironmentReader(homeDirectory: _root));

        var settings = store.Read();

        Assert.Equal(new UpdateSettings("https://releases.example.test/dotquay", false, 48), settings);
    }

    [Fact]
    public void UserConfig_WrittenByDotquay_ReadsBackUnchanged()
    {
        Directory.CreateDirectory(_fileSystem.CombinePath(_root, ".dotquay"));
        var environment = new FakeEnvironmentReader(homeDirectory: _root);
        var store = new TomlUserConfigStore(_fileSystem, environment);

        store.SetNoticeEnabled(false);

        Assert.Equal(
            UpdateSettings.Defaults with { NoticeEnabled = false },
            new TomlUserConfigStore(_fileSystem, environment).Read());
    }

    [Fact]
    public void PluginCfg_GodotFile_ReadsVersion()
    {
        const string Text = """
            [plugin]

            name="Widget"
            description="A widget addon"
            author="Example"
            version="1.4.2"
            script="plugin.gd"
            """;
        var path = WriteFixture("plugin.cfg", Text, withBom: true, crlf: true);

        Assert.Equal("1.4.2", PluginCfgReader.TryReadVersion(_fileSystem, path));
    }

    [Fact]
    public void PluginCfg_GodotOnlySyntax_ReadsAsNoVersion()
    {
        // Godot's ConfigFile is not TOML: a Variant constructor is a parse error, which has always
        // meant "no version to compare" rather than a failure.
        var path = WriteFixture(
            "plugin.cfg",
            "[plugin]\nversion=\"1.0\"\nicons=PackedStringArray(\"a\", \"b\")\n",
            withBom: false,
            crlf: false);

        Assert.Null(PluginCfgReader.TryReadVersion(_fileSystem, path));
    }

    [Fact]
    public void InvalidToml_EveryReaderNamesTheFile()
    {
        const string Broken = "[addons.widget\ngit = \"x\"\n";
        var manifestPath = WriteFixture("dotquay.toml", Broken, withBom: false, crlf: false);
        var lockPath = WriteFixture("dotquay.lock", Broken, withBom: false, crlf: false);
        WriteFixture(LocalLinks.FileName, Broken, withBom: false, crlf: false);
        var credentialPath = WriteFixture(TomlCredentialStore.FileName, Broken, withBom: false, crlf: false);
        var configPath = WriteFixture("config.toml", Broken, withBom: false, crlf: false);
        var linksPath = _fileSystem.CombinePath(_root, LocalLinks.FileName);

        AssertNamesFile(
            manifestPath, Assert.Throws<ManifestParseException>(() => ManifestReader.ReadProject(Broken, manifestPath)));
        AssertNamesFile(
            lockPath, Assert.Throws<LockfileParseException>(() => LockfileReader.Read(Broken, lockPath)));
        AssertNamesFile(
            linksPath, Assert.Throws<LocalLinksParseException>(() => new LocalLinks(_fileSystem).ReadAll(_root)));
        AssertNamesFile(
            credentialPath,
            Assert.Throws<CredentialsFileException>(() => new TomlCredentialStore(_fileSystem, credentialPath).List()));
        AssertNamesFile(
            configPath,
            Assert.Throws<UserConfigFileException>(
                () => new TomlUserConfigStore(_fileSystem, new FakeEnvironmentReader(), configPath).Read()));
    }

    [Fact]
    public void DotquayToml_EmptyDocument_IsAnEmptyTable()
    {
        Assert.Empty(DotquayToml.Parse(string.Empty, "empty.toml"));
    }

    [Fact]
    public void DotquayToml_SyntaxError_CarriesTheSourceNameInItsPosition()
    {
        var error = Assert.Throws<TomlException>(() => DotquayToml.Parse("a = ", "named.toml"));

        Assert.Contains("named.toml", error.Message);
    }

    private static void AssertNamesFile(string path, Exception error)
    {
        // Once in the reader's own prefix and once in Tomlyn's position, which comes from the
        // SourceName the reader passes in.
        var first = error.Message.IndexOf(path, StringComparison.Ordinal);
        Assert.True(first >= 0, $"'{error.Message}' does not name {path}");
        Assert.True(
            error.Message.IndexOf(path, first + path.Length, StringComparison.Ordinal) >= 0,
            $"Tomlyn's position in '{error.Message}' does not name {path}");
    }

    private string WriteFixture(string relativePath, string text, bool withBom, bool crlf)
    {
        var path = Path.Combine(_root, relativePath);
        var normalized = text.ReplaceLineEndings(crlf ? "\r\n" : "\n");
        File.WriteAllText(path, normalized, new UTF8Encoding(encoderShouldEmitUTF8Identifier: withBom));
        return path;
    }

    private sealed class LockedAddonComparer : IEqualityComparer<LockedAddon>
    {
        public static readonly LockedAddonComparer Instance = new();

        public bool Equals(LockedAddon? x, LockedAddon? y) =>
            x is not null && y is not null
            && x.Name == y.Name && x.Git == y.Git && x.Resolved == y.Resolved && x.Version == y.Version
            && x.Path == y.Path && x.Folder == y.Folder && x.ContentHash == y.ContentHash
            && x.PatchHash == y.PatchHash && x.PatchBaseSha == y.PatchBaseSha
            && x.Dependencies.SequenceEqual(y.Dependencies);

        public int GetHashCode(LockedAddon obj) => obj.Name.GetHashCode(StringComparison.Ordinal);
    }
}
