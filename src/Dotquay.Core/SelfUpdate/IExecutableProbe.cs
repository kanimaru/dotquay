namespace Dotquay.Core.SelfUpdate;

/// <summary>
/// Runs a candidate dotquay binary and asks it what version it is.
/// </summary>
/// <remarks>
/// This is this project's standing verification rule (docs/SPRINT-01.md — a binary that builds is
/// not a binary that runs) applied to the one case where the thing being checked is about to
/// replace the thing doing the checking. A download can be the right size, hash correctly, and
/// still be a binary the trimmer broke; the only test that catches that is starting it. Behind an
/// interface because starting a process is exactly the kind of I/O the rest of this codebase keeps
/// out of unit tests.
/// </remarks>
public interface IExecutableProbe
{
    /// <summary>The version the binary at <paramref name="path"/> reports, or null when it did not
    /// start, exited non-zero, or printed something that is not a version.</summary>
    Task<string?> ReadVersionAsync(string path, CancellationToken cancellationToken = default);
}
