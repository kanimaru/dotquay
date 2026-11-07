namespace Dotquay.Core.Workflows;

/// <summary>A user-facing, expected failure from <see cref="DependencyWorkflow"/> (missing
/// manifest/lockfile, duplicate addon name, ...) — distinct from the lower-level
/// <see cref="Resolve.AddonResolutionException"/>/<see cref="Sync.SyncException"/> it wraps, so CLI
/// commands can print one clean message without a stack trace for any of them.</summary>
public sealed class WorkflowException : Exception {
    public WorkflowException(string message) : base(message) {
    }

    public WorkflowException(string message, Exception innerException)
        : base(message, innerException) {
    }
}
