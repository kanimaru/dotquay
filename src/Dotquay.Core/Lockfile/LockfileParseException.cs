namespace Dotquay.Core.Lockfile;

/// <summary>Thrown for malformed <c>dotquay.lock</c> TOML and for entries that violate the
/// lockfile's invariants (most importantly: <c>resolved</c> must always be a full commit SHA).</summary>
public sealed class LockfileParseException : Exception {
    public LockfileParseException(string message)
        : base(message) {
    }

    public LockfileParseException(string message, Exception innerException)
        : base(message, innerException) {
    }
}
