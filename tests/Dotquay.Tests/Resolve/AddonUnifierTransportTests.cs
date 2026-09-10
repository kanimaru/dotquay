using Dotquay.Core.Manifest;
using Dotquay.Core.Resolve;
using Dotquay.Tests.Support;
using Xunit;

namespace Dotquay.Tests.Resolve;

/// <summary>
/// Covers unification's "required from different git repositories" check now comparing repositories
/// rather than URL strings (v0.15).
/// </summary>
/// <remarks>
/// This is the half of the transport problem that <see cref="Dotquay.Core.Git.GitTransportPreference"/>
/// deliberately declines to solve. On a host the project never described -- or described two ways --
/// nothing is rewritten, so two requirers can still arrive spelling one repository differently. Before
/// this, that was a hard error whose suggested fix (add an <c>[overrides]</c> entry) asked the user to
/// pick a winner between two identical things.
/// </remarks>
public class AddonUnifierTransportTests
{
    private static string Sha => new('c', 40);

    private static AddonSpec Pinned(string url) =>
        new() { Name = "gut", Git = url, Rev = Sha, Path = "addons/gut" };

    private static Task<UnificationOutcome> UnifyAsync(params string[] urls) =>
        new AddonUnifier(new FakeGitClient()).UnifyAsync(
            "gut",
            urls.Select((url, i) => new AddonRequirement($"requirer-{i}", Pinned(url))).ToList());

    [Fact]
    public async Task SameRepositoryDifferentTransports_Unifies()
    {
        var outcome = await UnifyAsync("https://github.com/bitwes/gut", "git@github.com:bitwes/gut.git");

        Assert.True(outcome.IsResolved);
        Assert.Equal(Sha, outcome.Resolved.ResolvedSha);
    }

    [Fact]
    public async Task SameRepositoryDifferentSpelling_Unifies()
    {
        // Trailing slash, .git suffix, redundant default port, host casing: four ways to write one
        // URL, none of which is a disagreement about anything.
        var outcome = await UnifyAsync(
            "https://GitHub.com/bitwes/gut.git",
            "https://github.com:443/bitwes/gut/");

        Assert.True(outcome.IsResolved);
    }

    [Fact]
    public async Task FirstDeclaredSpelling_IsWhatGetsLocked()
    {
        // Consistent with how the unifier already treats path and install_as: first requirement wins,
        // so the outcome depends only on the graph, not on dictionary ordering luck.
        var outcome = await UnifyAsync("git@github.com:bitwes/gut.git", "https://github.com/bitwes/gut");

        Assert.Equal("git@github.com:bitwes/gut.git", outcome.Resolved.Git);
    }

    [Fact]
    public async Task GenuinelyDifferentRepositories_StillConflict()
    {
        // The check has to keep working. A fork on a different host is exactly what it exists to
        // catch, and transport-blindness must not blur that.
        var outcome = await UnifyAsync("https://github.com/bitwes/gut", "https://gitlab.com/someone/gut");

        Assert.False(outcome.IsResolved);
        Assert.Equal(GraphConflictKind.DifferentGitUrls, outcome.Conflict.Kind);
    }

    [Fact]
    public async Task SameHostDifferentPaths_StillConflict()
    {
        var outcome = await UnifyAsync("https://github.com/bitwes/gut", "https://github.com/afork/gut");

        Assert.False(outcome.IsResolved);
        Assert.Equal(GraphConflictKind.DifferentGitUrls, outcome.Conflict.Kind);
    }

    [Fact]
    public async Task ALocalPathAndTheRemoteItMirrors_StillConflict()
    {
        // Not a false positive to fix. A working copy and its origin are different sources of truth,
        // and silently treating one as the other is how an uncommitted edit becomes a locked
        // dependency.
        var outcome = await UnifyAsync(@"C:\repos\gut", "https://github.com/bitwes/gut");

        Assert.False(outcome.IsResolved);
    }
}
