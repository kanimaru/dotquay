using System.Text.Json;
using Dotquay.Core.Abstractions;

namespace Dotquay.Core.SelfUpdate;

/// <summary>
/// The last answer the update channel gave, at <c>~/.dotquay/update-check.json</c>.
/// </summary>
/// <remarks>
/// <para>The notice is rendered from this file and never from a live request, which is what makes it
/// free: no command's latency depends on a network the user did not ask to touch. The refresh that
/// fills it is detached and best-effort.</para>
/// <para>Every operation swallows its own failures. An unreadable or half-written cache means "no
/// notice this time", never a failed command — a tool that cannot run because it could not read its
/// own reminder about updates would be a worse bug than the one this feature fixes.</para>
/// </remarks>
public sealed class UpdateCheckCache
{
    public const string FileName = "update-check.json";

    private readonly IFileSystem _fileSystem;
    private readonly string? _path;
    private readonly TimeProvider _clock;

    public UpdateCheckCache(IFileSystem fileSystem, IEnvironmentReader environment, TimeProvider? clock = null, string? path = null)
    {
        _fileSystem = fileSystem;
        _clock = clock ?? TimeProvider.System;
        _path = path ?? (environment.HomeDirectory is { Length: > 0 } home
            ? fileSystem.CombinePath(home, ".dotquay", FileName)
            : null);
    }

    public string? Path => _path;

    /// <summary>What was cached for this channel, or null when there is nothing usable. A cache
    /// written against a different channel is treated as absent rather than as an answer, because it
    /// is an answer to a different question.</summary>
    public UpdateCheckState? Read(string channelUrl)
    {
        if (_path is null || !_fileSystem.FileExists(_path))
        {
            return null;
        }

        try
        {
            var state = JsonSerializer.Deserialize(
                _fileSystem.ReadAllText(_path), SelfUpdateJsonContext.Default.UpdateCheckState);

            return state is null || !string.Equals(state.Channel, channelUrl, StringComparison.OrdinalIgnoreCase)
                ? null
                : state;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Write(string channelUrl, string? latestVersion)
    {
        if (_path is null)
        {
            return;
        }

        try
        {
            var state = new UpdateCheckState
            {
                CheckedAt = _clock.GetUtcNow(),
                LatestVersion = latestVersion,
                Channel = channelUrl,
            };

            _fileSystem.WriteAllText(_path, JsonSerializer.Serialize(state, SelfUpdateJsonContext.Default.UpdateCheckState));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Whether the cached answer is old enough to be worth a request. A missing cache is
    /// stale by definition, which is what makes the very first run start a check.</summary>
    public bool IsStale(string channelUrl, TimeSpan interval)
    {
        var state = Read(channelUrl);
        return state is null || _clock.GetUtcNow() - state.CheckedAt >= interval;
    }
}
