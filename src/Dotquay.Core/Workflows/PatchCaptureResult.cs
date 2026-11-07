namespace Dotquay.Core.Workflows;

/// <summary>Result of <see cref="DependencyWorkflow.PatchAsync"/> (SPEC.md §10, requirement P1-1).
/// <paramref name="Captured"/> is false in two cases the caller should tell apart in its message:
/// disk now matches the pristine base exactly (nothing to capture), or it previously had a patch
/// that this run cleared because the hand-edits it captured are gone.
/// <paramref name="PatchFilePath"/> is where the patch was written when <paramref name="Captured"/>
/// is true, or where a stale patch file was removed from when it's false — always
/// <c>patches/&lt;name&gt;.patch</c> either way.</summary>
public sealed record PatchCaptureResult(string Name, bool Captured, string PatchFilePath);
