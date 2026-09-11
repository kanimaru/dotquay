using Dotquay.Core.SelfUpdate;
using Dotquay.Tests.Support;
using Xunit;

namespace Dotquay.Tests.SelfUpdate;

public class VersionOrderTests
{
    [Theory]
    [InlineData("0.18.0", "0.17.9", true)]
    [InlineData("0.17.0", "0.17.0", false)]
    [InlineData("0.9.0", "0.10.0", false)] // the classic: 9 is not newer than 10
    [InlineData("1.0.0", "0.99.99", true)]
    [InlineData("v0.18.0", "0.17.0", true)]
    public void Compares_release_versions(string candidate, string current, bool newer) =>
        Assert.Equal(newer, VersionOrder.IsNewer(candidate, current));

    [Fact]
    public void A_merge_request_build_is_behind_every_release()
    {
        // CI stamps 0.0.0-ci-<sha> onto builds that are not releases. Someone running one must be
        // told about the release, not the other way round.
        Assert.True(VersionOrder.IsNewer("0.17.0", "0.0.0-ci-9f2c1ab"));
        Assert.False(VersionOrder.IsNewer("0.0.0-ci-9f2c1ab", "0.17.0"));
    }

    [Fact]
    public void An_unparseable_version_still_orders_rather_than_throwing()
    {
        // Reached from the background update check, which must never be able to fail a command.
        var exception = Record.Exception(() => VersionOrder.IsNewer("not-a-version", "also-not"));
        Assert.Null(exception);
    }
}

public class ExecutableSwapperTests
{
    [Fact]
    public void A_windows_binary_keeps_its_exe_extension_through_the_swap()
    {
        // Windows will not start a file without .exe, and the staged download is run with --version
        // before it is trusted - so the extension has to survive the naming scheme, not just the
        // final rename.
        var fileSystem = new InMemoryFileSystem();
        fileSystem.WriteAllText("C:/tools/dotquay.exe", "old");
        fileSystem.WriteAllText("C:/tools/dotquay.new-0.18.0.exe", "new");

        var retired = new ExecutableSwapper(fileSystem).Swap("C:/tools/dotquay.exe", "C:/tools/dotquay.new-0.18.0.exe", "0.17.0");

        Assert.Equal("C:/tools/dotquay.old-0.17.0.exe", retired);
        Assert.Equal("new", fileSystem.ReadAllText("C:/tools/dotquay.exe"));
        Assert.Equal("old", fileSystem.ReadAllText(retired));
    }

    [Fact]
    public void A_staged_download_a_failed_update_left_behind_is_swept()
    {
        // Not tidiness. A staged binary is run before it is trusted, and Windows keeps an
        // executable mapped for a moment after the process exits - long enough that deleting it
        // right there answers "access is denied". Observed against a real published binary, so the
        // update that rejected it gives up on the cleanup and the next one collects it.
        var fileSystem = new InMemoryFileSystem();
        fileSystem.WriteAllText("/opt/bin/dotquay", "current");
        fileSystem.WriteAllText("/opt/bin/dotquay.new-0.18.0", "a download that was rejected");
        fileSystem.WriteAllText("/opt/bin/other.new-1.0.0", "someone else's");

        new ExecutableSwapper(fileSystem).SweepStaged("/opt/bin/dotquay");

        Assert.False(fileSystem.FileExists("/opt/bin/dotquay.new-0.18.0"));
        Assert.Equal("current", fileSystem.ReadAllText("/opt/bin/dotquay"));
        Assert.True(fileSystem.FileExists("/opt/bin/other.new-1.0.0"));
    }

    [Fact]
    public void Retired_binaries_are_ordered_by_version_not_by_timestamp()
    {
        var fileSystem = new InMemoryFileSystem();
        fileSystem.WriteAllText("/opt/bin/dotquay", "current");
        fileSystem.WriteAllText("/opt/bin/dotquay.old-0.9.0", "nine");
        fileSystem.WriteAllText("/opt/bin/dotquay.old-0.10.0", "ten");

        // Not one of ours: same directory, different tool. Must not be offered as a rollback target.
        fileSystem.WriteAllText("/opt/bin/other.old-0.99.0", "someone else's");

        var retired = new ExecutableSwapper(fileSystem).Retired("/opt/bin/dotquay");

        Assert.Equal(["0.10.0", "0.9.0"], retired.Select(entry => entry.Version));
    }
}

public class UpdateCheckCacheTests
{
    private const string Channel = "https://gitlab.example/api/v4/projects/1";

    [Fact]
    public void A_missing_cache_is_stale_so_the_first_run_checks()
    {
        var cache = new UpdateCheckCache(new InMemoryFileSystem(), new FakeEnvironmentReader());

        Assert.True(cache.IsStale(Channel, TimeSpan.FromHours(24)));
        Assert.Null(cache.Read(Channel));
    }

    [Fact]
    public void A_fresh_answer_is_read_back_and_not_re_checked()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-09-11T10:00:00Z"));
        var cache = new UpdateCheckCache(new InMemoryFileSystem(), new FakeEnvironmentReader(), clock);

        cache.Write(Channel, "0.18.0");
        clock.Advance(TimeSpan.FromHours(1));

        Assert.Equal("0.18.0", cache.Read(Channel)?.LatestVersion);
        Assert.False(cache.IsStale(Channel, TimeSpan.FromHours(24)));

