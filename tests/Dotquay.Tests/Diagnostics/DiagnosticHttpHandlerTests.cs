using System.Net;
using System.Net.Http.Headers;
using Dotquay.Core.Diagnostics;
using Dotquay.Tests.Support;
using Xunit;

namespace Dotquay.Tests.Diagnostics;

public sealed class DiagnosticHttpHandlerTests {
    [Fact]
    public async Task Logs_method_redacted_url_status_and_auth_scheme_but_never_the_credential() {
        var sink = RecordingDiagnosticSink.Install(out var scope);
        using (scope) {
            using var client = new HttpClient(new DiagnosticHttpHandler(new StubHandler(HttpStatusCode.NotFound)));
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://feed.example/v3/index.json?private_token=abc");
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", "dXNlcjpzZWNyZXQ=");

            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }

        var line = Assert.Single(sink.Messages("http"));
        Assert.Matches(
            @"^GET https://feed\.example/v3/index\.json\?private_token=\*\*\* \[auth: Basic\] -> 404 Not Found after \d+ ms$",
            line);
        Assert.DoesNotContain("dXNlcjpzZWNyZXQ=", sink.AllText);
        Assert.DoesNotContain("abc", sink.AllText);
    }

    [Fact]
    public async Task Says_anonymous_or_token_header_when_there_is_no_Authorization() {
        var sink = RecordingDiagnosticSink.Install(out var scope);
        using (scope) {
            using var client = new HttpClient(new DiagnosticHttpHandler(new StubHandler(HttpStatusCode.OK)));
            using var anonymous = new HttpRequestMessage(HttpMethod.Get, "https://h/a");
            using var tokened = new HttpRequestMessage(HttpMethod.Get, "https://h/b");
            tokened.Headers.Add("PRIVATE-TOKEN", "glpat-secretsecretsecret");

            (await client.SendAsync(anonymous)).Dispose();
            (await client.SendAsync(tokened)).Dispose();
        }

        var lines = sink.Messages("http").ToList();
        Assert.Contains("[anonymous]", lines[0]);
        Assert.Contains("[auth: token header]", lines[1]);
        Assert.DoesNotContain("glpat", sink.AllText);
    }

    [Fact]
    public async Task A_failed_request_is_logged_and_rethrown_unchanged() {
        var sink = RecordingDiagnosticSink.Install(out var scope);
        var failure = new HttpRequestException("No such host is known. (h:443)");

        using (scope) {
            using var client = new HttpClient(new DiagnosticHttpHandler(new StubHandler(failure)));
            var thrown = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("https://h/x"));
            Assert.Same(failure, thrown);
        }

        Assert.Matches(@"^GET https://h/x \[anonymous\] failed after \d+ ms: HttpRequestException: No such host", Assert.Single(sink.Messages("http")));
    }

    [Fact]
    public async Task Is_a_pure_pass_through_when_off() {
        var inner = new StubHandler(HttpStatusCode.Accepted);
        using var client = new HttpClient(new DiagnosticHttpHandler(inner));

        using var response = await client.GetAsync("https://h/x");

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal(1, inner.Calls);
    }

    private sealed class StubHandler : HttpMessageHandler {
        private readonly HttpStatusCode _status;
        private readonly Exception? _failure;

        public StubHandler(HttpStatusCode status) => _status = status;

        public StubHandler(Exception failure) => _failure = failure;

        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
            Calls++;
            return _failure is null
                ? Task.FromResult(new HttpResponseMessage(_status) { RequestMessage = request })
                : Task.FromException<HttpResponseMessage>(_failure);
        }
    }
}
