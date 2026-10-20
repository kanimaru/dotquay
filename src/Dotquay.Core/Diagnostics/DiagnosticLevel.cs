namespace Dotquay.Core.Diagnostics;

/// <summary>
/// How much dotquay says about its own work, on stderr, for debugging (docs/DESIGN-v0.24-VERBOSE.md).
/// </summary>
/// <remarks>
/// Ordered on purpose: each level includes everything the levels below it say, so a check is always
/// a single <c>&gt;=</c> comparison and there is no combination of levels to reason about.
/// </remarks>
public enum DiagnosticLevel
{
    /// <summary>Nothing. The default, and what every existing invocation keeps getting.</summary>
    Off = 0,

    /// <summary><c>--verbose</c>: every external command (git, dotnet, cmd) with its exit code and
    /// duration, every HTTP request with its status, every pipeline step, every destructive file
    /// operation, resolver decisions, and full exception chains.</summary>
    Verbose = 1,

    /// <summary><c>--trace</c>: everything <see cref="Verbose"/> says, plus the captured
    /// stdout/stderr of each subprocess and every individual file write.</summary>
    Trace = 2,
}
