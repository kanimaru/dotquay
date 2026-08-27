namespace Dotquay.Core.Dotnet;

/// <summary>
/// The one place this project reads NuGet credentials from — <c>DOTQUAY_NUGET_USERNAME</c>/
/// <c>DOTQUAY_NUGET_TOKEN</c> environment variables, never <c>dotquay.toml</c> (which is meant to be
/// committed). Shared by <see cref="NuGetV3ClientFactory"/> (dotquay's own read-side version
/// queries) and <see cref="DotnetInstaller"/> (translating into the <c>dotnet restore</c> child
/// process's environment) so both read the exact same two variables the same way.
/// </summary>
internal static class NuGetCredentials
{
    private const string UsernameEnvironmentVariable = "DOTQUAY_NUGET_USERNAME";
    private const string TokenEnvironmentVariable = "DOTQUAY_NUGET_TOKEN";

    /// <summary>Both non-null, or both null — a half-configured credential (only a username, or
    /// only a token) is treated as "not configured" rather than guessed at.</summary>
    public static (string? Username, string? Token) FromEnvironment()
    {
        var username = Environment.GetEnvironmentVariable(UsernameEnvironmentVariable);
        var token = Environment.GetEnvironmentVariable(TokenEnvironmentVariable);
        return username is not null && token is not null ? (username, token) : (null, null);
    }
}
