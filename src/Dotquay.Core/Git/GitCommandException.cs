namespace Dotquay.Core.Git;

/// <summary>Thrown when shelling out to <c>git</c> fails or when <c>git</c> itself can't be
/// started. The message is written to be shown to the user as-is.</summary>
public sealed class GitCommandException : Exception {
    public GitCommandException(string message, bool isAuthenticationFailure = false)
        : base(message) {
        IsAuthenticationFailure = isAuthenticationFailure;
    }

    public GitCommandException(string message, Exception innerException, bool isAuthenticationFailure = false)
        : base(message, innerException) {
        IsAuthenticationFailure = isAuthenticationFailure;
    }

    /// <summary>
    /// Whether git refused for want of credentials rather than for any other reason.
    /// </summary>
    /// <remarks>
    /// Carried on the exception rather than re-derived from <see cref="Exception.Message"/> at the
    /// catch site: the classification is made where the raw stderr is still available
    /// (<see cref="GitErrorClassifier"/>), and by the time a message has been wrapped by a workflow
    /// it has been reworded at least once. Consumed by the CLI to emit
    /// <c>error.kind = "auth"</c> (SPEC.md §17).
    /// </remarks>
    public bool IsAuthenticationFailure { get; }
}
