using Dotquay.Core.Abstractions;

namespace Dotquay.Core.IO;

/// <summary>Real, process-backed <see cref="IEnvironmentReader"/>. The only type in the codebase
/// allowed to call <see cref="Environment.GetEnvironmentVariable(string)"/>.</summary>
public sealed class SystemEnvironmentReader : IEnvironmentReader
{
    /// <summary>Overrides <see cref="HomeDirectory"/>, so a run keeps its <c>.dotquay</c> folder (cache,
    /// credentials, user config, update check) somewhere else. The real-process tests set it to a temp
    /// folder: on Windows the user profile comes from the shell's known folder, not from
    /// <c>USERPROFILE</c>, so no other variable keeps a child dotquay out of the real
    /// <c>~/.dotquay</c>.</summary>
    public const string HomeOverrideVariable = "DOTQUAY_HOME";

    public string? GetVariable(string name) => Environment.GetEnvironmentVariable(name);

    public string? HomeDirectory
    {
        get
        {
            var overridden = GetVariable(HomeOverrideVariable);
            if (!string.IsNullOrEmpty(overridden))
            {
                return overridden;
            }

            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return string.IsNullOrEmpty(profile) ? null : profile;
        }
    }
}
