using Dotquay.Cli.Commands;
using Xunit;

namespace Dotquay.Tests.Cli;

/// <summary>
/// Covers how <c>dotquay add</c> parses the options that decide an addon's identity and folder.
/// </summary>
public class AddCommandOptionsTests {
    private const string Url = "https://gitlab.kemomi.de/wild-alliance/vbeami/";

    [Theory]
    [InlineData("--install-as")]
    [InlineData("--install_as")]
    public void InstallAs_AcceptsBothSpellings(string flag) {
        // The manifest key and the MCP argument are both install_as; the BotV2 report typed the
        // underscore and got "'--install_as' was not matched" before anything else could happen.
        var result = AddCommand.Build().Parse([Url, "--branch", "main", "--path", "godot/addons/vbeami_vcam", flag, "vbeami_vcam"]);

        Assert.Empty(result.Errors);
        Assert.Equal("vbeami_vcam", result.GetValue<string?>("--install-as"));
    }

    [Fact]
    public void Name_IsParsed() {
        var result = AddCommand.Build().Parse([Url, "--path", "godot/addons/vbeami_vcam", "--name", "camera"]);

        Assert.Empty(result.Errors);
        Assert.Equal("camera", result.GetValue<string?>("--name"));
    }
}
