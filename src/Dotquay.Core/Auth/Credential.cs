namespace Dotquay.Core.Auth;

/// <summary>A username/token pair. <see cref="Token"/> is a secret and must never be written to a
/// project file, a lockfile, a <c>NuGet.Config</c>, a log line, or a command line — see
/// <c>SecretLeakTests</c>, which asserts exactly that.</summary>
public sealed record Credential(string Username, string Token);

/// <summary>One entry as <c>dotquay login --list</c> shows it: scope and username, never the token.
/// Not even a truncated one — a <c>glpat-</c> prefix plus a host is already useful to an attacker and
/// useless to the person reading it.</summary>
public sealed record StoredCredential(string Scope, string Username);

/// <summary>Where a resolved credential came from. Reported by <c>doctor</c> so "which token is this
/// actually using?" has an answer that isn't "read the source".</summary>
public enum CredentialOrigin
{
    /// <summary>No credential — the request goes out anonymous.</summary>
    None,

    /// <summary><c>DOTQUAY_NUGET_USERNAME_&lt;SOURCE&gt;</c>/<c>DOTQUAY_NUGET_TOKEN_&lt;SOURCE&gt;</c>.</summary>
    SourceEnvironment,

    /// <summary><c>DOTQUAY_NUGET_USERNAME</c>/<c>DOTQUAY_NUGET_TOKEN</c> — the pre-v0.7 pair.</summary>
    GlobalEnvironment,

    /// <summary><c>~/.dotquay/credentials.toml</c>, written by <c>dotquay login</c>.</summary>
    CredentialFile,

    /// <summary>A credential existed but the target is plain HTTP and not loopback, so it was
    /// withheld. Reported distinctly from <see cref="None"/> because "you configured this and it
    /// isn't being used" needs saying.</summary>
    WithheldInsecureTransport,
}

/// <summary>The outcome of a credential lookup: what to send, and where it came from.</summary>
public sealed record CredentialLookup(Credential? Credential, CredentialOrigin Origin)
{
    public static readonly CredentialLookup Anonymous = new(null, CredentialOrigin.None);

    public bool IsAuthenticated => Credential is not null;
}
