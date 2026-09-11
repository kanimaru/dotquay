using Dotquay.Core.IO;
using Dotquay.Core.Json;
using Dotquay.Core.SelfUpdate;
using Dotquay.Core.Workflows;

namespace Dotquay.Cli.Commands;

/// <summary>The one place the CLI builds the self-update stack, so <c>self check</c>,
/// <c>self update</c>, and the passive notice can never disagree about which channel they are
/// talking to or which binary they are talking about.</summary>
internal static class SelfUpdateFactory
{
    public static UpdateSettings Settings() => ConfigStore().Read();

    public static TomlUserConfigStore ConfigStore() =>
        new(new SystemFileSystem(), new SystemEnvironmentReader());

    public static UpdateCheckCache Cache() =>
        new(new SystemFileSystem(), new SystemEnvironmentReader());

    public static IReleaseChannel Channel(UpdateSettings settings) =>
        new GitLabReleaseChannel(settings.ChannelUrl, CredentialFactory.CreateResolver());

    public static SelfUpdateWorkflow Workflow(UpdateSettings settings) =>
        new(new SystemFileSystem(), Channel(settings), new ProcessExecutableProbe(), ExecutablePath(), DotquayVersion.Current);

    /// <summary>
    /// The binary a self-update would replace.
    /// </summary>
    /// <remarks>
    /// <see cref="Environment.ProcessPath"/> is the real answer under NativeAOT, where there is no
    /// managed assembly to ask and <c>AppContext.BaseDirectory</c> would name a directory rather than
    /// a file. It returns null only on platforms that will not say, and a self-updater that does not
    /// know what it is has nothing useful to do.
    /// </remarks>
    public static string ExecutablePath() =>
        Environment.ProcessPath
        ?? throw new WorkflowException(
            "This platform will not say which file this process was started from, so dotquay cannot work out "
            + "which binary to replace. Update it by hand.");
}
