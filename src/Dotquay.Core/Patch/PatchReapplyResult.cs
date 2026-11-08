namespace Dotquay.Core.Patch;

/// <summary>What <see cref="PatchReapplier.ReapplyAsync"/> made of a patch on a new version.
/// </summary>
/// <param name="ConflictedPaths">Paths the merge left with conflict markers; empty when
/// clean.</param>
/// <param name="UpstreamContainsPatch">
/// The clean merge came out the same as the new version itself (line endings aside, editor files
/// included): upstream took every change the patch makes, typically by merging the pull request
/// the patch was waiting on. The patch then adds nothing and can go. A patch upstream took only
/// part of still changes the merge, so it stays.
/// </param>
public sealed record PatchReapplyResult(IReadOnlyList<string> ConflictedPaths,
    bool UpstreamContainsPatch);