        clock.Advance(TimeSpan.FromHours(24));
        Assert.True(cache.IsStale(Channel, TimeSpan.FromHours(24)));
    }

    [Fact]
    public void An_answer_from_another_channel_is_not_an_answer()
    {
        var cache = new UpdateCheckCache(new InMemoryFileSystem(), new FakeEnvironmentReader());
        cache.Write(Channel, "0.18.0");

        Assert.Null(cache.Read("https://mirror.example/api/v4/projects/7"));
        Assert.True(cache.IsStale("https://mirror.example/api/v4/projects/7", TimeSpan.FromHours(24)));
    }

    [Fact]
    public void A_corrupt_cache_means_no_notice_rather_than_a_failed_command()
    {
        var fileSystem = new InMemoryFileSystem();
        var cache = new UpdateCheckCache(fileSystem, new FakeEnvironmentReader());
        fileSystem.WriteAllText("/home/kani/.dotquay/update-check.json", "{ this is not json");

        Assert.Null(cache.Read(Channel));
    }

    [Fact]
    public void With_no_home_directory_the_cache_is_simply_absent()
    {
        var cache = new UpdateCheckCache(new InMemoryFileSystem(), new FakeEnvironmentReader(homeDirectory: null));

        cache.Write(Channel, "0.18.0");

        Assert.Null(cache.Path);
        Assert.Null(cache.Read(Channel));
    }
}

public class TomlUserConfigStoreTests
{
    [Fact]
    public void With_no_config_file_the_built_in_defaults_apply()
    {
        var settings = new TomlUserConfigStore(new InMemoryFileSystem(), new FakeEnvironmentReader()).Read();

        Assert.Equal(UpdateSettings.DefaultChannelUrl, settings.ChannelUrl);
        Assert.True(settings.NoticeEnabled);
        Assert.Equal(24, settings.IntervalHours);
    }

    [Fact]
    public void The_file_is_read()
    {
        var fileSystem = new InMemoryFileSystem();
        fileSystem.WriteAllText("/home/kani/.dotquay/config.toml", """
            [update]
            channel = "https://mirror.example/api/v4/projects/7"
            check = false
            interval_hours = 6
            """);

        var settings = new TomlUserConfigStore(fileSystem, new FakeEnvironmentReader()).Read();

        Assert.Equal("https://mirror.example/api/v4/projects/7", settings.ChannelUrl);
        Assert.False(settings.NoticeEnabled);
        Assert.Equal(6, settings.IntervalHours);
    }

    [Fact]
    public void The_environment_beats_the_file()
    {
        // CI sets variables; a CI image should not have to write a config file to stop a tool from
        // reaching the network.
        var fileSystem = new InMemoryFileSystem();
        fileSystem.WriteAllText("/home/kani/.dotquay/config.toml", """
            [update]
            channel = "https://mirror.example/api/v4/projects/7"
            check = true
            """);

        var environment = new FakeEnvironmentReader()
            .Set(UpdateSettings.ChannelVariable, "https://from-env.example/api/v4/projects/9/")
            .Set(UpdateSettings.NoCheckVariable, "1");

        var settings = new TomlUserConfigStore(fileSystem, environment).Read();

        Assert.Equal("https://from-env.example/api/v4/projects/9", settings.ChannelUrl);
        Assert.False(settings.NoticeEnabled);
    }

    [Fact]
    public void Notify_off_round_trips_through_the_file()
    {
        var fileSystem = new InMemoryFileSystem();
        var store = new TomlUserConfigStore(fileSystem, new FakeEnvironmentReader());

        store.SetNoticeEnabled(false);
        Assert.False(new TomlUserConfigStore(fileSystem, new FakeEnvironmentReader()).Read().NoticeEnabled);

        store.SetNoticeEnabled(true);
        Assert.True(new TomlUserConfigStore(fileSystem, new FakeEnvironmentReader()).Read().NoticeEnabled);
    }

    [Fact]
    public void Notify_preserves_a_custom_channel()
    {
        var fileSystem = new InMemoryFileSystem();
        fileSystem.WriteAllText("/home/kani/.dotquay/config.toml", """
            [update]
            channel = "https://mirror.example/api/v4/projects/7"
            """);

        new TomlUserConfigStore(fileSystem, new FakeEnvironmentReader()).SetNoticeEnabled(false);

        var settings = new TomlUserConfigStore(fileSystem, new FakeEnvironmentReader()).Read();
        Assert.Equal("https://mirror.example/api/v4/projects/7", settings.ChannelUrl);
        Assert.False(settings.NoticeEnabled);
    }

    [Fact]
    public void The_config_file_is_plain_ascii()
    {
        // Same reasoning as the credential store's: this file gets opened in whatever editor or
        // shell is to hand, and a stray em-dash renders as mojibake in anything reading it as ANSI.
        var fileSystem = new InMemoryFileSystem();
        new TomlUserConfigStore(fileSystem, new FakeEnvironmentReader()).SetNoticeEnabled(false);

        var text = fileSystem.ReadAllText("/home/kani/.dotquay/config.toml");
        Assert.All(text, character => Assert.True(character < 128, $"non-ASCII character '{character}' in config.toml"));
    }
}

/// <summary>A <see cref="TimeProvider"/> the tests drive by hand, so the cache's staleness rule is
/// asserted rather than waited for.</summary>
public sealed class FakeTimeProvider : TimeProvider
{
    private DateTimeOffset _now;

    public FakeTimeProvider(DateTimeOffset now)
    {
        _now = now;
    }

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now = _now.Add(by);
}
