using Dotquay.Core.Abstractions;
using Dotquay.Core.Workflows;
using Tomlyn;
using Tomlyn.Model;

namespace Dotquay.Core.SelfUpdate;

/// <summary>
/// User-level settings at <c>~/.dotquay/config.toml</c>, beside <c>credentials.toml</c> and the
/// shared fetch cache.
/// </summary>
/// <remarks>
/// <para>Per-user, never per-project, and that is the whole argument for a new file rather than a
/// section in <c>dotquay.toml</c>: which dotquay you run is a property of your machine, and a
/// project manifest that could pin its collaborators' CLI version would be a project that can
/// change what runs on someone else's computer.</para>
/// <para>Read through the same <c>TomlSerializer.Deserialize&lt;TomlTable&gt;</c> call shape as the
/// credential store — a call shape already proven under NativeAOT — and written as hand-built ASCII
/// for the same reason that one is: this file gets opened in whatever editor is to hand.</para>
/// </remarks>
public sealed class TomlUserConfigStore
{
    public const string FileName = "config.toml";

    private const string UpdateSection = "update";

    private readonly IFileSystem _fileSystem;
    private readonly IEnvironmentReader _environment;
    private readonly string? _path;

    public TomlUserConfigStore(IFileSystem fileSystem, IEnvironmentReader environment, string? path = null)
    {
        _fileSystem = fileSystem;
        _environment = environment;
        _path = path ?? DefaultPath(environment, fileSystem);
    }

    /// <summary>Null when the platform will not say where home is. User-level settings are then
    /// unavailable rather than guessed at, and the built-in defaults apply — the same call this
    /// project's credential store makes.</summary>
    public static string? DefaultPath(IEnvironmentReader environment, IFileSystem fileSystem) =>
        environment.HomeDirectory is { Length: > 0 } home
            ? fileSystem.CombinePath(home, ".dotquay", FileName)
            : null;

    public string? Path => _path;

    /// <summary>File first, then environment on top. A malformed file is reported rather than
    /// silently ignored: a setting that quietly does nothing is worse than one that complains.</summary>
    public UpdateSettings Read()
    {
        var settings = ReadFile();

        if (_environment.GetVariable(UpdateSettings.ChannelVariable) is { Length: > 0 } channel)
        {
            settings = settings with { ChannelUrl = channel.TrimEnd('/') };
        }

        if (_environment.GetVariable(UpdateSettings.NoCheckVariable) is { Length: > 0 })
        {
            settings = settings with { NoticeEnabled = false };
        }

        return settings;
    }

    /// <summary>Backs <c>dotquay self notify on|off</c>. Rewrites the file rather than editing it in
    /// place: dotquay owns it, and every key it can hold is written here.</summary>
    public void SetNoticeEnabled(bool enabled)
    {
        if (_path is null)
        {
            throw new WorkflowException(
                "Could not determine your home directory, so there is nowhere to store settings. "
                + $"Set {UpdateSettings.NoCheckVariable}=1 in your environment instead.");
        }

        var current = ReadFile();
        Write(current with { NoticeEnabled = enabled });
    }

    private UpdateSettings ReadFile()
    {
        if (_path is null || !_fileSystem.FileExists(_path))
        {
            return UpdateSettings.Defaults;
        }

        TomlTable table;
        try
        {
            table = TomlSerializer.Deserialize<TomlTable>(
                _fileSystem.ReadAllText(_path), new TomlSerializerOptions { SourceName = _path }) ?? new TomlTable();
        }
        catch (TomlException ex)
        {
            throw new WorkflowException($"{_path}: invalid TOML.{Environment.NewLine}{ex.Message}", ex);
        }

        if (!table.TryGetValue(UpdateSection, out var raw) || raw is not TomlTable update)
        {
            return UpdateSettings.Defaults;
        }

        return new UpdateSettings(
            update.TryGetValue("channel", out var channel) && channel is string text && text.Length > 0
                ? text.TrimEnd('/')
                : UpdateSettings.DefaultChannelUrl,
            NoticeEnabled: !update.TryGetValue("check", out var check) || check is not bool flag || flag,
            IntervalHours: update.TryGetValue("interval_hours", out var interval) && interval is long hours && hours > 0
                ? (int)Math.Min(hours, int.MaxValue)
                : UpdateSettings.DefaultIntervalHours);
    }

    private void Write(UpdateSettings settings)
    {
        var builder = new System.Text.StringBuilder();
        builder.AppendLine("# dotquay settings - written by 'dotquay self notify'.");
        builder.AppendLine("# Per-user, never per-project: which dotquay you run is a property of this machine.");
        builder.AppendLine();
        builder.AppendLine($"[{UpdateSection}]");
        builder.AppendLine($"channel = \"{settings.ChannelUrl.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"");
        builder.AppendLine($"check = {(settings.NoticeEnabled ? "true" : "false")}");
        builder.AppendLine($"interval_hours = {settings.IntervalHours}");

        _fileSystem.WriteAllText(_path!, builder.ToString());
    }
}
