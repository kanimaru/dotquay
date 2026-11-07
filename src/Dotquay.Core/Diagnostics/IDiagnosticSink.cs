namespace Dotquay.Core.Diagnostics;

/// <summary>One diagnostic line.</summary>
/// <param name="Level">The lowest level at which this line is shown.</param>
/// <param name="Category">Short, lowercase subsystem name - <c>git</c>, <c>http</c>, <c>fs</c>,
/// <c>step</c> - so a reader can filter a long log by eye or with grep.</param>
/// <param name="Message">Already redacted where it carries URLs or arguments (see
/// <see cref="SecretRedactor"/>). May span several lines; the sink decides how continuation lines are
/// laid out.</param>
public readonly record struct DiagnosticEvent(DiagnosticLevel Level, string Category, string Message);

/// <summary>
/// Where <c>Dotquay.Core</c> sends its diagnostic lines.
/// </summary>
/// <remarks>
/// The same edge-of-the-system split as <see cref="Progress.IProgressReporter"/>: Core never decides
/// where text goes, so it physically cannot write a diagnostic line onto stdout and break the
/// <c>--json</c> one-document guarantee (SPEC.md section 17) or the <c>dotquay mcp</c> JSON-RPC
/// stream. The CLI's sink writes to stderr; tests record into a list.
/// </remarks>
public interface IDiagnosticSink {
    /// <summary>The most detailed level this sink wants. Read on every check, so keep it cheap.</summary>
    DiagnosticLevel Level { get; }

    /// <summary>Called only for events at or below <see cref="Level"/>. Must be thread-safe: git
    /// fetches, HTTP requests and the update check run concurrently.</summary>
    void Write(DiagnosticEvent diagnostic);
}

/// <summary>Wants nothing. The ambient default, so no call site ever null-checks a sink.</summary>
public sealed class NullDiagnosticSink : IDiagnosticSink {
    public static readonly NullDiagnosticSink Instance = new();

    private NullDiagnosticSink() {
    }

    public DiagnosticLevel Level => DiagnosticLevel.Off;

    public void Write(DiagnosticEvent diagnostic) {
    }
}
