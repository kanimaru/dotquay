using System.Net;
using Dotquay.Core.Dotnet;
using Xunit;

namespace Dotquay.Tests.Dotnet;

/// <summary>
/// Exercises <see cref="NuGetV3Client"/>'s actual HTTP + JSON parsing path (service-index
/// discovery, then the discovered flat-container-style resource) against a canned
/// <see cref="HttpMessageHandler"/> — deliberately not just trusting
/// <see cref="Support.FakeNuGetClient"/> everywhere else, since that fake bypasses this exact code
/// path. Also covers the two things public nuget.org alone would never exercise: a private-feed
/// service index that publishes its resources at different URLs than nuget.org's (GitLab shape,
/// confirmed for real against the user's own private feed), and Basic-auth header attachment.
/// </summary>
public class NuGetV3ClientTests {
    private const string ServiceIndexUrl =
        "https://gitlab.example.com/api/v4/projects/1/packages/nuget/index.json";

    private const string GitLabShapedServiceIndex = """
        {
          "version": "3.0.0",
          "resources": [
            { "@id": "https://gitlab.example.com/api/v4/projects/1/packages/nuget/query", "@type": "SearchQueryService" },
            { "@id": "https://gitlab.example.com/api/v4/projects/1/packages/nuget/metadata", "@type": "RegistrationsBaseUrl" },
            { "@id": "https://gitlab.example.com/api/v4/projects/1/packages/nuget/download", "@type": "PackageBaseAddress/3.0.0" }
          ]
        }
        """;

    private sealed class RecordingHandler : HttpMessageHandler {
        private readonly Dictionary<string, (HttpStatusCode Status, string? Body)> _responsesByUrl;
        public List<HttpRequestMessage> Requests { get; } = [];

        public RecordingHandler(Dictionary<string, (HttpStatusCode, string?)> responsesByUrl) {
            _responsesByUrl = responsesByUrl;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken) {
            Requests.Add(request);
            var url = request.RequestUri!.ToString();

            if (!_responsesByUrl.TryGetValue(url, out var canned)) {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }

            var response = new HttpResponseMessage(canned.Item1) {
                Content = canned.Item2 is null ? null : new StringContent(canned.Item2),
            };
            return Task.FromResult(response);
        }
    }

    [Fact]
    public async Task GetVersionsAsync_DiscoversPackageBaseAddressFromServiceIndex_ThenQueriesIt() {
        var handler = new RecordingHandler(new() {
            [ServiceIndexUrl] = (HttpStatusCode.OK, GitLabShapedServiceIndex),
            ["https://gitlab.example.com/api/v4/projects/1/packages/nuget/download/"
                + "acme.internal/index.json"] =
                (HttpStatusCode.OK, """{"versions":["1.0.0","2.1.0"]}"""),
        });
        var client = new NuGetV3Client(ServiceIndexUrl, httpClient: new HttpClient(handler));

        var query = await client.GetVersionsAsync("Acme.Internal");

        Assert.Equal(["1.0.0", "2.1.0"], query.Versions);
    }

    [Fact]
    public async Task GetVersionsAsync_CachesDiscoveredResource_OnlyFetchesServiceIndexOnce() {
        var handler = new RecordingHandler(new() {
            [ServiceIndexUrl] = (HttpStatusCode.OK, GitLabShapedServiceIndex),
            ["https://gitlab.example.com/api/v4/projects/1/packages/nuget/download/"
                + "acme.a/index.json"] =
                (HttpStatusCode.OK, """{"versions":["1.0.0"]}"""),
            ["https://gitlab.example.com/api/v4/projects/1/packages/nuget/download/"
                + "acme.b/index.json"] =
                (HttpStatusCode.OK, """{"versions":["1.0.0"]}"""),
        });
        var client = new NuGetV3Client(ServiceIndexUrl, httpClient: new HttpClient(handler));

        await client.GetVersionsAsync("Acme.A");
        await client.GetVersionsAsync("Acme.B");

        Assert.Equal(1, handler.Requests.Count(r => r.RequestUri!.ToString() == ServiceIndexUrl));
    }

