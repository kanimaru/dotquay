namespace Dotquay.Core.Sync;

/// <summary>Thrown by the sync engine (subtree extraction, dirty detection, atomic swap) for
/// anything that should stop a sync and be shown to the user as-is.</summary>
public sealed class SyncException : Exception
{
    public SyncException(string message)
        : base(message)
    {
    }

    public SyncException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
