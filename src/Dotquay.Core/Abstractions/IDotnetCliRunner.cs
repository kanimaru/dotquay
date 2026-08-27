namespace Dotquay.Core.Abstractions;

/// <summary>
/// Shells out to the <c>dotnet</c> CLI for the one step dotquay deliberately doesn't reimplement:
/// actually restoring NuGet packages (SPEC.md §11 — dotquay writes <c>PackageReference</c> entries
/// itself, then hands off to real <c>dotnet restore</c> for resolution/download/lockfile, the same
/// "shell out to the real tool" philosophy as <see cref="IGitClient"/>). Kept behind an interface so
/// the dotnet sync path is testable without actually invoking the SDK or touching the network.
/// </summary>
public interface IDotnetCliRunner
{
    /// <summary>Runs <c>dotnet restore &lt;csprojPath&gt;</c>. Throws if it exits non-zero.
    /// <paramref name="environmentVariables"/>, when given, are set on the child process only —
    /// everything else it inherits from dotquay's own environment is untouched. Used to pass
    /// <c>NuGetPackageSourceCredentials_dotquay</c> through for private feeds (see
    /// <see cref="Dotnet.DotnetInstaller"/>) without ever writing a secret to disk.</summary>
    Task RestoreAsync(
        string csprojPath, IReadOnlyDictionary<string, string>? environmentVariables = null, CancellationToken cancellationToken = default);
}
