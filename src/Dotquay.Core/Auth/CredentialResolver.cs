using Dotquay.Core.Abstractions;

namespace Dotquay.Core.Auth;

/// <summary>
/// The one place dotquay decides what credential to use for a given feed or repository
/// (SPEC.md §11.2). Absorbs the pre-v0.7 <c>NuGetCredentials</c>, which read one hard-coded pair of
/// environment variables.
/// </summary>
/// <remarks>
/// One class rather than a chain of them, because the ordering *is* the design and it should be
/// readable top to bottom in one method. Environment beats the stored file because CI injects
/// secrets as environment variables and should not have to write a credential file at pipeline
/// start; the cost is that a stale exported variable silently shadows a fresh <c>dotquay login</c>,
/// which <c>login</c> warns about and <c>doctor</c> reports.
/// </remarks>
public sealed class CredentialResolver {
    /// <summary>Pre-v0.7 variables, still honoured so no existing CI pipeline has to
    /// change.</summary>
    public const string GlobalUsernameVariable = "DOTQUAY_NUGET_USERNAME";

    public const string GlobalTokenVariable = "DOTQUAY_NUGET_TOKEN";

    private readonly IEnvironmentReader _environment;
    private readonly ICredentialStore _store;

    public CredentialResolver(IEnvironmentReader environment, ICredentialStore store) {
        _environment = environment;
        _store = store;
    }

    /// <summary>Per-source override variable names, e.g. source <c>gitlab-core</c> →
    /// <c>DOTQUAY_NUGET_USERNAME_GITLAB_CORE</c>. Normalised to <c>[A-Z0-9_]</c> so the result is a
    /// name a POSIX shell can actually export, unlike the
    /// <c>NuGetPackageSourceCredentials_&lt;key&gt;</c> variable NuGet itself defines.</summary>
    public static (string Username, string Token) SourceVariableNames(string sourceName) {
        var suffix = new string(sourceName.Select(c => char.IsLetterOrDigit(c)
            ? char.ToUpperInvariant(c) : '_').ToArray());
        return ($"{GlobalUsernameVariable}_{suffix}", $"{GlobalTokenVariable}_{suffix}");
    }

    /// <summary>
    /// Resolves the credential for a NuGet source: per-source environment, then the legacy global
    /// pair, then the stored file, then anonymous.
    /// </summary>
    public CredentialLookup ForNuGetSource(string sourceName, string sourceUrl) {
        var (usernameVariable, tokenVariable) = SourceVariableNames(sourceName);

        var fromSourceEnvironment = FromEnvironment(usernameVariable, tokenVariable);
        if (fromSourceEnvironment is not null) {
            return Guard(fromSourceEnvironment, CredentialOrigin.SourceEnvironment, sourceUrl);
        }

        var fromGlobalEnvironment = FromEnvironment(GlobalUsernameVariable, GlobalTokenVariable);
        if (fromGlobalEnvironment is not null) {
            return Guard(fromGlobalEnvironment, CredentialOrigin.GlobalEnvironment, sourceUrl);
        }

        return FromStore(sourceUrl);
    }

    /// <summary>
    /// Resolves the credential for a git remote. The stored file only — there is deliberately no
    /// git environment-variable pair, because git already has a well-established way to inject
    /// credentials in CI (its own credential helpers and SSH agent), and dotquay appends itself
    /// *after* those rather than competing with them (SPEC.md §11.2).
    /// </summary>
    public CredentialLookup ForGitRepository(string repositoryUrl) => FromStore(repositoryUrl);

    private CredentialLookup FromStore(string url) {
        if (!CredentialScope.TryParse(url, out var scope, out _)) {
            // An ssh:// or git@ remote lands here. Not an error: it simply has no stored
            // credential, and git's own key handling takes it from here.
            return CredentialLookup.Anonymous;
        }

        var credential = _store.Find(scope);
        return credential is null
            ? CredentialLookup.Anonymous
            : Guard(credential, CredentialOrigin.CredentialFile, url);
    }

    /// <summary>Withholds a credential bound for cleartext HTTP. Enforced here rather than at each
    /// call site so no caller can leak a token by forgetting to check.</summary>
    private static CredentialLookup Guard(Credential credential, CredentialOrigin origin,
        string url) {
        if (CredentialScope.TryParse(url, out var scope, out _) && !scope.IsSecure) {
            return new CredentialLookup(null, CredentialOrigin.WithheldInsecureTransport);
        }

        return new CredentialLookup(credential, origin);
    }

    /// <summary>Both variables, or neither. A half-configured pair (a username with no token, or
    /// the reverse) falls through to the next source rather than being guessed at — the same rule
    /// the pre-v0.7 <c>NuGetCredentials</c> used, kept because a partially-set pair is far more
    /// likely to be a typo than an intention.</summary>
    private Credential? FromEnvironment(string usernameVariable, string tokenVariable) {
        var username = _environment.GetVariable(usernameVariable);
        var token = _environment.GetVariable(tokenVariable);

        return !string.IsNullOrEmpty(username) && !string.IsNullOrEmpty(token)
            ? new Credential(username, token)
            : null;
    }
}
