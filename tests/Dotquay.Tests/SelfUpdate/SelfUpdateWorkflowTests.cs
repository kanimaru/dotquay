using Dotquay.Core.SelfUpdate;
using Dotquay.Core.Workflows;
using Dotquay.Tests.Support;
using Xunit;

namespace Dotquay.Tests.SelfUpdate;

/// <summary>
/// The self-updater's behaviour, all of it against fakes (SPEC.md §12).
/// </summary>
/// <remarks>
/// Every assertion here is about a case that is either impossible or ruinous to reproduce against a
/// real install: a download that does not match its checksum, a binary that will not start, a
/// filesystem that refuses the rename halfway through. What this suite cannot see is equally worth
/// stating — whether a real NativeAOT binary can serialize the new <c>--json</c> payloads, and
/// whether Windows really does let a running image be renamed. Both are verified by running the
/// published binary (docs/DESIGN-v0.17-SELF-UPDATE.md), not here.
/// </remarks>
public class SelfUpdateWorkflowTests {
    private const string Executable = "/home/kani/.local/bin/dotquay";
    private const string Rid = "linux-x64";

    [Fact]
    public async Task Check_reports_a_newer_release() {
        var (workflow, _, channel) = Build("0.17.0");
        channel.Publish("0.18.0", Rid);

        var result = await workflow.CheckAsync();

        Assert.True(result.UpdateAvailable);
        Assert.Equal("0.18.0", result.LatestVersion);
        Assert.Equal("0.17.0", result.CurrentVersion);
        Assert.Equal(Executable, result.ExecutablePath);
        Assert.Equal(new[] { Rid }, result.AvailableRids);
    }

    [Fact]
    public async Task Check_on_the_newest_release_reports_nothing_to_do() {
        var (workflow, _, channel) = Build("0.18.0");
        channel.Publish("0.18.0", Rid);

        Assert.False((await workflow.CheckAsync()).UpdateAvailable);
    }

    [Fact]
    public async Task Check_on_a_channel_with_no_releases_says_so_rather_than_up_to_date() {
        var (workflow, _, _) = Build("0.17.0");

        var result = await workflow.CheckAsync();

        Assert.Null(result.LatestVersion);
        Assert.False(result.UpdateAvailable);
    }

    [Fact]
    public async Task Update_replaces_the_executable_and_keeps_the_old_one() {
        var (workflow, fileSystem, channel) = Build("0.17.0");
        channel.Publish("0.18.0", Rid);

        var result = await workflow.UpdateAsync(new SelfUpdateOptions());

        Assert.True(result.Changed);
        Assert.Equal("0.18.0", result.ToVersion);
        Assert.Equal($"dotquay 0.18.0 for {Rid}", fileSystem.ReadAllText(Executable));

        // The retired binary is the rollback target, so it must both exist and still be the old one.
        Assert.Equal("/home/kani/.local/bin/dotquay.old-0.17.0", result.PreviousBinary);
        Assert.Equal("dotquay 0.17.0 installed", fileSystem.ReadAllText(result.PreviousBinary!));

        // Nothing half-finished is left lying beside it.
        Assert.False(fileSystem.FileExists("/home/kani/.local/bin/dotquay.new-0.18.0"));

        // A downloaded binary arrives as plain bytes with no mode at all.
        Assert.Contains("home/kani/.local/bin/dotquay", fileSystem.ExecutableFiles);
    }

    [Fact]
    public async Task Update_when_already_current_does_nothing_and_downloads_nothing() {
        var (workflow, fileSystem, channel) = Build("0.18.0");
        channel.Publish("0.18.0", Rid);

        var result = await workflow.UpdateAsync(new SelfUpdateOptions());

        Assert.False(result.Changed);
        Assert.Equal(0, channel.Downloads);
        Assert.Equal("dotquay 0.18.0 installed", fileSystem.ReadAllText(Executable));
    }

