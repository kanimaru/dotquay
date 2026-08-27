namespace Dotquay.Core.Git;

/// <summary>Thrown when shelling out to <c>git</c> fails or when <c>git</c> itself can't be
/// started. The message is written to be shown to the user as-is.</summary>
public sealed class GitCommandException : Exception
{
    public GitCommandException(string message)
        : base(message)
    {
    }

    public GitCommandException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
