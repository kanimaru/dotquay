using Dotquay.Core.Abstractions;
using Dotquay.Core.Diagnostics;
using Dotquay.Core.IO;
using Dotquay.Core.Manifest;
using Dotquay.Core.Progress;
using Dotquay.Core.Resolve;
using Dotquay.Core.Sync;
using Dotquay.Tests.Support;
using Xunit;

namespace Dotquay.Tests.Diagnostics;

/// <summary>
/// The Core call sites that report to the diagnostic log do so with the content a debugger needs -
/// one test per kind of site, so a refactor that silently drops a line fails here.
/// </summary>
public sealed class InstrumentedCallSiteTests
{
    [Fact]
    public async Task Resolver_says_which_tag_a_constraint_matched()
    {
        var git = new FakeGitClient();
        git.RemoteRefsByUrl["https://example.com/foo"] =
        [
            new GitRef("v1.0.0", "sha-a", GitRefKind.Tag),
            new GitRef("v1.2.0", "sha-b", GitRefKind.Tag),
            new GitRef("main", "sha-c", GitRefKind.Branch),
        ];

        var sink = RecordingDiagnosticSink.Install(out var scope);
        using (scope)
        {
            await new AddonResolver(git).ResolveAsync(
                new AddonSpec { Name = "foo", Git = "https://example.com/foo", Version = "~1.2" });
        }

        Assert.Equal(["foo: '~1.2' against 2 tag(s) -> v1.2.0 (sha-b)"], sink.Messages("resolve"));
    }

    [Fact]
    public async Task Resolver_says_when_nothing_matched_before_it_throws()
    {
        var git = new FakeGitClient();
        git.RemoteRefsByUrl["https://example.com/foo"] = [new GitRef("v1.0.0", "sha-a", GitRefKind.Tag)];

        var sink = RecordingDiagnosticSink.Install(out var scope);
        using (scope)
        {
            await Assert.ThrowsAsync<AddonResolutionException>(() => new AddonResolver(git).ResolveAsync(
                new AddonSpec { Name = "foo", Git = "https://example.com/foo", Version = "~2.0" }));
        }

        Assert.Equal(["foo: '~2.0' against 1 tag(s) -> no match"], sink.Messages("resolve"));
    }

    [Fact]
    public async Task Resolver_logs_branch_tips_and_rev_pins()
    {
        var git = new FakeGitClient();
        git.BranchTips[("https://example.com/foo", "godot4")] = "deadbeef";

        var sink = RecordingDiagnosticSink.Install(out var scope);
        using (scope)
        {
            var resolver = new AddonResolver(git);
            await resolver.ResolveAsync(new AddonSpec { Name = "foo", Git = "https://example.com/foo", Branch = "godot4" });
            await resolver.ResolveAsync(new AddonSpec { Name = "bar", Git = "https://example.com/bar", Rev = "9f2c1ab" });
        }

        Assert.Equal(
            ["foo: branch 'godot4' tip is deadbeef", "bar: rev pin 9f2c1ab, no lookup needed"],
            sink.Messages("resolve"));
    }

    [Fact]
    public async Task Repo_cache_reports_a_miss_then_a_hit_with_credentials_redacted()
    {
        var fileSystem = new InMemoryFileSystem();
        var git = new FakeGitClient { FileSystemForFetchedContent = fileSystem };
        const string url = "https://oauth2:secret@example.com/foo.git";
        git.FetchedFilesByUrl[url] = new Dictionary<string, string> { ["addons/foo/a.gd"] = "x" };
        var cache = new RepoCache(fileSystem, git, "/cache");

        var sink = RecordingDiagnosticSink.Install(out var scope);
        using (scope)
        {
            await cache.GetOrFetchAsync(url, "0123456789abcdef");
            await cache.GetOrFetchAsync(url, "0123456789abcdef");
        }

        var lines = sink.Messages("cache").ToList();
        Assert.Equal(2, lines.Count);
        Assert.StartsWith("miss https://oauth2:***@example.com/foo.git@0123456789, fetching into ", lines[0]);
        Assert.StartsWith("hit https://oauth2:***@example.com/foo.git@0123456789 -> ", lines[1]);
        Assert.DoesNotContain("secret", sink.AllText);
    }

    [Fact]
    public void System_file_system_logs_destructive_operations_at_verbose_and_writes_at_trace()
    {
        var root = Directory.CreateTempSubdirectory("dotquay-diag-fs-").FullName;
        try
        {
            var fileSystem = new SystemFileSystem();
            var tree = Path.Combine(root, "tree");
            var moved = Path.Combine(root, "moved");

            var verbose = RecordingDiagnosticSink.Install(out var verboseScope, DiagnosticLevel.Verbose);
            using (verboseScope)
            {
                fileSystem.WriteAllText(Path.Combine(tree, "a.txt"), "hello");
                fileSystem.MoveDirectory(tree, moved);
                fileSystem.DeleteDirectory(moved);
            }

            var verboseLines = verbose.Messages("fs").ToList();
            Assert.Equal($"move tree {tree} -> {moved}", verboseLines[0]);
            Assert.Equal($"delete tree {moved}", verboseLines[^1]);
            Assert.DoesNotContain(verboseLines, l => l.StartsWith("write ", StringComparison.Ordinal));

            var trace = RecordingDiagnosticSink.Install(out var traceScope);
            using (traceScope)
            {
                fileSystem.WriteAllText(Path.Combine(root, "b.txt"), "hello");
            }

            Assert.Equal([$"write {Path.Combine(root, "b.txt")} (5 chars)"], trace.Messages("fs"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Progress_steps_are_copied_into_the_log_and_still_reach_the_inner_reporter()
    {
        var inner = new ListProgressReporter();

        var sink = RecordingDiagnosticSink.Install(out var scope);
        using (scope)
        {
            var reporter = DiagnosticProgressReporter.Wrap(inner);
            reporter.Report("fetching foo");
            reporter.Report("installing", 2, 5);
        }

        Assert.Equal(["fetching foo", "[2/5] installing"], sink.Messages("step"));
        Assert.Equal(["fetching foo", "installing"], inner.Updates.Select(u => u.Message));
    }

    [Fact]
    public void Progress_wrapper_is_not_applied_when_off()
    {
        var inner = new ListProgressReporter();

        Assert.Same(inner, DiagnosticProgressReporter.Wrap(inner));
    }

    private sealed class ListProgressReporter : IProgressReporter
    {
        public List<ProgressUpdate> Updates { get; } = [];

        public void Report(ProgressUpdate update) => Updates.Add(update);
    }
}
