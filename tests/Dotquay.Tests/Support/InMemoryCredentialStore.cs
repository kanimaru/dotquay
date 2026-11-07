using Dotquay.Core.Abstractions;
using Dotquay.Core.Auth;

namespace Dotquay.Tests.Support;

/// <summary>
/// <see cref="ICredentialStore"/> test double. Implements the longest-prefix rule for real rather
/// than returning a canned answer — the rule is the interesting part of the contract, and a fake
/// that ignored it would let a resolver bug through while every test passed.
/// </summary>
public sealed class InMemoryCredentialStore : ICredentialStore {
    private readonly Dictionary<string,
        (CredentialScope Scope, Credential Credential)> _entries = new(StringComparer.Ordinal);

    public InMemoryCredentialStore Add(string scope, string username, string token) {
        Set(CredentialScope.Parse(scope), new Credential(username, token));
        return this;
    }

    public Credential? Find(CredentialScope scope) {
        Credential? best = null;
        var bestSpecificity = -1;

        foreach (var (entryScope, credential) in _entries.Values) {
            if (entryScope.Covers(scope) && entryScope.Specificity > bestSpecificity) {
                best = credential;
                bestSpecificity = entryScope.Specificity;
            }
        }

        return best;
    }

    public IReadOnlyList<StoredCredential> List() =>
        _entries.Values
            .Select(entry => new StoredCredential(entry.Scope.Key, entry.Credential.Username))
            .OrderBy(entry => entry.Scope, StringComparer.Ordinal)
            .ToList();

    public void Set(CredentialScope scope, Credential credential) =>
        _entries[scope.Key] = (scope, credential);

    public bool Remove(CredentialScope scope) => _entries.Remove(scope.Key);
}
