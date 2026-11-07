using Dotquay.Core.Auth;
using Dotquay.Tests.Support;
using Xunit;

namespace Dotquay.Tests.Auth;

public class TomlCredentialStoreTests
{
    private const string Path = "/home/kani/.dotquay/credentials.toml";

    private static (TomlCredentialStore Store, InMemoryFileSystem FileSystem) Create()
    {
        var fileSystem = new InMemoryFileSystem();
        return (new TomlCredentialStore(fileSystem, Path), fileSystem);
    }

    [Fact]
    public void Find_MissingFile_ReturnsNullWithoutThrowing()
    {
        var (store, _) = Create();

        Assert.Null(store.Find(CredentialScope.Parse("gitlab.example.com")));
    }

    [Fact]
    public void SetThenFind_RoundTripsThroughRealToml()
    {
        var (store, _) = Create();

        store.Set(CredentialScope.Parse("gitlab.example.com"), new Credential("kani", "glpat-abc123"));

        var found = store.Find(CredentialScope.Parse("https://gitlab.example.com/api/v4/projects/9"));
        Assert.Equal(new Credential("kani", "glpat-abc123"), found);
    }

    [Fact]
    public void Find_LongestMatchingPrefixWins()
    {
        var (store, _) = Create();
        store.Set(CredentialScope.Parse("gitlab.example.com"), new Credential("kani", "host-wide"));
        store.Set(CredentialScope.Parse("gitlab.example.com/api/v4/projects/123"), new Credential("ci", "narrow"));

        Assert.Equal("narrow", store.Find(CredentialScope.Parse("https://gitlab.example.com/api/v4/projects/123/packages/nuget/index.json"))!.Token);
        Assert.Equal("host-wide", store.Find(CredentialScope.Parse("https://gitlab.example.com/api/v4/projects/456/packages/nuget/index.json"))!.Token);
    }

    [Fact]
    public void Set_WritesOwnerOnlyPermissions_AfterTheFileExists()
    {
        var (store, fileSystem) = Create();

        store.Set(CredentialScope.Parse("gitlab.example.com"), new Credential("kani", "glpat-abc123"));

        // The fake only records the call when the file already exists, mirroring the real
        // implementation's early return — so this also proves the store doesn't chmod before writing.
        Assert.Contains(Path.TrimStart('/'), fileSystem.OwnerOnlyPaths);
    }

    [Fact]
    public void Set_ReplacesTheEntryForTheSameScope_RatherThanAppending()
    {
        var (store, _) = Create();
        var scope = CredentialScope.Parse("gitlab.example.com");

        store.Set(scope, new Credential("kani", "first"));
        store.Set(scope, new Credential("kani", "second"));

        Assert.Equal("second", store.Find(scope)!.Token);
        Assert.Single(store.List());
    }

    [Fact]
    public void Remove_MatchesTheExactScopeOnly_NeverByPrefix()
    {
        var (store, _) = Create();
        store.Set(CredentialScope.Parse("gitlab.example.com"), new Credential("kani", "host-wide"));
        store.Set(CredentialScope.Parse("gitlab.example.com/api/v4/projects/123"), new Credential("ci", "narrow"));

        Assert.True(store.Remove(CredentialScope.Parse("gitlab.example.com")));

        // Removing the host must not take the narrower entry with it.
        Assert.Equal("narrow", store.Find(CredentialScope.Parse("https://gitlab.example.com/api/v4/projects/123"))!.Token);
        Assert.False(store.Remove(CredentialScope.Parse("gitlab.example.com")));
    }

    [Fact]
    public void List_ReturnsScopeAndUsername_AndNoTokenAnywhere()
    {
        var (store, _) = Create();
        store.Set(CredentialScope.Parse("gitlab.example.com"), new Credential("kani", "glpat-supersecret"));

        var listed = Assert.Single(store.List());

        Assert.Equal("gitlab.example.com", listed.Scope);
        Assert.Equal("kani", listed.Username);
        Assert.DoesNotContain("glpat-supersecret", string.Join('\n', listed.Scope, listed.Username), StringComparison.Ordinal);
    }

    [Fact]
    public void Set_TokenContainingQuotesAndBackslashes_SurvivesTheRoundTrip()
    {
        var (store, _) = Create();
        var awkward = "a\"b\\c\td";

        store.Set(CredentialScope.Parse("gitlab.example.com"), new Credential("kani", awkward));

        Assert.Equal(awkward, store.Find(CredentialScope.Parse("gitlab.example.com"))!.Token);
    }

    [Fact]
    public void Read_FileFromANewerVersion_IsRefusedRatherThanHalfRead()
    {
        var (store, fileSystem) = Create();
        fileSystem.WriteAllText(Path, $"version = {TomlCredentialStore.CurrentVersion + 1}\n");

        var exception = Assert.Throws<CredentialsFileException>(() => store.List());
        Assert.Contains("newer dotquay", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Read_EntryMissingItsToken_SaysWhichScopeAndHowToFixIt()
    {
        var (store, fileSystem) = Create();
        fileSystem.WriteAllText(Path, "version = 1\n\n[credential.\"gitlab.example.com\"]\nusername = \"kani\"\n");

        var exception = Assert.Throws<CredentialsFileException>(() => store.List());
        Assert.Contains("dotquay login gitlab.example.com", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Read_InvalidToml_IsReportedWithTheFilePath()
    {
        var (store, fileSystem) = Create();
        fileSystem.WriteAllText(Path, "this is not = = toml");

        var exception = Assert.Throws<CredentialsFileException>(() => store.List());
        Assert.Contains(Path, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Write_HeaderIsPlainAscii()
    {
        var (store, fileSystem) = Create();
        store.Set(CredentialScope.Parse("gitlab.example.com"), new Credential("kani", "glpat-abc123"));

        // This file gets opened in whatever editor or shell is to hand, and non-ASCII punctuation
        // renders as mojibake in anything reading it as ANSI.
        Assert.All(fileSystem.ReadAllText(Path), c => Assert.True(c < 128, $"non-ASCII character U+{(int)c:X4}"));
    }

    [Fact]
    public void Set_EmptyUsernameOrToken_IsRejected()
    {
        var (store, _) = Create();
        var scope = CredentialScope.Parse("gitlab.example.com");

        Assert.Throws<CredentialException>(() => store.Set(scope, new Credential("kani", string.Empty)));
        Assert.Throws<CredentialException>(() => store.Set(scope, new Credential(string.Empty, "token")));
    }
}
