using System.Diagnostics;

namespace Dotquay.Core.Diagnostics;

/// <summary>
/// Logs every HTTP request dotquay makes - method, redacted URL, status and duration - and nothing
/// else about it.
/// </summary>
/// <remarks>
/// <para>
/// A <see cref="DelegatingHandler"/> rather than log lines at each <c>SendAsync</c> call site, so the
/// NuGet client, the GitLab release channel and the credential verifier all report the same way and
/// a fourth HTTP caller added later is covered by using <see cref="DiagnosticHttp.CreateClient"/>.
/// </para>
/// <para>
/// Headers are never logged, not even redacted: the interesting ones (<c>Authorization</c>,
/// <c>PRIVATE-TOKEN</c>) are the secret, and a redaction rule that missed a new header name would
/// leak a token into a log someone pastes into an issue. Whether a credential was attached at all is
/// what actually diagnoses a 401 vs a 404 on GitLab (see the GitLab notes in docs/DESIGN-v0.7-AUTH.md),
/// so that - and only the scheme - is said.
/// </para>
/// </remarks>
public sealed class DiagnosticHttpHandler : DelegatingHandler {
    public DiagnosticHttpHandler(HttpMessageHandler innerHandler)
        : base(innerHandler) {
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
        if (!DiagnosticLog.IsEnabled(DiagnosticLevel.Verbose)) {
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }

        var target = $"{request.Method} {SecretRedactor.Redact(request.RequestUri?.ToString())}";
        var credential = DescribeCredential(request);
        var started = Stopwatch.GetTimestamp();

        try {
            var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var elapsed = DiagnosticLog.FormatDuration(Stopwatch.GetElapsedTime(started));
            DiagnosticLog.Write(
                DiagnosticLevel.Verbose,
                "http",
                $"{target}{credential} -> {(int)response.StatusCode} {response.ReasonPhrase} after {elapsed}");
            return response;
        } catch (Exception ex) {
            var elapsed = DiagnosticLog.FormatDuration(Stopwatch.GetElapsedTime(started));
            DiagnosticLog.Write(
                DiagnosticLevel.Verbose,
                "http",
                $"{target}{credential} failed after {elapsed}: {ex.GetType().Name}: {SecretRedactor.Redact(ex.Message)}");
            throw;
        }
    }

    private static string DescribeCredential(HttpRequestMessage request) {
        if (request.Headers.Authorization is { } authorization) {
            return $" [auth: {authorization.Scheme}]";
        }

        // GitLab's own token header, used by the release channel.
        return request.Headers.Contains("PRIVATE-TOKEN") || request.Headers.Contains("JOB-TOKEN")
            ? " [auth: token header]"
            : " [anonymous]";
    }
}

/// <summary>Builds the <see cref="HttpClient"/> every Core HTTP caller defaults to.</summary>
public static class DiagnosticHttp {
    /// <summary>
    /// Behaves exactly like <c>new HttpClient()</c> - same <see cref="HttpClientHandler"/>, same
    /// system proxy, same defaults - with <see cref="DiagnosticHttpHandler"/> in front, which is a
    /// pass-through when diagnostics are off.
    /// </summary>
    public static HttpClient CreateClient() => new(new DiagnosticHttpHandler(new HttpClientHandler()));
}
