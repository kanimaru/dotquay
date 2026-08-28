using Dotquay.Core.Abstractions;

namespace Dotquay.Core.IO;

/// <summary>Real, process-backed <see cref="IEnvironmentReader"/>. The only type in the codebase
/// allowed to call <see cref="Environment.GetEnvironmentVariable(string)"/>.</summary>
public sealed class SystemEnvironmentReader : IEnvironmentReader
{
    public string? GetVariable(string name) => Environment.GetEnvironmentVariable(name);

    public string? HomeDirectory
    {
        get
        {
            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return string.IsNullOrEmpty(profile) ? null : profile;
        }
    }
}
