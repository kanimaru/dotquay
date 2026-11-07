namespace Dotquay.Core.Auth;

/// <summary>Thrown for a <c>~/.dotquay/credentials.toml</c> that can't be read: invalid TOML, a file
/// version this build doesn't know, or an entry that isn't the shape <c>dotquay login</c> writes.</summary>
/// <remarks>
/// A <see cref="CredentialException"/> so every place that already handles an unreadable credential
/// store keeps doing so (the git credential helper answers "no credential" rather than failing a
/// clone); separate from it so <c>JsonErrorClassifier</c> can report the file as
/// <c>invalid_config_file</c> rather than <c>auth</c>. An <c>auth</c> failure suggests
/// <c>dotquay login</c>, which reads the same file and fails the same way.
/// </remarks>
public sealed class CredentialsFileException : CredentialException
{
    public CredentialsFileException(string message)
        : base(message)
    {
    }

    public CredentialsFileException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
