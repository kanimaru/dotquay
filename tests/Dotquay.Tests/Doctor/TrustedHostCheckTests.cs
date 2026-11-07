using Dotquay.Core.Dotnet;
using Xunit;

namespace Dotquay.Tests.Doctor;

/// <summary>
/// Covers the <c>trusted_hosts</c> sanity check. Every case here fails silently in production —
/// a wildcard or typo matches nothing and the user is prompted forever without knowing why — which is
/// exactly why <c>doctor</c> has to say something.
/// </summary>
public class TrustedHostCheckTests {
    [Theory]
    [InlineData("gitlab.kemomi.de")]
    [InlineData("https://gitlab.kemomi.de")]
    [InlineData("gitlab.kemomi.de:8443")]
    [InlineData("http://localhost:5000")]
    public void DescribeTrustedHostProblem_UsableEntries_ReportNothing(string entry) {
        Assert.Null(SourceAdopter.DescribeTrustedHostProblem(entry));
    }

    [Theory]
    [InlineData("*.kemomi.de")]
    [InlineData("gitlab.*.de")]
    [InlineData("gitlab.kemomi.?e")]
    public void DescribeTrustedHostProblem_Wildcard_SaysItTrustsNothing(string entry) {
        var problem = SourceAdopter.DescribeTrustedHostProblem(entry);

        // The dangerous misreading is that this trusts a whole domain. It trusts nothing at all.
        Assert.Contains("trusts nothing", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void DescribeTrustedHostProblem_EntryWithAPath_SaysItTrustsTheWholeHost() {
        var problem = SourceAdopter.DescribeTrustedHostProblem("gitlab.kemomi.de/wild-alliance");

        // Broader than it reads: only the host is compared, so the path is decoration.
        Assert.Contains("whole of gitlab.kemomi.de", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void DescribeTrustedHostProblem_PlainHttp_IsFlagged() {
        Assert.Contains(
            "cleartext", SourceAdopter.DescribeTrustedHostProblem("http://gitlab.kemomi.de")!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ssh://gitlab.kemomi.de")]
    public void DescribeTrustedHostProblem_Unusable_IsReported(string entry) {
        Assert.NotNull(SourceAdopter.DescribeTrustedHostProblem(entry));
    }

    [Fact]
    public void DescribeTrustedHostProblem_LoopbackHttp_IsAllowed() {
        // Local testing against a throwaway feed shouldn't be nagged about.
        Assert.Null(SourceAdopter.DescribeTrustedHostProblem("http://127.0.0.1:8971"));
    }
}
