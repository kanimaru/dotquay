namespace Dotquay.Core.Abstractions;

/// <summary>
/// Every read of the process environment in Dotquay.Core goes through this seam, for the same reason
/// <see cref="IFileSystem"/> exists: environment variables are process-global mutable state, and
/// tests that set them race each other.
/// </summary>
/// <remarks>
/// Not a theoretical concern. Before this seam, <c>NuGetV3ClientFactoryTests</c> and
/// <c>DotnetInstallerTests</c> both save-set-restored the real <c>DOTQUAY_NUGET_*</c> variables in a
/// try/finally, each reasoning correctly that xunit runs the facts *within* one class sequentially —
/// and neither covering the fact that xunit parallelises *across* classes by default, which is
/// exactly what those two classes did to each other.
/// </remarks>
public interface IEnvironmentReader {
    /// <summary>The variable's value, or null when unset. An empty value is returned as-is; callers
    /// decide whether empty means "unset" (credentials do).</summary>
    string? GetVariable(string name);

    /// <summary>The user's home directory — where <c>~/.dotquay</c> lives. Null when the platform
    /// won't say, in which case user-level state is unavailable rather than guessed at.</summary>
    string? HomeDirectory { get; }
}
