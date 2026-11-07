namespace Dotquay.Core.SelfUpdate;

/// <summary>
/// What the user has said about updates (docs/DESIGN-v0.17-SELF-UPDATE.md).
/// </summary>
/// <param name="ChannelUrl">Where releases come from.</param>
/// <param name="NoticeEnabled">Whether the passive "a newer dotquay exists" line may be printed.
/// Never affects <c>self check</c> or <c>self update</c>, which are asked for explicitly — turning
/// the notice off is not turning the feature off.</param>
/// <param name="IntervalHours">How stale the cached answer may get before a refresh is
/// started.</param>
public sealed record UpdateSettings(string ChannelUrl, bool NoticeEnabled, int IntervalHours) {
    /// <summary>dotquay's own release channel. A constant rather than a required setting because a
    /// tool that cannot update itself out of the box has not solved the problem it set out to
    /// solve.</summary>
    public const string DefaultChannelUrl = "https://gitlab.kemomi.de/api/v4/projects/128";

    public const int DefaultIntervalHours = 24;

    /// <summary>Overrides the channel, for a mirror or a test. Environment beats the config file
    /// for the same reason it does for credentials (SPEC.md §11.2): CI sets variables, and a CI
    /// image should not have to write a config file.</summary>
    public const string ChannelVariable = "DOTQUAY_UPDATE_URL";

    /// <summary>Set to anything non-empty to silence the notice, for one command or for a whole
    /// image.</summary>
    public const string NoCheckVariable = "DOTQUAY_NO_UPDATE_CHECK";

    public static UpdateSettings Defaults { get; } = new(DefaultChannelUrl, NoticeEnabled: true,
        DefaultIntervalHours);

    public TimeSpan Interval => TimeSpan.FromHours(Math.Clamp(IntervalHours, 1, 24 * 30));
}
