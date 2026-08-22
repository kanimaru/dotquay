namespace Dependi.Core.Manifest;

/// <summary>Thrown for both malformed TOML and manifests that violate dependi's schema rules
/// (e.g. an <c>[addons.*]</c> entry naming zero or more than one of version/branch/rev). The
/// message is written to be shown to the user as-is — see requirement P0-2's acceptance
/// criterion that bad input "errors with a clear message".</summary>
public sealed class ManifestParseException : Exception
{
    public ManifestParseException(string message)
        : base(message)
    {
    }

    public ManifestParseException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
