namespace Dotquay.Core.Local;

/// <summary>Thrown for a <c>dotquay.local.toml</c> that can't be read: invalid TOML, or an entry
/// that isn't the shape the file is written in.</summary>
/// <remarks>
/// A <see cref="LocalLinkException"/> so every place that already turns an unreadable local file
/// into a workflow failure keeps doing so; separate from it so <c>JsonErrorClassifier</c> can report
/// a broken file as <c>invalid_project_file</c>, like a broken <c>dotquay.toml</c> or
/// <c>dotquay.lock</c>, rather than as the link target problems the base type also covers.
/// </remarks>
public sealed class LocalLinksParseException : LocalLinkException
{
    public LocalLinksParseException(string message)
        : base(message)
    {
    }

    public LocalLinksParseException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
