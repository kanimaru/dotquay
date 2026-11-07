using Dotquay.Core.Abstractions;
using Dotquay.Core.Dotnet;
using Dotquay.Core.Manifest;
using Xunit;

namespace Dotquay.Tests.Dotnet;

/// <summary>
/// Covers the policy that decides which addon-declared NuGet feeds a project takes on
/// (SPEC.md §11.3). This is a real security boundary, so every branch is asserted rather than
/// reasoned about.
/// </summary>
public class SourceAdopterTests {
    private const string FeedUrl =
        "https://gitlab.kemomi.de/api/v4/groups/7/-/packages/nuget/index.json";

    private sealed class ApproveAll : ISourceApproval {
        public List<SourceAdoptionRequest> Seen { get; } = [];

        public bool Approve(SourceAdoptionRequest request) {
            Seen.Add(request);
            return true;
        }
    }

    private static SourceAdopter.Result Evaluate(
        ProjectManifest manifest, ISourceApproval? approval = null, NuGetSourceSpec? source = null,
        string addon = "vbeami") {
        var declared = source ?? new NuGetSourceSpec("vbeami", FeedUrl);
        return new SourceAdopter(approval).Evaluate(
            manifest,
            [(addon, declared)],
            new Dictionary<string,
                IReadOnlyList<string>>(StringComparer.Ordinal) { [addon] = ["VBeami.Core"] });
    }

    [Fact]
    public void Evaluate_UntrustedHostWithNoApprover_IsRefused() {
        // The default everywhere that can't ask a human: CI, --json, the editor plugin.
        var result = Evaluate(new ProjectManifest());

        Assert.Empty(result.Adopted);
        var refused = Assert.Single(result.Refused);
        Assert.Equal("vbeami", refused.AddonName);
        Assert.Equal(["VBeami.Core"], refused.PackageIds);
    }

    [Fact]
    public void Evaluate_TrustedHost_IsAdoptedWithoutAsking() {
        var approval = new ApproveAll();

        var result = Evaluate(
            new ProjectManifest { TrustedHosts = ["gitlab.kemomi.de"] }, approval);

        Assert.Single(result.Adopted);
        Assert.Empty(result.Refused);

        // Trust means the question is never asked, not that it's asked and auto-answered.
        Assert.Empty(approval.Seen);
    }

    [Fact]
    public void Evaluate_AdoptedSource_RecordsWhichAddonAskedForIt() {
        var result = Evaluate(new ProjectManifest { TrustedHosts = ["gitlab.kemomi.de"] });

        Assert.Equal("vbeami", Assert.Single(result.Adopted).DeclaredByAddon);
    }

    [Fact]
    public void Evaluate_UntrustedHostButApproved_IsAdopted() {
        var approval = new ApproveAll();

        var result = Evaluate(new ProjectManifest(), approval);

        Assert.Single(result.Adopted);
        Assert.Single(approval.Seen);
    }

    [Fact]
    public void Evaluate_TrustedHostMatchIsExact_NotBySuffix() {
        // A trusted "kemomi.de" must not cover "evil-kemomi.de" or "gitlab.kemomi.de.attacker.com".
        Assert.False(SourceAdopter.IsTrusted(["kemomi.de"], "https://gitlab.kemomi.de/x"));
        Assert.False(SourceAdopter.IsTrusted(["gitlab.kemomi.de"],
            "https://gitlab.kemomi.de.attacker.com/x"));
        Assert.False(SourceAdopter.IsTrusted(["gitlab.kemomi.de"],
            "https://evil-gitlab.kemomi.de/x"));
        Assert.True(SourceAdopter.IsTrusted(["gitlab.kemomi.de"], "https://gitlab.kemomi.de/x"));
    }

    [Fact]
    public void Evaluate_TrustedHostIgnoresCaseAndScheme_ButNotPort() {
        Assert.True(SourceAdopter.IsTrusted(["GitLab.Kemomi.DE"], "https://gitlab.kemomi.de/x"));
        Assert.True(SourceAdopter.IsTrusted(["https://gitlab.kemomi.de"],
            "https://gitlab.kemomi.de/x"));

        // A different port is a different service, and may well be a different owner.
        Assert.False(SourceAdopter.IsTrusted(["gitlab.kemomi.de"],
            "https://gitlab.kemomi.de:8443/x"));
    }

    [Fact]
    public void Evaluate_EmptyTrustedHosts_TrustsNothing() {
        Assert.False(SourceAdopter.IsTrusted([], "https://gitlab.kemomi.de/x"));
    }

    [Fact]
    public void Evaluate_SourceTheProjectAlreadyDeclares_IsNeitherAdoptedNorAsked() {
        var approval = new ApproveAll();

        // Matched on URL, not name: the project calls it "kemomi", the addon calls it "vbeami".
        var result = Evaluate(
            new ProjectManifest { NuGetSources = [new NuGetSourceSpec("kemomi", FeedUrl)] },
            approval);

        Assert.Empty(result.Adopted);
        Assert.Empty(result.Refused);
        Assert.Empty(approval.Seen);
    }

    [Fact]
    public void Evaluate_AdoptedNameCollidesWithAnExistingSource_IsRenamed() {
        // Two [nuget.sources.x] blocks is a parse error, and silently overwriting the project's own
        // source with an addon's would be worse than either.
        var manifest = new ProjectManifest {
            TrustedHosts = ["gitlab.kemomi.de"],
            NuGetSources = [new NuGetSourceSpec("vbeami",
                "https://gitlab.kemomi.de/api/v4/projects/9/packages/nuget/index.json")],
        };

        var adopted = Assert.Single(Evaluate(manifest).Adopted);

        Assert.NotEqual("vbeami", adopted.Name);
        Assert.StartsWith("vbeami", adopted.Name, StringComparison.Ordinal);
    }

    [Fact]
    public void Evaluate_SameFeedDeclaredByTwoAddons_IsAdoptedOnce() {
        var adopter = new SourceAdopter(new ApproveAll());

        var result = adopter.Evaluate(
            new ProjectManifest(),
            [("vbeami", new NuGetSourceSpec("vbeami", FeedUrl)),
                ("botlib", new NuGetSourceSpec("botlib", FeedUrl))],
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal));

        Assert.Single(result.Adopted);
    }

    [Fact]
    public void Evaluate_OwnedPatternsSurviveAdoption() {
        var source = new NuGetSourceSpec("vbeami", FeedUrl) { Packages = ["VBeami.*"] };

        var adopted = Assert.Single(
            Evaluate(new ProjectManifest { TrustedHosts = ["gitlab.kemomi.de"] }, source: source)
            .Adopted);

        Assert.Equal(["VBeami.*"], adopted.Packages);
    }
}
