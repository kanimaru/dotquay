using System.Net;
using System.Net.Http;
using System.Text;
using Dotquay.Core.Auth;
using Xunit;

namespace Dotquay.Tests.Auth;

public class CredentialVerifierTests
{
    private const string Feed = "https://gitlab.example.com/api/v4/projects/124/packages/nuget/index.json";
    private const string Remote = "https://gitlab.example.com/kani/widget.git/info/refs?service=git-upload-pack";

    private static readonly Credential Kani = new("kani", "glpat-secret");

    [Fact]
    public async Task VerifyAsync_SendsBasicAuthWithUsernameAndToken()
    {
        var handler = new RoutingHandler { [Feed] = HttpStatusCode.OK };

        await new CredentialVerifier(new HttpClient(handler)).VerifyAsync(Feed, Kani);

        var header = Assert.Single(handler.Requests).Headers.Authorization!;
        Assert.Equal("Basic", header.Scheme);
        Assert.Equal("kani:glpat-secret", Encoding.UTF8.GetString(Convert.FromBase64String(header.Parameter!)));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task VerifyAsync_AuthFailure_IsRejected(HttpStatusCode status)
    {
        var handler = new RoutingHandler { [Feed] = status };

        var (outcome, detail) = await new CredentialVerifier(new HttpClient(handler)).VerifyAsync(Feed, Kani);

        Assert.Equal(CredentialVerifier.Result.Rejected, outcome);
        Assert.Contains(((int)status).ToString(), detail);
    }

    [Theory]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task VerifyAsync_SuccessOrWrongPath_IsAccepted(HttpStatusCode status)
    {
        var handler = new RoutingHandler { [Feed] = status };

        var (outcome, _) = await new CredentialVerifier(new HttpClient(handler)).VerifyAsync(Feed, Kani);

        Assert.Equal(CredentialVerifier.Result.Accepted, outcome);
    }

    [Fact]
    public async Task VerifyAsync_ServerError_IsInconclusive()
    {
        var handler = new RoutingHandler { [Feed] = HttpStatusCode.InternalServerError };

        var (outcome, _) = await new CredentialVerifier(new HttpClient(handler)).VerifyAsync(Feed, Kani);

        Assert.Equal(CredentialVerifier.Result.Inconclusive, outcome);
    }

    [Fact]
    public async Task VerifyAsync_TransportFailure_IsInconclusive()
    {
        var handler = new RoutingHandler { Failure = new HttpRequestException("no such host") };

        var (outcome, detail) = await new CredentialVerifier(new HttpClient(handler)).VerifyAsync(Feed, Kani);

        Assert.Equal(CredentialVerifier.Result.Inconclusive, outcome);
        Assert.Equal("no such host", detail);
    }

    [Fact]
    public async Task VerifyAnyAsync_RejectionAnywhere_IsRejectedAndNamesTheUrl()
    {
        var handler = new RoutingHandler
        {
            [Feed] = HttpStatusCode.BadGateway,
            [Remote] = HttpStatusCode.Unauthorized,
        };

        var (outcome, detail) = await new CredentialVerifier(new HttpClient(handler))
            .VerifyAnyAsync([Feed, Remote], Kani);

        Assert.Equal(CredentialVerifier.Result.Rejected, outcome);
        Assert.Contains(Remote, detail);
    }

    [Fact]
    public async Task VerifyAnyAsync_StopsAtTheFirstDecisiveAnswer()
    {
        var handler = new RoutingHandler
        {
            [Feed] = HttpStatusCode.OK,
            [Remote] = HttpStatusCode.Unauthorized,
        };

        var (outcome, detail) = await new CredentialVerifier(new HttpClient(handler))
            .VerifyAnyAsync([Feed, Remote], Kani);

        Assert.Equal(CredentialVerifier.Result.Accepted, outcome);
        Assert.Null(detail);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task VerifyAnyAsync_EverythingInconclusive_IsInconclusiveWithTheLastDetail()
    {
        var handler = new RoutingHandler
        {
            [Feed] = HttpStatusCode.InternalServerError,
            [Remote] = HttpStatusCode.ServiceUnavailable,
        };

        var (outcome, detail) = await new CredentialVerifier(new HttpClient(handler))
            .VerifyAnyAsync([Feed, Remote], Kani);

        Assert.Equal(CredentialVerifier.Result.Inconclusive, outcome);
        Assert.Contains("503", detail);
        Assert.Contains(Remote, detail);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task VerifyAnyAsync_NoUrls_IsInconclusiveWithoutTouchingTheNetwork()
    {
        var handler = new RoutingHandler();

        var (outcome, _) = await new CredentialVerifier(new HttpClient(handler)).VerifyAnyAsync([], Kani);

        Assert.Equal(CredentialVerifier.Result.Inconclusive, outcome);
        Assert.Empty(handler.Requests);
    }

    /// <summary>Answers each URL with its configured status (404 for an unknown one), or fails every
    /// request with <see cref="Failure"/> when set. Records the requests for assertions.</summary>
    private sealed class RoutingHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, HttpStatusCode> _routes = new(StringComparer.Ordinal);

        public List<HttpRequestMessage> Requests { get; } = [];

        public Exception? Failure { get; init; }

        public HttpStatusCode this[string url]
        {
            set => _routes[url] = value;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);

            if (Failure is not null)
            {
                return Task.FromException<HttpResponseMessage>(Failure);
            }

            var status = _routes.TryGetValue(request.RequestUri!.ToString(), out var configured)
                ? configured
                : HttpStatusCode.NotFound;
            return Task.FromResult(new HttpResponseMessage(status) { RequestMessage = request });
        }
    }
}