    [Fact]
    public async Task GetVersionsAsync_WithCredentials_SendsBasicAuthOnEveryRequest() {
        var handler = new RecordingHandler(new() {
            [ServiceIndexUrl] = (HttpStatusCode.OK, GitLabShapedServiceIndex),
            ["https://gitlab.example.com/api/v4/projects/1/packages/nuget/download/"
                + "acme.internal/index.json"] =
                (HttpStatusCode.OK, """{"versions":["1.0.0"]}"""),
        });
        var client = new NuGetV3Client(ServiceIndexUrl, username: "kani", password: "glpat-secret",
            httpClient: new HttpClient(handler));

        await client.GetVersionsAsync("Acme.Internal");

        Assert.All(handler.Requests, request => {
            Assert.NotNull(request.Headers.Authorization);
            Assert.Equal("Basic", request.Headers.Authorization!.Scheme);
        });

        var expectedToken =
            Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("kani:glpat-secret"));
        Assert.Equal(expectedToken, handler.Requests[0].Headers.Authorization!.Parameter);
    }

    [Fact]
    public async Task GetVersionsAsync_NoCredentials_SendsNoAuthorizationHeader() {
        var handler = new RecordingHandler(new() {
            [ServiceIndexUrl] = (HttpStatusCode.OK, GitLabShapedServiceIndex),
            ["https://gitlab.example.com/api/v4/projects/1/packages/nuget/download/"
                + "acme.internal/index.json"] =
                (HttpStatusCode.OK, """{"versions":["1.0.0"]}"""),
        });
        var client = new NuGetV3Client(ServiceIndexUrl, httpClient: new HttpClient(handler));

        await client.GetVersionsAsync("Acme.Internal");

        Assert.All(handler.Requests, request => Assert.Null(request.Headers.Authorization));
    }

    [Fact]
    public async Task GetVersionsAsync_PackageNotFound_ReturnsEmptyRatherThanThrowing() {
        var handler = new RecordingHandler(new() {
            [ServiceIndexUrl] = (HttpStatusCode.OK, GitLabShapedServiceIndex),
            ["https://gitlab.example.com/api/v4/projects/1/packages/nuget/download/"
                + "does.not.exist/index.json"] =
                (HttpStatusCode.NotFound, null),
        });
        var client = new NuGetV3Client(ServiceIndexUrl, httpClient: new HttpClient(handler));

        var query = await client.GetVersionsAsync("Does.Not.Exist");

        Assert.Empty(query.Versions);
    }

    [Fact]
    public async Task GetVersionsAsync_UnauthorizedOnThePackageQuery_ThrowsTypedAuthException() {
        var handler = new RecordingHandler(new() {
            [ServiceIndexUrl] = (HttpStatusCode.OK, GitLabShapedServiceIndex),
            ["https://gitlab.example.com/api/v4/projects/1/packages/nuget/download/"
                + "acme.internal/index.json"] =
                (HttpStatusCode.Unauthorized, null),
        });
        var client = new NuGetV3Client(ServiceIndexUrl, httpClient: new HttpClient(handler),
            sourceName: "core");

        var exception = await Assert.ThrowsAsync<NuGetAuthenticationException>(
            () => client.GetVersionsAsync("Acme.Internal"));

        Assert.Equal("core", exception.SourceName);
    }

    [Fact]
    public async Task GetVersionsAsync_UnauthorizedOnTheServiceIndex_ThrowsTypedAuthException() {
        // The one that usually fires in practice: on a private feed a bad or missing credential is
        // rejected at the service index, before any package lookup happens at all.
        var handler = new RecordingHandler(new() {
            [ServiceIndexUrl] = (HttpStatusCode.Unauthorized, null),
        });
        var client = new NuGetV3Client(ServiceIndexUrl, httpClient: new HttpClient(handler),
            sourceName: "core");

        var exception = await Assert.ThrowsAsync<NuGetAuthenticationException>(
            () => client.GetVersionsAsync("Acme.Internal"));

        Assert.Contains("dotquay login", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetVersionsAsync_Forbidden_IsTreatedAsAnAuthFailureToo() {
        var handler = new RecordingHandler(new() {
            [ServiceIndexUrl] = (HttpStatusCode.Forbidden, null),
        });
        var client = new NuGetV3Client(ServiceIndexUrl, httpClient: new HttpClient(handler),
            sourceName: "core");

        await Assert.ThrowsAsync<NuGetAuthenticationException>(() =>
            client.GetVersionsAsync("Acme.Internal"));
    }

    [Fact]
    public async Task GetVersionsAsync_ReportsTheSourceName_ButNullForNuGetOrg() {
        var handler = new RecordingHandler(new() {
            [ServiceIndexUrl] = (HttpStatusCode.OK, GitLabShapedServiceIndex),
            ["https://gitlab.example.com/api/v4/projects/1/packages/nuget/download/"
                + "acme.internal/index.json"] =
                (HttpStatusCode.OK, """{"versions":["1.0.0"]}"""),
        });

        var privateQuery = await new NuGetV3Client(
            ServiceIndexUrl, httpClient: new HttpClient(handler), sourceName: "core")
            .GetVersionsAsync("Acme.Internal");

        Assert.Equal("core", privateQuery.SourceName);
    }

    [Fact]
    public async Task GetVersionsAsync_ServiceIndexNotFound_Unauthenticated_SuggestsLoggingIn() {
        // The crash that shipped in the first cut: EnsureSuccessStatusCode's HttpRequestException
        // was caught by nothing and escaped as an unhandled exception — a stack trace on stderr
        // and, with --json, no envelope at all.
        var handler = new RecordingHandler(new() {
            [ServiceIndexUrl] = (HttpStatusCode.NotFound, null),
        });
        var client = new NuGetV3Client(ServiceIndexUrl, httpClient: new HttpClient(handler),
            sourceName: "core");

        var exception = await Assert.ThrowsAsync<NuGetSourceException>(() =>
            client.GetVersionsAsync("Acme.Internal"));

        // GitLab returns 404 both for a project that doesn't exist and for one you can't see, so
        // the message must offer both readings rather than assert one.
        Assert.Contains("doesn't exist", exception.Message, StringComparison.Ordinal);
        Assert.Contains("dotquay login", exception.Message, StringComparison.Ordinal);
        Assert.Equal("core", exception.SourceName);
    }

    [Fact]
    public async Task GetVersionsAsync_ServiceIndexNotFound_Authenticated_BlamesTheUrlNotTheCredential() {
        var handler = new RecordingHandler(new() {
            [ServiceIndexUrl] = (HttpStatusCode.NotFound, null),
        });
        var client = new NuGetV3Client(
            ServiceIndexUrl, "kani", "glpat-token", new HttpClient(handler), sourceName: "core");

        var exception = await Assert.ThrowsAsync<NuGetSourceException>(() =>
            client.GetVersionsAsync("Acme.Internal"));

        // With a credential accepted this far, "log in" is the wrong advice — an admin token that
        // can see everything still gets 404 for a URL that names no project.
        Assert.DoesNotContain("dotquay login", exception.Message, StringComparison.Ordinal);
        Assert.Contains("numeric ID", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetVersionsAsync_BadRequest_PointsAtTheUrlShape() {
        // A URL-encoded project path is accepted by most of GitLab's API but rejected by the NuGet
        // registry endpoint, which wants the numeric ID — observed as a 400, not a 404.
        var handler = new RecordingHandler(new() {
            [ServiceIndexUrl] = (HttpStatusCode.BadRequest, null),
        });
        var client = new NuGetV3Client(ServiceIndexUrl, httpClient: new HttpClient(handler),
            sourceName: "core");

        var exception = await Assert.ThrowsAsync<NuGetSourceException>(() =>
            client.GetVersionsAsync("Acme.Internal"));

        Assert.Contains("numeric ID", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetVersionsAsync_ServerError_ThrowsTypedSourceException() {
        var handler = new RecordingHandler(new() {
            [ServiceIndexUrl] = (HttpStatusCode.InternalServerError, null),
        });
        var client = new NuGetV3Client(ServiceIndexUrl, httpClient: new HttpClient(handler),
            sourceName: "core");

        var exception = await Assert.ThrowsAsync<NuGetSourceException>(() =>
            client.GetVersionsAsync("Acme.Internal"));

        Assert.Contains("down or misconfigured", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetVersionsAsync_TransportFailure_ThrowsTypedSourceExceptionRatherThanHttpRequestException() {
        var client = new NuGetV3Client(
            "https://nonexistent.invalid/v3/index.json",
            httpClient: new HttpClient(new ThrowingHandler()),
            sourceName: "core");

        var exception = await Assert.ThrowsAsync<NuGetSourceException>(() =>
            client.GetVersionsAsync("Acme.Internal"));

        Assert.Contains("could not be reached", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>Simulates DNS failure / refused connection / TLS error.</summary>
    private sealed class ThrowingHandler : HttpMessageHandler {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            throw new HttpRequestException("No such host is known.");
    }

    [Fact]
    public async Task GetVersionsAsync_ServiceIndexMissingPackageBaseAddress_Throws() {
        var handler = new RecordingHandler(new() {
            [ServiceIndexUrl] = (HttpStatusCode.OK, """{"version":"3.0.0","resources":[]}"""),
        });
        var client = new NuGetV3Client(ServiceIndexUrl, httpClient: new HttpClient(handler),
            sourceName: "core");

        // Typed, not InvalidOperationException: found by running the AOT binary against a real URL
        // that answered 200 without a service index, where it escaped as a raw stack trace.
        var exception = await Assert.ThrowsAsync<NuGetSourceException>(() =>
            client.GetVersionsAsync("Acme.Internal"));
        Assert.Contains("isn't a usable V3 feed", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetVersionsAsync_ServiceIndexIsHtml_ThrowsTypedSourceException() {
        // A sign-in page answering 200 with HTML is the realistic shape of this.
        var handler = new RecordingHandler(new() {
            [ServiceIndexUrl] = (HttpStatusCode.OK,
                "<!DOCTYPE html><html><body>Sign in</body></html>"),
        });
        var client = new NuGetV3Client(ServiceIndexUrl, httpClient: new HttpClient(handler),
            sourceName: "core");

        var exception = await Assert.ThrowsAsync<NuGetSourceException>(() =>
            client.GetVersionsAsync("Acme.Internal"));
        Assert.Contains("valid V3 service index", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetVersionsAsync_AgainstNuGetOrgShapedIndex_StillWorks() {
        // nuget.org's own resource list is much longer, but the discovery logic only cares about
        // finding the one entry with @type == "PackageBaseAddress/3.0.0" — same code path as the
        // GitLab-shaped tests above, just proving the "public default" shape isn't special-cased.
        const string nuGetOrgShapedIndex = """
            {
              "version": "3.0.0",
              "resources": [
                { "@id": "https://azuresearch-usnc.nuget.org/query", "@type": "SearchQueryService" },
                { "@id": "https://api.nuget.org/v3-flatcontainer/", "@type": "PackageBaseAddress/3.0.0" }
              ]
            }
            """;
        var handler = new RecordingHandler(new() {
            [NuGetV3Client.DefaultServiceIndexUrl] = (HttpStatusCode.OK, nuGetOrgShapedIndex),
            ["https://api.nuget.org/v3-flatcontainer/newtonsoft.json/index.json"] =
                (HttpStatusCode.OK, """{"versions":["13.0.3","13.0.4"]}"""),
        });
        var client = new NuGetV3Client(httpClient: new HttpClient(handler));

        var query = await client.GetVersionsAsync("Newtonsoft.Json");

        Assert.Equal(["13.0.3", "13.0.4"], query.Versions);

        // Null rather than "nuget.org": the lockfile omits the key for the public feed, so a
        // public-only project's lockfile stays byte-identical to what pre-v0.7 dotquay wrote.
        Assert.Null(query.SourceName);
    }
}
