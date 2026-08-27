namespace Dotquay.Core.Patch;

/// <summary>Thrown for anything in the patch workflow (SPEC.md §10, requirement P1-1) that should
/// stop the operation and be shown to the user as-is — a failed <c>git diff</c>/<c>git apply</c>, or
/// an internal-consistency problem like a patch that no longer applies to its recorded base (as
/// opposed to a normal 3-way merge conflict, which is data, not an exception — see
/// <see cref="PatchMerger.Result"/>).</summary>
public sealed class PatchException : Exception
{
    public PatchException(string message)
        : base(message)
    {
    }

    public PatchException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
