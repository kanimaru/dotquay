using Dotquay.Core.Abstractions;
using Dotquay.Core.Auth;
using Dotquay.Core.IO;

namespace Dotquay.Cli.Commands;

/// <summary>The one place the CLI builds the credential stack, so <c>login</c>, the git credential
/// helper, <c>doctor</c>, and the NuGet client can never end up reading different files or applying
/// different precedence.</summary>
internal static class CredentialFactory
{
    public static ICredentialStore CreateStore()
    {
        var fileSystem = new SystemFileSystem();
        return new TomlCredentialStore(fileSystem, TomlCredentialStore.DefaultPath(new SystemEnvironmentReader(), fileSystem));
    }

    public static CredentialResolver CreateResolver() =>
        new(new SystemEnvironmentReader(), CreateStore());

    /// <summary>Where the credential file lives, for messages that tell the user what was written
    /// where. Never prints its contents.</summary>
    public static string StorePath() =>
        TomlCredentialStore.DefaultPath(new SystemEnvironmentReader(), new SystemFileSystem());
}
