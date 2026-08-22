using System.Net;
using Dependi.Core.Dotnet;
using Xunit;

namespace Dependi.Tests.Dotnet;

/// <summary>
/// Exercises <see cref="NuGetV3Client"/>'s actual HTTP + JSON parsing path (service-index discovery,
/// then the discovered flat-container-style resource) against a canned
/// <see cref="HttpMessageHandler"/> — deliberately not just trusting
/// <see cref="Support.FakeNuGetClient"/> everywhere else, since that fake bypasses this exact code
/// path. Also covers the two things public nuget.org alone would never exercise: a private-feed
/// service index that publishes its resources at different URLs than nuget.org's (GitLab shape,
/// confirmed for real against the user's own private feed), and Basic-auth header attachment.
/// </summary>
public class NuGetV3ClientTests
{
    private const string ServiceIndexUrl = "https://gitlab.example.com/api/v4/projects/1/packages/nuget/index.json";

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

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, (HttpStatusCode Status, string? Body)> _responsesByUrl;
        public List<HttpRequestMessage> Requests { get; } = [];

        public RecordingHandler(Dictionary<string, (HttpStatusCode, string?)> responsesByUrl)
        {
            _responsesByUrl = responsesByUrl;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var url = request.RequestUri!.ToString();

            if (!_responsesByUrl.TryGetValue(url, out var canned))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }

            var response = new HttpResponseMessage(canned.Item1)
            {
                Content = canned.Item2 is null ? null : new StringContent(canned.Item2),
            };
            return Task.FromResult(response);
        }
    }

    [Fact]
    public async Task GetVersionsAsync_DiscoversPackageBaseAddressFromServiceIndex_ThenQueriesIt()
    {
        var handler = new RecordingHandler(new()
        {
            [ServiceIndexUrl] = (HttpStatusCode.OK, GitLabShapedServiceIndex),
            ["https://gitlab.example.com/api/v4/projects/1/packages/nuget/download/acme.internal/index.json"] =
                (HttpStatusCode.OK, """{"versions":["1.0.0","2.1.0"]}"""),
        });
        var client = new NuGetV3Client(ServiceIndexUrl, httpClient: new HttpClient(handler));

        var versions = await client.GetVersionsAsync("Acme.Internal");

        Assert.Equal(["1.0.0", "2.1.0"], versions);
    }

    [Fact]
    public async Task GetVersionsAsync_CachesDiscoveredResource_OnlyFetchesServiceIndexOnce()
    {
        var handler = new RecordingHandler(new()
        {
            [ServiceIndexUrl] = (HttpStatusCode.OK, GitLabShapedServiceIndex),
            ["https://gitlab.example.com/api/v4/projects/1/packages/nuget/download/acme.a/index.json"] = (HttpStatusCode.OK, """{"versions":["1.0.0"]}"""),
            ["https://gitlab.example.com/api/v4/projects/1/packages/nuget/download/acme.b/index.json"] = (HttpStatusCode.OK, """{"versions":["1.0.0"]}"""),
        });
        var client = new NuGetV3Client(ServiceIndexUrl, httpClient: new HttpClient(handler));

        await client.GetVersionsAsync("Acme.A");
        await client.GetVersionsAsync("Acme.B");

        Assert.Equal(1, handler.Requests.Count(r => r.RequestUri!.ToString() == ServiceIndexUrl));
    }

    [Fact]
    public async Task GetVersionsAsync_WithCredentials_SendsBasicAuthOnEveryRequest()
    {
        var handler = new RecordingHandler(new()
        {
            [ServiceIndexUrl] = (HttpStatusCode.OK, GitLabShapedServiceIndex),
            ["https://gitlab.example.com/api/v4/projects/1/packages/nuget/download/acme.internal/index.json"] =
                (HttpStatusCode.OK, """{"versions":["1.0.0"]}"""),
        });
        var client = new NuGetV3Client(ServiceIndexUrl, username: "kani", password: "glpat-secret", httpClient: new HttpClient(handler));

        await client.GetVersionsAsync("Acme.Internal");

        Assert.All(handler.Requests, request =>
        {
            Assert.NotNull(request.Headers.Authorization);
            Assert.Equal("Basic", request.Headers.Authorization!.Scheme);
        });

        var expectedToken = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("kani:glpat-secret"));
        Assert.Equal(expectedToken, handler.Requests[0].Headers.Authorization!.Parameter);
    }

    [Fact]
    public async Task GetVersionsAsync_NoCredentials_SendsNoAuthorizationHeader()
    {
        var handler = new RecordingHandler(new()
        {
            [ServiceIndexUrl] = (HttpStatusCode.OK, GitLabShapedServiceIndex),
            ["https://gitlab.example.com/api/v4/projects/1/packages/nuget/download/acme.internal/index.json"] =
                (HttpStatusCode.OK, """{"versions":["1.0.0"]}"""),
        });
        var client = new NuGetV3Client(ServiceIndexUrl, httpClient: new HttpClient(handler));

        await client.GetVersionsAsync("Acme.Internal");

        Assert.All(handler.Requests, request => Assert.Null(request.Headers.Authorization));
    }

    [Fact]
    public async Task GetVersionsAsync_PackageNotFound_ReturnsEmptyRatherThanThrowing()
    {
        var handler = new RecordingHandler(new()
        {
            [ServiceIndexUrl] = (HttpStatusCode.OK, GitLabShapedServiceIndex),
            ["https://gitlab.example.com/api/v4/projects/1/packages/nuget/download/does.not.exist/index.json"] = (HttpStatusCode.NotFound, null),
        });
        var client = new NuGetV3Client(ServiceIndexUrl, httpClient: new HttpClient(handler));

        var versions = await client.GetVersionsAsync("Does.Not.Exist");

        Assert.Empty(versions);
    }

    [Fact]
    public async Task GetVersionsAsync_ServiceIndexMissingPackageBaseAddress_Throws()
    {
        var handler = new RecordingHandler(new()
        {
            [ServiceIndexUrl] = (HttpStatusCode.OK, """{"version":"3.0.0","resources":[]}"""),
        });
        var client = new NuGetV3Client(ServiceIndexUrl, httpClient: new HttpClient(handler));

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetVersionsAsync("Acme.Internal"));
    }

    [Fact]
    public async Task GetVersionsAsync_AgainstNuGetOrgShapedIndex_StillWorks()
    {
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
        var handler = new RecordingHandler(new()
        {
            [NuGetV3Client.DefaultServiceIndexUrl] = (HttpStatusCode.OK, nuGetOrgShapedIndex),
            ["https://api.nuget.org/v3-flatcontainer/newtonsoft.json/index.json"] = (HttpStatusCode.OK, """{"versions":["13.0.3","13.0.4"]}"""),
        });
        var client = new NuGetV3Client(httpClient: new HttpClient(handler));

        var versions = await client.GetVersionsAsync("Newtonsoft.Json");

        Assert.Equal(["13.0.3", "13.0.4"], versions);
    }
}
