namespace Dotquay.Core.SelfUpdate;

/// <summary>Thrown for a <c>~/.dotquay/config.toml</c> that can't be read (invalid TOML). The message
/// names the file and the parser's <c>(line,column)</c>; <c>JsonErrorClassifier</c> reports it as
/// <c>invalid_config_file</c>.</summary>
public sealed class UserConfigFileException : Exception
{
    public UserConfigFileException(string message)
        : base(message)
    {
    }

    public UserConfigFileException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
