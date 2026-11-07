using System.Net;
using Dotquay.Core.SelfUpdate;
using Xunit;

namespace Dotquay.Tests.SelfUpdate;

/// <summary>
/// What dotquay says when GitLab refuses.
/// </summary>
/// <remarks>
/// These exist because the first version of this message guessed. It told a user their token
/// probably lacked <c>read_api</c>; the token was a fine-grained one that read the package registry
/// perfectly well and was missing one project permission — and GitLab had said exactly that, in the
/// response body, which the client discarded. An error message that sounds authoritative and is
/// wrong costs more than one that says nothing.
/// </remarks>
public class GitLabErrorExplanationTests {
    private const string Host = "gitlab.example";

    private const string Granular =
        "Access denied: This operation requires a fine-grained personal access token with the "
        + "following project permissions: [Release: Read].";

    [Fact]
    public void GitLabs_own_words_are_passed_through_verbatim() {
        var message = GitLabErrorExplanation.Describe(
            HttpStatusCode.Forbidden, Granular, authenticated: true, Host,
            "looking up the newest release");

        Assert.Contains("[Release: Read]", message);
        Assert.Contains("403", message);
        Assert.Contains("looking up the newest release", message);

        // The guess that started all this must not come back.
        Assert.DoesNotContain("read_api", message);
    }

    [Fact]
    public void A_granular_refusal_points_at_a_permission_not_at_a_bad_token() {
        var message = GitLabErrorExplanation.Describe(
            HttpStatusCode.Forbidden, Granular, authenticated: true, Host,
            "looking up the newest release");

        Assert.Contains("not ticked", message);
    }

    [Fact]
    public void With_no_body_an_authenticated_refusal_offers_both_token_shapes() {
        var message = GitLabErrorExplanation.Describe(
            HttpStatusCode.Forbidden, detail: null, authenticated: true, Host,
            "looking up the newest release");

        Assert.Contains("fine-grained", message);
        Assert.Contains("read_api", message);
    }

    [Fact]
    public void Without_a_credential_both_readings_stay_on_the_table() {
        var message = GitLabErrorExplanation.Describe(
            HttpStatusCode.NotFound, detail: null, authenticated: false, Host,
            "looking up the newest release");

        Assert.Contains($"dotquay login {Host}", message);
        Assert.Contains("does not exist", message);

        // The channel is configurable, so this can be someone's own mirror - and dotquay's own
        // project is expected to be public once it ships. Asserting that a channel is private is
        // asserting something this code cannot know.
        Assert.DoesNotContain("is private", message);
    }

    [Fact]
    public void With_a_credential_a_404_blames_the_url_not_the_login() {
        // GitLab denies "does not exist" and "not allowed to see" identically, so a 404 is
        // ambiguous only while anonymous. With a credential accepted this far, telling someone to
        // log in is advice that cannot work.
        var message = GitLabErrorExplanation.Describe(
            HttpStatusCode.NotFound, detail: null, authenticated: true, Host,
            "looking up the newest release");

        Assert.Contains("numeric ID", message);
        Assert.DoesNotContain("dotquay login", message);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, true, true)]
    [InlineData(HttpStatusCode.Forbidden, true, true)]
    [InlineData(HttpStatusCode.NotFound, false, true)]
    [InlineData(HttpStatusCode.NotFound, true, false)]
    [InlineData(HttpStatusCode.InternalServerError, true, false)]
    public void Auth_failures_are_classified_so_a_ui_can_offer_a_login(
        HttpStatusCode status, bool authenticated, bool expected) =>
        Assert.Equal(expected, GitLabErrorExplanation.IsAuthFailure(status, authenticated));

    [Fact]
    public void GitLabs_bare_status_echo_does_not_crowd_out_the_advice() {
        // The literal message a second developer's PC printed. GitLab's body was
        // {"message":"401 Unauthorized"}, quoted as if it explained something, and the sentence
        // saying a stored credential had been sent and refused never appeared.
        var message = GitLabErrorExplanation.Describe(
            HttpStatusCode.Unauthorized, "401 Unauthorized", authenticated: true, Host,
            "looking up the newest release");

        Assert.DoesNotContain("GitLab said", message);
        Assert.Contains("does not recognise", message);
        Assert.Contains($"dotquay login {Host}", message);
    }

    [Fact]
    public void An_authenticated_401_blames_the_token_not_its_scopes() {
        // 403 is what a real token lacking a permission gets; 401 means GitLab does not know the
        // token at all, so scope advice would be a confident wrong guess.
        var message = GitLabErrorExplanation.Describe(
            HttpStatusCode.Unauthorized, detail: null, authenticated: true, Host,
            "looking up the newest release");

        Assert.Contains("mistyped, expired or revoked", message);
        Assert.DoesNotContain("read_api", message);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "401 Unauthorized", true)]
    [InlineData(HttpStatusCode.Forbidden, "403 Forbidden", true)]
    [InlineData(HttpStatusCode.NotFound, "404 Not found", true)]
    [InlineData(HttpStatusCode.NotFound, "  404  ", true)]
    [InlineData(HttpStatusCode.NotFound, "404 Project Not Found", false)]
    [InlineData(HttpStatusCode.Unauthorized, "403 Forbidden", false)]
    [InlineData(HttpStatusCode.Forbidden, "requires [Release: Read]", false)]
    [InlineData(HttpStatusCode.Forbidden, null, false)]
    public void Only_an_exact_restatement_of_the_status_counts_as_an_echo(
        HttpStatusCode status, string? detail, bool expected) =>
        Assert.Equal(expected, GitLabErrorExplanation.IsStatusEcho(status, detail));
}
