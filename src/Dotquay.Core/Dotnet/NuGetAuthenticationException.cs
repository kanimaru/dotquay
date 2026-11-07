namespace Dotquay.Core.Dotnet;

/// <summary>
/// A NuGet feed answered 401 or 403 — it wanted credentials dotquay didn't have, or rejected the
/// ones it sent.
/// </summary>
/// <remarks>
/// <para>A distinct type rather than a status-code check on <see cref="HttpRequestException"/>,
/// because two callers need to tell this apart from every other failure and neither should be
/// reaching into another class's HTTP details to do it.</para>
/// <para>The important one is <see cref="MultiSourceNuGetClient"/>. When searching several feeds in
/// order, a 404 means "not here, try the next"; a 401 must <em>not</em>, because "this feed might
/// have the package and won't say" followed by silently taking a same-named package from a public
/// feed is a dependency-confusion substitution — precisely the outcome ordered resolution exists to
/// prevent (SPEC.md §11.1).</para>
/// </remarks>
public sealed class NuGetAuthenticationException : Exception {
    public NuGetAuthenticationException(string sourceName, string sourceUrl, string message)
        : base(message) {
        SourceName = sourceName;
        SourceUrl = sourceUrl;
    }

    /// <summary>The declared source name, so the message can tell the user which feed to log in to
    /// rather than making them match a URL by eye.</summary>
    public string SourceName { get; }

    public string SourceUrl { get; }
}
