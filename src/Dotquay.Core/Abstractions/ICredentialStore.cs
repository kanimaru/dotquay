using Dotquay.Core.Auth;

namespace Dotquay.Core.Abstractions;

/// <summary>
/// The user-level credential store behind <c>dotquay login</c> (SPEC.md §11.2).
/// </summary>
/// <remarks>
/// An interface rather than a concrete class because the storage decision is explicitly provisional.
/// The shipping implementation is a plaintext TOML file with owner-only permissions — the same
/// tradeoff <c>docker login</c> makes by default, chosen because an OS keychain needs P/Invoke or a
/// per-platform subprocess and this project ships one NativeAOT binary per OS (SPEC.md §12). Keeping
/// callers behind this seam means a DPAPI/libsecret/Keychain backend can replace it later without
/// touching a single call site.
/// </remarks>
public interface ICredentialStore
{
    /// <summary>The credential covering <paramref name="scope"/>, or null. When several entries match,
    /// the most specific wins (SPEC.md §11.2) — a per-project token beats a host-wide one.</summary>
    Credential? Find(CredentialScope scope);

    /// <summary>Every stored entry, without tokens, for <c>dotquay login --list</c>.</summary>
    IReadOnlyList<StoredCredential> List();

    /// <summary>Adds or replaces the entry for exactly this scope.</summary>
    void Set(CredentialScope scope, Credential credential);

    /// <summary>Removes the entry for exactly this scope, returning whether there was one.
    /// Deliberately not prefix-matched: removing more credentials than were named is not something a
    /// <c>logout</c> should ever do.</summary>
    bool Remove(CredentialScope scope);
}
