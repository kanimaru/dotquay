namespace Dotquay.Core.Git;

/// <summary>
/// Makes sure a freshly checked-out repository holds real files rather than Git LFS pointers.
/// </summary>
/// <remarks>
/// <para>
/// <b>It only does anything when pointers are present.</b> A repository without LFS, or a machine where
/// git-lfs is active and git already downloaded everything during checkout, costs one <c>git grep</c> and
/// nothing else. The repair is for the machine where it did not happen:
/// </para>
/// <list type="bullet">
/// <item>git-lfs missing: stop with a message that names the fix. Carrying on would install pointer files
/// and report them clean (see <see cref="LfsPointer"/>).</item>
/// <item>git-lfs present but never activated (<c>git lfs install</c> not run, common on a CI image or a
/// fresh Linux box): activate it for this checkout only, then <c>git lfs pull</c>. Measured on a machine with
/// an empty global git config: the plain checkout left 132-byte pointers; <c>git lfs install --local
/// --skip-repo</c> followed by <c>git lfs pull origin</c> produced files hash-identical to the originals.</item>
/// </list>
/// <para>
/// <c>--local</c> because dotquay's cache checkout is its own repository, and nothing about a user's global
/// git configuration should change as a side effect of installing an addon. <c>--skip-repo</c> because
/// there is nothing to hook into: no commits are made here.
/// </para>
/// <para>
/// Git itself is injected as <see cref="GitRunner"/> so every branch is testable without a repository, a
/// network, or git-lfs installed; the real wiring lives in <see cref="ProcessGitClient"/>.
/// </para>
/// </remarks>
public sealed class LfsResolver {
    /// <summary>Runs <c>git &lt;args&gt;</c> in a directory and returns its exit code and output.</summary>
    public delegate Task<(int ExitCode, string StdOut, string StdErr)> GitRunner(
        string workingDirectory, string[] args, CancellationToken cancellationToken);

    /// <summary>Cap on candidate files read to confirm they are pointers: a repository full of pointers
    /// has them all fail the same way, so naming a few is enough.</summary>
    private const int MaxCandidates = 500;

    private readonly GitRunner _git;
    private readonly Func<string, byte[]> _readFile;

    /// <param name="git">How to run git.</param>
    /// <param name="readFile">Reads a file by full path. Defaults to the real file system.</param>
    public LfsResolver(GitRunner git, Func<string, byte[]>? readFile = null) {
        _git = git;
        _readFile = readFile ?? File.ReadAllBytes;
    }

    /// <summary>
    /// Ensures <paramref name="checkoutPath"/> contains no LFS pointers, downloading the real files from
    /// <paramref name="remote"/> when it does. Throws a <see cref="GitCommandException"/> written to be shown
    /// to the user if that cannot be done.
    /// </summary>
    public async Task EnsureResolvedAsync(string checkoutPath, string remote, CancellationToken cancellationToken = default) {
        var pointers = await FindPointersAsync(checkoutPath, cancellationToken);
        if (pointers.Count == 0) {
            return;
        }

        var version = await _git(checkoutPath, ["lfs", "version"], cancellationToken);
        if (version.ExitCode != 0) {
            throw new GitCommandException(NotInstalled(pointers));
        }

        var install = await _git(checkoutPath, ["lfs", "install", "--local", "--skip-repo"], cancellationToken);
        if (install.ExitCode != 0) {
            throw new GitCommandException(PullFailed(pointers, install.StdErr));
        }

        var pull = await _git(checkoutPath, ["lfs", "pull", remote], cancellationToken);
        if (pull.ExitCode != 0) {
            throw new GitCommandException(
                PullFailed(pointers, pull.StdErr),
                isAuthenticationFailure: GitErrorClassifier.LooksLikeAuthenticationFailure(pull.StdErr));
        }

        // Trust the result, not the exit code: an LFS server that answers but lacks the objects can
        // report success and leave pointers behind.
        var remaining = await FindPointersAsync(checkoutPath, cancellationToken);
        if (remaining.Count > 0) {
            throw new GitCommandException(StillPointers(remaining));
        }
    }

    /// <summary>
    /// Tracked files in the working tree that are LFS pointers, as paths relative to
    /// <paramref name="checkoutPath"/>. Empty when there are none.
    /// </summary>
    public async Task<IReadOnlyList<string>> FindPointersAsync(string checkoutPath, CancellationToken cancellationToken = default) {
        // git grep reads tracked files only, skips binaries with -I (a pointer is text, a real library is
        // not), and so stays fast on a repository of large binaries. -z for paths with odd characters.
        var grep = await _git(
            checkoutPath,
            ["grep", "-l", "-I", "-z", "-e", "^version https://git-lfs.github.com/spec/v1", "--", "."],
            cancellationToken);

        // Exit 1 is "no match". Any other failure also means there is nothing to act on: this is a
        // safeguard, not a gate, and must never turn a working install into a failing one.
        if (grep.ExitCode != 0 || grep.StdOut.Length == 0) {
            return [];
        }

        var found = new List<string>();
        foreach (var candidate in grep.StdOut.Split('\0', StringSplitOptions.RemoveEmptyEntries).Take(MaxCandidates)) {
            try {
                var full = Path.Combine(checkoutPath, candidate);
                // Cheap guard first: only small files can be pointers, and this avoids reading a large
                // text file (a changelog) in full.
                if (new FileInfo(full).Length <= LfsPointer.MaxPointerBytes && LfsPointer.Matches(_readFile(full))) {
                    found.Add(candidate.Replace('\\', '/'));
                }
            } catch (IOException) {
                // Vanished or unreadable between grep and here: not a pointer we can report on.
            }
        }

        return found;
    }

    private static string Describe(IReadOnlyList<string> pointers) =>
        pointers.Count == 1
            ? $"1 file ({pointers[0]})"
            : $"{pointers.Count} files (for example {pointers[0]})";

    private static string NotInstalled(IReadOnlyList<string> pointers) =>
        $"This repository stores {Describe(pointers)} in Git LFS, but git-lfs is not installed, so git left "
        + "tiny placeholder text files where the real ones belong and dotquay will not install those as the addon. "
        + "Install git-lfs (https://git-lfs.com), run 'git lfs install' once, and try again.";

    private static string PullFailed(IReadOnlyList<string> pointers, string stderr) =>
        $"This repository stores {Describe(pointers)} in Git LFS, and downloading them failed: {stderr.Trim()}";

    private static string StillPointers(IReadOnlyList<string> remaining) =>
        $"Git LFS finished but {Describe(remaining)} are still placeholders. The LFS server may not have the "
        + "files for this commit; check that they were pushed with 'git lfs push --all' from the source repository.";
}
