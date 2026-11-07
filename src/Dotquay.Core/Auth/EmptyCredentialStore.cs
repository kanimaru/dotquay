using Dotquay.Core.Abstractions;

namespace Dotquay.Core.Auth;

/// <summary>
/// A store that holds nothing and accepts nothing, used when there is no home directory to keep a
/// credential file in.
/// </summary>
/// <remarks>
/// A null object rather than a nullable store, so <see cref="CredentialResolver"/> has one code path
/// instead of a null check at every step of its precedence chain. The situation is ordinary rather
/// than exceptional: a container with no <c>$HOME</c> is a normal CI shape, and it authenticates
/// through <c>DOTQUAY_NUGET_*</c> environment variables, which sit above the file in precedence
/// anyway (SPEC.md §11.2).
/// </remarks>
public sealed class EmptyCredentialStore : ICredentialStore {
    public Credential? Find(CredentialScope scope) => null;

    public IReadOnlyList<StoredCredential> List() => [];

    public void Set(CredentialScope scope, Credential credential) =>
        throw new CredentialException(
            "Could not determine your home directory, so there is nowhere to store credentials. "
            + "Set DOTQUAY_NUGET_USERNAME and DOTQUAY_NUGET_TOKEN instead.");

    public bool Remove(CredentialScope scope) => false;
}
