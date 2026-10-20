using System.Net;
using System.Text;
using Dotquay.Core.Auth;
using Dotquay.Core.SelfUpdate;
using Dotquay.Core.Workflows;
using Dotquay.Tests.Support;
using Xunit;

namespace Dotquay.Tests.SelfUpdate;

/// <summary>
/// How the release channel treats a stored credential GitLab refuses.
/// </summary>
/// <remarks>
/// Written after a second developer's PC answered <c>self check</c> with
/// <c>401 Unauthorized ... GitLab said: 401 Unauthorized</c> against a channel anyone may read
/// anonymously: <c>dotquay login</c> had stored a token for the bare host without checking it, and
/// GitLab rejects a request carrying an unrecognised token even where no token is needed. The fake
/// server below answers the way gitlab.kemomi.de was measured to on 2026-10-20 - anonymous 200 on a
/// public project, 401 <c>{"message":"401 Unauthorized"}</c> for a bogus PRIVATE-TOKEN.
/// </remarks>
public class GitLabReleaseChannelTests
{
    private const string Host = "gitlab.example";
    private const string ProjectApi = $"https://{Host}/api/v4/projects/128";
    private const string ValidToken = "glpat-valid";
    private const string StaleToken = "glpat-stale";

    [Fact]
    public async Task A_refused_token_on_a_public_channel_falls_back_to_anonymous()
    {
        var server = new FakeGitLab(isPublic: true);
        var channel = Channel(server, StaleToken);

        var latest = await channel.GetLatestAsync();

        Assert.NotNull(latest);
        Assert.Equal("9.9.9", latest.Version);
        Assert.True(channel.StoredCredentialRejected);
    }

    [Fact]
    public async Task After_a_fallback_the_refused_token_is_not_offered_again()
    {
        var server = new FakeGitLab(isPublic: true);
        var channel = Channel(server, StaleToken);

        var latest = await channel.GetLatestAsync();
        await channel.DownloadAsync(latest!.Assets.Single());

        // releases (token, refused) -> releases (anonymous) -> manifest -> binary. Offering the
        // refused token again would double every remaining round trip.
        Assert.Equal(
            [true, false, false, false],
            server.Requests.Select(request => request.HadCredential).ToArray());
    }

    [Fact]
    public async Task A_fine_grained_token_missing_a_permission_falls_back_too()
    {
        // A real token without Release: Read gets 403, not 401 - and on a public project anonymous
        // access needs no permission at all.
        var server = new FakeGitLab(isPublic: true, forbidValidToken: true);
        var channel = Channel(server, ValidToken);

        var latest = await channel.GetLatestAsync();

        Assert.Equal("9.9.9", latest!.Version);
        Assert.True(channel.StoredCredentialRejected);
    }

    [Fact]
    public async Task On_a_private_channel_the_refused_token_is_what_gets_reported()
    {
        var server = new FakeGitLab(isPublic: false);
        var channel = Channel(server, StaleToken);

        var exception = await Assert.ThrowsAsync<WorkflowException>(() => channel.GetLatestAsync());

        // The anonymous retry 404s here, and reporting that would blame the URL. The token is the
        // fault, so the 401 and its advice are what the reader sees.
        Assert.Contains("401", exception.Message, StringComparison.Ordinal);
        Assert.Contains("does not recognise", exception.Message, StringComparison.Ordinal);
        Assert.Contains($"dotquay login {Host}", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("GitLab said: 401 Unauthorized", exception.Message, StringComparison.Ordinal);
        Assert.IsType<CredentialException>(exception.InnerException);
        Assert.False(channel.StoredCredentialRejected);
    }

    [Fact]
    public async Task A_working_token_is_sent_on_every_request_and_never_retried()
    {
        var server = new FakeGitLab(isPublic: false);
        var channel = Channel(server, ValidToken);

        var latest = await channel.GetLatestAsync();
        await channel.DownloadAsync(latest!.Assets.Single());

        Assert.All(server.Requests, request => Assert.True(request.HadCredential));
        Assert.Equal(3, server.Requests.Count);
        Assert.False(channel.StoredCredentialRejected);
    }

    [Fact]
    public async Task A_404_with_a_credential_is_not_retried_anonymously()
    {
        // Not about the credential, so an anonymous retry would only turn one answer into two.
        var server = new FakeGitLab(isPublic: false);
        var channel = Channel(server, ValidToken);

        var release = await channel.GetVersionAsync("0.0.1");

        Assert.Null(release);
        Assert.Single(server.Requests);
    }

    [Fact]
    public async Task Without_a_stored_credential_a_public_channel_is_one_anonymous_request()
    {
        var server = new FakeGitLab(isPublic: true);
        var channel = new GitLabReleaseChannel(ProjectApi, TestCredentials.None(), new HttpClient(server));

        var latest = await channel.GetLatestAsync();

        Assert.Equal("9.9.9", latest!.Version);
        Assert.Equal([false, false], server.Requests.Select(request => request.HadCredential).ToArray());
        Assert.False(channel.StoredCredentialRejected);
    }

    private static GitLabReleaseChannel Channel(FakeGitLab server, string token) =>
        new(ProjectApi, TestCredentials.ForHost(Host, "cheese44", token), new HttpClient(server));

    /// <summary>
    /// Just enough of a GitLab instance: one project's releases, the manifest CI uploads, and a
    /// binary - with GitLab's measured behaviour around tokens.
    /// </summary>
    private sealed class FakeGitLab(bool isPublic, bool forbidValidToken = false) : HttpMessageHandler
    {
        private const string ManifestUrl = $"https://{Host}/packages/dotquay/9.9.9/manifest.json";
        private const string BinaryUrl = $"https://{Host}/packages/dotquay/9.9.9/dotquay.exe";

        public List<(string Url, bool HadCredential)> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            var token = request.Headers.TryGetValues("PRIVATE-TOKEN", out var values) ? values.Single() : null;
            var hadCredential = token is not null || request.Headers.Authorization is not null;
            Requests.Add((url, hadCredential));

            // GitLab authenticates whatever credential arrives before it looks at visibility, so
            // an unrecognised token is refused even on a project that needs none.
            if (token is not null && token != ValidToken)
            {
                return Answer(HttpStatusCode.Unauthorized, """{"message":"401 Unauthorized"}""");
            }

            if (token == ValidToken && forbidValidToken)
            {
                return Answer(
                    HttpStatusCode.Forbidden,
                    """{"error":"insufficient_granular_scope","error_description":"requires [Release: Read]"}""");
            }

            if (token is null && !isPublic)
            {
                return Answer(HttpStatusCode.NotFound, """{"message":"404 Project Not Found"}""");
            }

            return url switch
            {
                $"{ProjectApi}/releases?per_page=1" => Answer(
                    HttpStatusCode.OK,
                    $$$"""[{"tag_name":"v9.9.9","assets":{"links":[{"name":"manifest.json","url":"{{{ManifestUrl}}}"}]}}]"""),
                ManifestUrl => Answer(
                    HttpStatusCode.OK,
                    $$$"""{"schema":1,"version":"9.9.9","assets":[{"rid":"win-x64","file_name":"dotquay.exe","url":"{{{BinaryUrl}}}","sha256":"ab"}]}"""),
                BinaryUrl => Answer(HttpStatusCode.OK, "MZ-binary"),
                _ => Answer(HttpStatusCode.NotFound, """{"message":"404 Not found"}"""),
            };
        }

        private static Task<HttpResponseMessage> Answer(HttpStatusCode status, string body) =>
            Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
    }
}