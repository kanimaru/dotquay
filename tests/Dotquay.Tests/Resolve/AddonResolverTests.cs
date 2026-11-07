using Dotquay.Core.Abstractions;
using Dotquay.Core.Manifest;
using Dotquay.Core.Resolve;
using Dotquay.Tests.Support;
using Xunit;

namespace Dotquay.Tests.Resolve;

public class AddonResolverTests {
    [Fact]
    public async Task ResolveAsync_Rev_PassesThroughWithoutTouchingGit() {
        var git = new FakeGitClient();
        var resolver = new AddonResolver(git);
        var spec = new AddonSpec { Name = "foo", Git = "https://example.com/foo", Rev = "9f2c1ab" };

        var resolved = await resolver.ResolveAsync(spec);

        Assert.Equal("9f2c1ab", resolved.ResolvedSha);
        Assert.Null(resolved.DisplayVersion);
        Assert.Empty(git.FetchCalls);
    }

    [Fact]
    public async Task ResolveAsync_Branch_ResolvesTipViaGitClient() {
        var git = new FakeGitClient();
        git.BranchTips[("https://example.com/foo", "godot4")] = "deadbeef";
        var resolver = new AddonResolver(git);
        var spec = new AddonSpec {
            Name = "foo",
            Git = "https://example.com/foo",
            Branch = "godot4"
        };

        var resolved = await resolver.ResolveAsync(spec);

        Assert.Equal("deadbeef", resolved.ResolvedSha);
        Assert.Null(resolved.DisplayVersion);
    }

    [Fact]
    public async Task ResolveAsync_Version_PicksHighestMatchingTag() {
        var git = new FakeGitClient();
        git.RemoteRefsByUrl["https://example.com/foo"] =
        [
            new GitRef("v2.44.0", "sha-a", GitRefKind.Tag),
            new GitRef("v2.44.1", "sha-b", GitRefKind.Tag),
            new GitRef("v2.45.0", "sha-c", GitRefKind.Tag), // outside ~2.44
        ];
        var resolver = new AddonResolver(git);
        var spec = new AddonSpec {
            Name = "foo",
            Git = "https://example.com/foo",
            Version = "~2.44"
        };

        var resolved = await resolver.ResolveAsync(spec);

        Assert.Equal("sha-b", resolved.ResolvedSha);
        Assert.Equal("v2.44.1", resolved.DisplayVersion);
    }

    [Fact]
    public async Task ResolveAsync_Version_ThrowsHelpfulErrorWhenRepoHasNoTags() {
        var git = new FakeGitClient();
        git.RemoteRefsByUrl["https://example.com/foo"] = [];
        var resolver = new AddonResolver(git);
        var spec = new AddonSpec {
            Name = "foo",
            Git = "https://example.com/foo",
            Version = "~2.44"
        };

        var ex = await Assert.ThrowsAsync<AddonResolutionException>(() =>
            resolver.ResolveAsync(spec));

        Assert.Contains("no tags at all", ex.Message);
        Assert.Contains("foo", ex.Message);
    }

    [Fact]
    public async Task ResolveAsync_Version_ThrowsHelpfulErrorWhenNoTagSatisfiesConstraint() {
        var git = new FakeGitClient();
        git.RemoteRefsByUrl["https://example.com/foo"] =
        [
            new GitRef("v1.0.0", "sha-a", GitRefKind.Tag),
        ];
        var resolver = new AddonResolver(git);
        var spec = new AddonSpec {
            Name = "foo",
            Git = "https://example.com/foo",
            Version = "~2.44"
        };

        var ex = await Assert.ThrowsAsync<AddonResolutionException>(() =>
            resolver.ResolveAsync(spec));

        Assert.Contains("no tag satisfies", ex.Message);
    }

    [Fact]
    public async Task ResolveAsync_Version_ThrowsOnInvalidConstraint() {
        var git = new FakeGitClient();
        var resolver = new AddonResolver(git);
        var spec = new AddonSpec {
            Name = "foo",
            Git = "https://example.com/foo",
            Version = "not-a-version"
        };

        await Assert.ThrowsAsync<AddonResolutionException>(() => resolver.ResolveAsync(spec));
    }
}
