using Dotquay.Core.Dotnet;
using Dotquay.Core.Versioning;
using Dotquay.Tests.Support;
using Xunit;

namespace Dotquay.Tests.Dotnet;

public class NuGetVersionSelectorTests {
    [Fact]
    public async Task SelectLatestStableAsync_PicksHighestNonPrerelease() {
        var client = new FakeNuGetClient();
        client.VersionsByPackageId["Newtonsoft.Json"] = ["13.0.1", "13.0.3", "13.0.4-beta1",
            "12.0.3"];

        var latest = await NuGetVersionSelector.SelectLatestStableAsync(client, "Newtonsoft.Json");

        Assert.Equal("13.0.3", latest);
    }

    [Fact]
    public async Task SelectLatestStableAsync_OnlyPrereleasesAvailable_ReturnsNull() {
        var client = new FakeNuGetClient();
        client.VersionsByPackageId["Some.Preview.Package"] = ["1.0.0-beta1", "1.0.0-beta2"];

        var latest = await NuGetVersionSelector.SelectLatestStableAsync(client,
            "Some.Preview.Package");

        Assert.Null(latest);
    }

    [Fact]
    public async Task SelectLatestStableAsync_UnknownPackage_ReturnsNull() {
        var client = new FakeNuGetClient();

        var latest = await NuGetVersionSelector.SelectLatestStableAsync(client, "Does.Not.Exist");

        Assert.Null(latest);
    }

    [Theory]
    [InlineData("13.0.3", "13.0.3")]
    [InlineData("^13.0", "13.2.0")]
    [InlineData("~13.0", "13.0.3")]
    [InlineData("99.0.0", null)]
    [InlineData("^13.3.0-beta1", "13.3.0-beta2")]
    public void SelectHighestMatching_TakesTheHighestInsideTheRange(string range,
        string? expected) {
        VersionConstraint.TryParse(range, out var constraint);
        string[] versions = ["13.0.1", "13.0.3", "13.2.0", "13.3.0-beta1", "13.3.0-beta2", "14.0.0",
            "not-a-version"];

        Assert.Equal(expected, NuGetVersionSelector.SelectHighestMatching(versions, constraint!));
    }

    [Fact]
    public async Task CheckForNewerStableAsync_NewerStableExists_ReturnsIt() {
        var client = new FakeNuGetClient();
        client.VersionsByPackageId["Newtonsoft.Json"] = ["13.0.3", "13.0.4"];

        var newer = await NuGetVersionSelector.CheckForNewerStableAsync(client, "Newtonsoft.Json",
            "13.0.3");

        Assert.Equal("13.0.4", newer);
    }

    [Fact]
    public async Task CheckForNewerStableAsync_AlreadyAtLatest_ReturnsNull() {
        var client = new FakeNuGetClient();
        client.VersionsByPackageId["Newtonsoft.Json"] = ["13.0.3"];

        var newer = await NuGetVersionSelector.CheckForNewerStableAsync(client, "Newtonsoft.Json",
            "13.0.3");

        Assert.Null(newer);
    }

    [Fact]
    public async Task CheckForNewerStableAsync_NormalizedFormDiffersButValueIsEqual_ReturnsNull() {
        // "13.0.3" and "13.0.3.0" are the same NuGetVersion once parsed — string comparison would
        // wrongly flag this as outdated, which is exactly what this method exists to avoid.
        var client = new FakeNuGetClient();
        client.VersionsByPackageId["Newtonsoft.Json"] = ["13.0.3.0"];

        var newer = await NuGetVersionSelector.CheckForNewerStableAsync(client, "Newtonsoft.Json",
            "13.0.3");

        Assert.Null(newer);
    }

    [Fact]
    public async Task CheckForNewerStableAsync_CurrentVersionUnparsable_ReturnsNull() {
        var client = new FakeNuGetClient();
        client.VersionsByPackageId["Newtonsoft.Json"] = ["13.0.4"];

        var newer = await NuGetVersionSelector.CheckForNewerStableAsync(client, "Newtonsoft.Json",
            "not-a-version");

        Assert.Null(newer);
    }

    [Fact]
    public async Task CheckForNewerStableAsync_NoStableReleaseAtAll_ReturnsNull() {
        var client = new FakeNuGetClient();
        client.VersionsByPackageId["Some.Preview.Package"] = ["1.0.0-beta1"];

        var newer = await NuGetVersionSelector.CheckForNewerStableAsync(client,
            "Some.Preview.Package", "0.9.0");

        Assert.Null(newer);
    }
}
