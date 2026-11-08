namespace Dotquay.Core.Patch;

/// <summary>
/// Thrown when a non-empty patch applied without error yet left its base exactly as it was.
/// </summary>
/// <remarks>
/// Taken as success, that tree goes on to be installed and its hash recorded as the patched
/// addon's <c>content_hash</c>: the patch is gone, and the lockfile now vouches for its absence.
/// It happened for real: <c>git apply</c> run from a staging folder inside the project's own
/// repository skipped every path and exited 0. Whatever the cause next time, refusing is the only
/// answer that keeps the lockfile honest.
/// </remarks>
public sealed class PatchHadNoEffectException : Exception {
    public PatchHadNoEffectException(string message)
        : base(message) {
    }
}
