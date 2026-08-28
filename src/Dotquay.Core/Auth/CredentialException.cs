namespace Dotquay.Core.Auth;

/// <summary>Thrown when a credential can't be parsed, read, or stored. The message is written to be
/// shown to the user as-is, and must never contain a token.</summary>
public sealed class CredentialException : Exception
{
    public CredentialException(string message)
        : base(message)
    {
    }

    public CredentialException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