    [Fact]
    public async Task Update_ahead_of_the_channel_says_so_rather_than_downgrading_silently() {
        var (workflow, _, channel) = Build("0.19.0");
        channel.Publish("0.18.0", Rid);

        var result = await workflow.UpdateAsync(new SelfUpdateOptions());

        Assert.False(result.Changed);
        Assert.Contains(result.Messages, message => message.Contains("ahead of the newest release"));
    }

    [Fact]
    public async Task Update_to_an_older_version_is_allowed_and_says_it_is_a_downgrade() {
        var (workflow, fileSystem, channel) = Build("0.18.0");
        channel.Publish("0.17.5", Rid);
        channel.Publish("0.18.0", Rid);

        var result = await workflow.UpdateAsync(new SelfUpdateOptions(ToVersion: "0.17.5"));

        Assert.True(result.Changed);
        Assert.Contains(result.Messages, message => message.Contains("Downgrading from 0.18.0 to 0.17.5"));
        Assert.Equal($"dotquay 0.17.5 for {Rid}", fileSystem.ReadAllText(Executable));
    }

    [Fact]
    public async Task Update_to_a_version_that_does_not_exist_names_it() {
        var (workflow, _, channel) = Build("0.18.0");
        channel.Publish("0.18.0", Rid);

        var error = await Assert.ThrowsAsync<WorkflowException>(
            () => workflow.UpdateAsync(new SelfUpdateOptions(ToVersion: "9.9.9")));

        Assert.Contains("9.9.9", error.Message);
    }

    [Fact]
    public async Task Dry_run_writes_nothing_and_downloads_nothing() {
        var (workflow, fileSystem, channel) = Build("0.17.0");
        channel.Publish("0.18.0", Rid);

        var result = await workflow.UpdateAsync(new SelfUpdateOptions(DryRun: true));

        Assert.False(result.Changed);
        Assert.Equal(0, channel.Downloads);
        Assert.Equal("dotquay 0.17.0 installed", fileSystem.ReadAllText(Executable));
        Assert.Contains(result.Messages, message => message.Contains("Would download"));
    }

    [Fact]
    public async Task A_checksum_mismatch_installs_nothing() {
        var (workflow, fileSystem, channel) = Build("0.17.0");
        channel.Publish("0.18.0", Rid).Corrupt("0.18.0");

        var error = await Assert.ThrowsAsync<WorkflowException>(() => workflow.UpdateAsync(new SelfUpdateOptions()));

        Assert.Contains("does not match the checksum", error.Message);
        Assert.Equal("dotquay 0.17.0 installed", fileSystem.ReadAllText(Executable));
        Assert.False(fileSystem.FileExists("/home/kani/.local/bin/dotquay.new-0.18.0"));
    }

    [Fact]
    public async Task A_release_without_a_checksum_is_refused_rather_than_trusted() {
        var (workflow, fileSystem, channel) = Build("0.17.0");
        channel.PublishWithoutChecksum("0.18.0", Rid);

        var error = await Assert.ThrowsAsync<WorkflowException>(() => workflow.UpdateAsync(new SelfUpdateOptions()));

        Assert.Contains("no checksum", error.Message);
        Assert.Equal(0, channel.Downloads);
        Assert.Equal("dotquay 0.17.0 installed", fileSystem.ReadAllText(Executable));
    }

    [Fact]
    public async Task A_binary_that_does_not_start_is_never_installed() {
        var (workflow, fileSystem, channel, probe) = BuildWithProbe("0.17.0");
        channel.Publish("0.18.0", Rid);
        probe.Reports("/home/kani/.local/bin/dotquay.new-0.18.0", null);

        var error = await Assert.ThrowsAsync<WorkflowException>(() => workflow.UpdateAsync(new SelfUpdateOptions()));

        Assert.Contains("does not start", error.Message);
        Assert.Equal("dotquay 0.17.0 installed", fileSystem.ReadAllText(Executable));
        Assert.False(fileSystem.FileExists("/home/kani/.local/bin/dotquay.new-0.18.0"));
    }

