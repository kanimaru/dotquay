namespace Dotquay.Core.Dotnet;

/// <summary>
/// A NuGet feed could not be used: it answered with an unexpected status, or couldn't be reached at
/// all.
/// </summary>
/// <remarks>
/// <para>Separate from <see cref="NuGetAuthenticationException"/> because the two need different
/// advice, and separate from a bare <see cref="System.Net.Http.HttpRequestException"/> because that
/// escaped the CLI as an unhandled exception — a stack trace on stderr and, under <c>--json</c>, no
/// envelope at all, which silently breaks the contract the editor plugin depends on (SPEC.md §17).</para>
/// <para>A failure discovering the <em>service index</em> is always fatal, never a reason to try the
/// next feed. The feed is misconfigured, unreachable, or invisible to the current credential, and
/// quietly resolving the package from a public feed instead is the substitution ordered resolution
/// exists to prevent (SPEC.md §11.1).</para>
/// </remarks>
public sealed class NuGetSourceException : Exception {
    public NuGetSourceException(string sourceName, string sourceUrl, string message, Exception? innerException = null)
        : base(message, innerException) {
        SourceName = sourceName;
        SourceUrl = sourceUrl;
    }

    public string SourceName { get; }

    public string SourceUrl { get; }
}
