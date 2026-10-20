using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;

namespace Dotquay.Core.Auth;

/// <summary>
/// Checks a credential against the server before <c>dotquay login</c> stores it (SPEC.md §11.2).
/// </summary>
/// <remarks>
/// Worth the round trip: without it, a mistyped token is accepted silently and surfaces three
/// commands later as a restore failure, at which point the token is the last thing anyone suspects.
/// Verifying at the moment of entry puts the error next to the typo.
/// </remarks>
public sealed class CredentialVerifier
{
    private readonly HttpClient _httpClient;

    public CredentialVerifier(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? Diagnostics.DiagnosticHttp.CreateClient();
    }

    public enum Result
    {
        /// <summary>The server accepted the credential.</summary>
        Accepted,

        /// <summary>The server rejected it — 401 or 403.</summary>
        Rejected,

        /// <summary>Couldn't tell: the request failed for some other reason (offline, DNS, TLS, a
        /// 500). Reported separately from <see cref="Rejected"/> so a network hiccup doesn't get
        /// reported to the user as a bad token.</summary>
        Inconclusive,
    }

    public async Task<(Result Outcome, string? Detail)> VerifyAsync(
        string url, Credential credential, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{credential.Username}:{credential.Token}")));

        try
        {
            using var response = await _httpClient.SendAsync(request, cancellationToken);

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return (Result.Rejected, $"the server answered {(int)response.StatusCode} {response.StatusCode}");
            }

            // Anything that isn't an auth rejection counts as accepted. A 404 here means the URL is
            // wrong, not the credential, and refusing to store a working token because a path was
            // mistyped would be answering a question nobody asked.
            return response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.NotFound
                ? (Result.Accepted, null)
                : (Result.Inconclusive, $"the server answered {(int)response.StatusCode} {response.StatusCode}");
        }
        catch (HttpRequestException ex)
        {
            return (Result.Inconclusive, ex.Message);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return (Result.Inconclusive, "the request timed out");
        }
    }
}