    [Fact]
    public async Task A_binary_reporting_the_wrong_version_is_never_installed() {
        var (workflow, fileSystem, channel, probe) = BuildWithProbe("0.17.0");
        channel.Publish("0.18.0", Rid);
        probe.Reports("/home/kani/.local/bin/dotquay.new-0.18.0", "0.4.4");

        var error = await Assert.ThrowsAsync<WorkflowException>(() => workflow.UpdateAsync(new SelfUpdateOptions()));

        Assert.Contains("reports version '0.4.4'", error.Message);
        Assert.Equal("dotquay 0.17.0 installed", fileSystem.ReadAllText(Executable));
    }

    [Fact]
    public async Task A_release_with_no_build_for_this_platform_names_what_it_does_publish() {
        var (workflow, _, channel) = Build("0.17.0");
        channel.Publish("0.18.0", "win-x64");

        var error = await Assert.ThrowsAsync<WorkflowException>(() => workflow.UpdateAsync(new SelfUpdateOptions()));

        Assert.Contains("no build for linux-x64", error.Message);
        Assert.Contains("win-x64", error.Message);
    }

    [Fact]
    public async Task A_build_output_is_refused_unless_forced() {
        var fileSystem = new InMemoryFileSystem();
        var path = "/repo/src/Dotquay.Cli/bin/Debug/net10.0/dotquay";
        fileSystem.WriteAllBytes(path, "dotquay 0.9.0 installed"u8.ToArray());

        var channel = new FakeReleaseChannel().Publish("0.18.0", Rid);
        var workflow = new SelfUpdateWorkflow(fileSystem, channel, new FakeExecutableProbe(), path, "0.9.0") {
            RuntimeIdentifier = Rid,
        };

        var error = await Assert.ThrowsAsync<WorkflowException>(() => workflow.UpdateAsync(new SelfUpdateOptions()));
        Assert.Contains("looks like a build output", error.Message);

        // --force is the documented escape hatch, and it must reach the actual install.
        var forced = await workflow.UpdateAsync(new SelfUpdateOptions(Force: true));
        Assert.True(forced.Changed);
    }

    [Fact]
    public async Task An_install_directory_called_bin_is_not_mistaken_for_a_build_output() {
        // ~/.local/bin is where the install scripts put dotquay. A guard that fired here would fire
        // on the most common correct installation there is.
        var (workflow, _, channel) = Build("0.17.0");
        channel.Publish("0.18.0", Rid);

        Assert.True((await workflow.UpdateAsync(new SelfUpdateOptions())).Changed);
    }

    [Fact]
    public async Task A_directory_this_process_cannot_write_to_is_explained_not_attempted() {
        var (workflow, fileSystem, channel) = Build("0.17.0");
        channel.Publish("0.18.0", Rid);
        fileSystem.ReadOnlyDirectories.Add("/home/kani/.local/bin");

        var error = await Assert.ThrowsAsync<WorkflowException>(() => workflow.UpdateAsync(new SelfUpdateOptions()));

        Assert.Contains("cannot write to", error.Message);
        Assert.Equal(0, channel.Downloads);
    }

    [Fact]
    public async Task Rollback_restores_the_previous_binary_and_keeps_the_one_it_replaced() {
        var (workflow, fileSystem, channel) = Build("0.17.0");
        channel.Publish("0.18.0", Rid);
        await workflow.UpdateAsync(new SelfUpdateOptions());

        // A new workflow, because the binary now running is the one that was just installed.
        var updated = new SelfUpdateWorkflow(fileSystem, channel, new FakeExecutableProbe(), Executable, "0.18.0") {
            RuntimeIdentifier = Rid,
        };

        var result = await updated.RollbackAsync();

        Assert.True(result.Changed);
        Assert.Equal("0.17.0", result.ToVersion);
        Assert.Equal("dotquay 0.17.0 installed", fileSystem.ReadAllText(Executable));

        // Rolling back must be reversible in turn: what it replaced is kept, under its own version.
        Assert.Equal("/home/kani/.local/bin/dotquay.old-0.18.0", result.PreviousBinary);
        Assert.False(fileSystem.FileExists("/home/kani/.local/bin/dotquay.old-0.17.0"));
    }

