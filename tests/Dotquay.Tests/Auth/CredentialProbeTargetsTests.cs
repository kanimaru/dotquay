using Dotquay.Core.Auth;
using Dotquay.Core.Manifest;
using Xunit;

namespace Dotquay.Tests.Auth;

public class CredentialProbeTargetsTests
{
    private const string FeedUrl = "https://gitlab.example.com/api/v4/projects/124/packages/nuget/index.json";
    private const string OtherFeedUrl = "https://gitlab.example.com/api/v4/projects/9/packages/nuget/index.json";
    private const string RemoteUrl = "https://gitlab.example.com/kani/widget.git";

    [Fact]
    public void For_HostWideScope_PicksTheProjectsFeedsAndRemotesOnThatHost()
    {
        var manifest = new ProjectManifest
        {
            NuGetSources =
            [
                new NuGetSourceSpec("vbeami", FeedUrl),
                new NuGetSourceSpec("elsewhere", "https://pkgs.example.org/v3/index.json"),
                NuGetSourceSpec.Public,
            ],
            Addons = new Dictionary<string, AddonSpec>
            {
                ["widget"] = new() { Name = "widget", Git = RemoteUrl },
                ["github"] = new() { Name = "github", Git = "https://github.com/someone/thing.git" },
                ["ssh"] = new() { Name = "ssh", Git = "git@gitlab.example.com:kani/private.git" },
            },
        };

        var targets = CredentialProbeTargets.For(CredentialScope.Parse("gitlab.example.com"), manifest);

        Assert.Equal(
            [FeedUrl, "https://gitlab.example.com/kani/widget.git/info/refs?service=git-upload-pack"],
            targets);
    }

    [Fact]
    public void For_FeedsComeBeforeRemotes_WhateverTheManifestOrder()
    {
        var manifest = new ProjectManifest
        {
            NuGetSources = [new NuGetSourceSpec("vbeami", FeedUrl)],
            Addons = new Dictionary<string, AddonSpec> { ["widget"] = new() { Name = "widget", Git = RemoteUrl } },
        };

        var targets = CredentialProbeTargets.For(CredentialScope.Parse("gitlab.example.com"), manifest);

        Assert.Equal(FeedUrl, targets[0]);
    }

    [Fact]
    public void For_PathScope_OnlyCoversUrlsUnderThatPath()
    {
        var manifest = new ProjectManifest
        {
            NuGetSources = [new NuGetSourceSpec("vbeami", FeedUrl), new NuGetSourceSpec("other", OtherFeedUrl)],
        };

        var targets = CredentialProbeTargets.For(
            CredentialScope.Parse("gitlab.example.com/api/v4/projects/124"), manifest);

        Assert.Equal([FeedUrl], targets);
    }

    [Fact]
    public void For_NothingOnThatHost_IsEmpty()
    {
        var manifest = new ProjectManifest
        {
            NuGetSources = [new NuGetSourceSpec("vbeami", FeedUrl)],
            Addons = new Dictionary<string, AddonSpec> { ["widget"] = new() { Name = "widget", Git = RemoteUrl } },
        };

        Assert.Empty(CredentialProbeTargets.For(CredentialScope.Parse("gitlab.other.com"), manifest));
    }

    [Fact]
    public void For_TwoAddonsFromOneRemote_ProbeItOnce()
    {
        var manifest = new ProjectManifest
        {
            Addons = new Dictionary<string, AddonSpec>
            {
                ["a"] = new() { Name = "a", Git = RemoteUrl, Path = "addons/a" },
                ["b"] = new() { Name = "b", Git = RemoteUrl + "/", Path = "addons/b" },
            },
        };

        var targets = CredentialProbeTargets.For(CredentialScope.Parse("gitlab.example.com"), manifest);

        Assert.Single(targets);
    }
}
