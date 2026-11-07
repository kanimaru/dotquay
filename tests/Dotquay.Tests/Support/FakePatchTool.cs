using Dotquay.Core.Abstractions;
using Dotquay.Core.Patch;

namespace Dotquay.Tests.Support;

/// <summary>Configurable <see cref="IPatchTool"/> test double — no real git process needed to test
/// patch capture/re-apply orchestration (mirrors <see cref="FakeGitClient"/>'s role). Deliberately
/// does not attempt a faithful text 3-way merge: it takes whichever side is configured to "win" (or
/// writes a recognizable conflict marker) so tests can assert on <see cref="PatchMerger"/>'s own
/// file-presence branching (add/delete/modify routing), not on diff3 algorithm fidelity — that's
/// covered by real end-to-end verification against actual git.</summary>
public sealed class FakePatchTool : IPatchTool {
    private readonly IFileSystem _fileSystem;

    public FakePatchTool(IFileSystem fileSystem) {
        _fileSystem = fileSystem;
    }

    /// <summary>Configured diff text for a given (base, ours) directory pair. Missing entries
    /// return an empty string (no differences).</summary>
    public Dictionary<(string BaseDirectory, string OursDirectory), string> DiffResults {
        get;
    } = new();

    /// <summary>(base, ours) pairs that <see cref="DiffDirectoriesAsync"/> should throw for —
    /// simulates a real <c>git diff --no-index</c> failure (e.g. one side unreadable).</summary>
    public HashSet<(string BaseDirectory, string OursDirectory)> DiffFailures { get; } = new();

    /// <summary>Patch texts that <see cref="ApplyPatchAsync"/> should throw for — simulates a patch
    /// that no longer applies cleanly to its recorded base.</summary>
    public HashSet<string> PatchesThatFailToApply { get; } = new(StringComparer.Ordinal);

    public List<(string PatchText, string TargetDirectory)> ApplyCalls { get; } = [];

    /// <summary>Relative-path -> content writes that <see cref="ApplyPatchAsync"/> performs into
    /// <c>targetDirectory</c> when called with this exact patch text — simulates "this patch
    /// changes these files" without a real diff/apply engine.</summary>
    public Dictionary<string, IReadOnlyDictionary<string, string>> FileChangesByPatchText {
        get;
    } = new();

    /// <summary>Relative paths that <see cref="ApplyPatchAsync"/> deletes from
    /// <c>targetDirectory</c> when called with this exact patch text — simulates "this patch
    /// removes these files".</summary>
    public Dictionary<string, IReadOnlyList<string>> FileDeletionsByPatchText { get; } = new();

    /// <summary>Configured conflict count for an exact (current, base, other) file
    /// triple.</summary>
    public Dictionary<(string Current, string Base, string Other), int> ConflictCountsByFiles {
        get;
    } = new();

    /// <summary>Conflict count used when a triple isn't in <see cref="ConflictCountsByFiles"/> — 0
    /// (clean) by default.</summary>
    public int DefaultConflictCount { get; set; }

    public Task<string> DiffDirectoriesAsync(string baseDirectory, string oursDirectory,
        CancellationToken cancellationToken = default) {
        if (DiffFailures.Contains((baseDirectory, oursDirectory))) {
            throw new PatchException($"simulated diff failure for {baseDirectory} vs "
                + $"{oursDirectory}");
        }

        return Task.FromResult(DiffResults.TryGetValue((baseDirectory, oursDirectory), out var diff)
            ? diff : "");
    }

    public Task ApplyPatchAsync(string patchText, string targetDirectory,
        CancellationToken cancellationToken = default) {
        ApplyCalls.Add((patchText, targetDirectory));

        if (PatchesThatFailToApply.Contains(patchText)) {
            throw new PatchException($"simulated apply failure for a patch targeting "
                + $"{targetDirectory}");
        }

        if (FileChangesByPatchText.TryGetValue(patchText, out var changes)) {
            foreach (var (relativePath, content) in changes) {
                _fileSystem.WriteAllText(_fileSystem.CombinePath(targetDirectory, relativePath),
                    content);
            }
        }

        if (FileDeletionsByPatchText.TryGetValue(patchText, out var deletions)) {
            foreach (var relativePath in deletions) {
                _fileSystem.DeleteFile(_fileSystem.CombinePath(targetDirectory, relativePath));
            }
        }

        return Task.CompletedTask;
    }

    public Task<int> MergeFileAsync(string currentPath, string basePath, string otherPath,
        CancellationToken cancellationToken = default) {
        var conflictCount = ConflictCountsByFiles.TryGetValue((currentPath, basePath, otherPath),
            out var configured)
            ? configured
            : DefaultConflictCount;

        if (conflictCount == 0) {
            // Simulate a clean merge by taking "other" wholesale — good enough for orchestration
            // tests (see class remarks).
            _fileSystem.WriteAllBytes(currentPath, _fileSystem.ReadAllBytes(otherPath));
        } else {
            _fileSystem.WriteAllText(
                currentPath,
                $"<<<<<<< current\n=======\n>>>>>>> other ({conflictCount} conflict(s))\n");
        }

        return Task.FromResult(conflictCount);
    }
}