    [Fact]
    public async Task Rollback_with_nothing_to_roll_back_to_says_so() {
        var (workflow, _, _) = Build("0.17.0");

        var error = await Assert.ThrowsAsync<WorkflowException>(() => workflow.RollbackAsync());

        Assert.Contains("no previous dotquay", error.Message);
    }

    [Fact]
    public async Task Rollback_to_a_binary_that_no_longer_starts_is_refused() {
        var (workflow, fileSystem, channel) = Build("0.17.0");
        channel.Publish("0.18.0", Rid);
        await workflow.UpdateAsync(new SelfUpdateOptions());

        var probe = new FakeExecutableProbe().Reports("/home/kani/.local/bin/dotquay.old-0.17.0", null);
        var updated = new SelfUpdateWorkflow(fileSystem, channel, probe, Executable, "0.18.0") {
            RuntimeIdentifier = Rid,
        };

        var error = await Assert.ThrowsAsync<WorkflowException>(() => updated.RollbackAsync());

        Assert.Contains("does not start", error.Message);
        Assert.Equal($"dotquay 0.18.0 for {Rid}", fileSystem.ReadAllText(Executable));
    }

    [Fact]
    public async Task An_update_sweeps_what_an_earlier_failed_attempt_left_behind() {
        var (workflow, fileSystem, channel) = Build("0.17.0");
        channel.Publish("0.18.0", Rid);
        fileSystem.WriteAllText("/home/kani/.local/bin/dotquay.new-0.17.9", "a download rejected last week");

        await workflow.UpdateAsync(new SelfUpdateOptions());

        Assert.False(fileSystem.FileExists("/home/kani/.local/bin/dotquay.new-0.17.9"));
    }

    [Fact]
    public async Task Only_one_retired_binary_is_kept() {
        var (workflow, fileSystem, channel) = Build("0.17.0");
        channel.Publish("0.18.0", Rid);
        await workflow.UpdateAsync(new SelfUpdateOptions());

        channel.Publish("0.19.0", Rid);
        var second = new SelfUpdateWorkflow(fileSystem, channel, new FakeExecutableProbe(), Executable, "0.18.0") {
            RuntimeIdentifier = Rid,
        };
        await second.UpdateAsync(new SelfUpdateOptions());

        var retired = new ExecutableSwapper(fileSystem).Retired(Executable);

        Assert.Single(retired);
        Assert.Equal("0.18.0", retired[0].Version);
    }

    private static (SelfUpdateWorkflow Workflow, InMemoryFileSystem FileSystem, FakeReleaseChannel Channel) Build(
        string currentVersion) {
        var (workflow, fileSystem, channel, _) = BuildWithProbe(currentVersion);
        return (workflow, fileSystem, channel);
    }

    private static (SelfUpdateWorkflow Workflow, InMemoryFileSystem FileSystem, FakeReleaseChannel Channel, FakeExecutableProbe Probe)
        BuildWithProbe(string currentVersion) {
        var fileSystem = new InMemoryFileSystem();
        fileSystem.WriteAllText(Executable, $"dotquay {currentVersion} installed");

        var channel = new FakeReleaseChannel();
        var probe = new FakeExecutableProbe();

        return (
            new SelfUpdateWorkflow(fileSystem, channel, probe, Executable, currentVersion) { RuntimeIdentifier = Rid },
            fileSystem,
            channel,
            probe);
    }
}
