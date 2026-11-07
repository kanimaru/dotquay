using Dotquay.Cli.Diagnostics;
using Dotquay.Core.Diagnostics;
using Xunit;

namespace Dotquay.Tests.Cli;

public sealed class DiagnosticSettingsTests {
    [Theory]
    [InlineData(false, false, null, DiagnosticLevel.Off)]
    [InlineData(true, false, null, DiagnosticLevel.Verbose)]
    [InlineData(false, true, null, DiagnosticLevel.Trace)]
    [InlineData(true, true, null, DiagnosticLevel.Trace)]
    [InlineData(false, false, "1", DiagnosticLevel.Verbose)]
    [InlineData(false, false, "trace", DiagnosticLevel.Trace)]
    // The more verbose source wins, in either direction.
    [InlineData(true, false, "trace", DiagnosticLevel.Trace)]
    [InlineData(false, true, "1", DiagnosticLevel.Trace)]
    // The variable can't turn a flag off.
    [InlineData(true, false, "0", DiagnosticLevel.Verbose)]
    public void Flags_and_environment_combine_to_the_more_verbose(bool verbose, bool trace, string? environment, DiagnosticLevel expected) {
        Assert.Equal(expected, DiagnosticSettings.Resolve(verbose, trace, environment));
    }

    [Theory]
    [InlineData(null, DiagnosticLevel.Off)]
    [InlineData("", DiagnosticLevel.Off)]
    [InlineData("   ", DiagnosticLevel.Off)]
    [InlineData("0", DiagnosticLevel.Off)]
    [InlineData("false", DiagnosticLevel.Off)]
    [InlineData("OFF", DiagnosticLevel.Off)]
    [InlineData("no", DiagnosticLevel.Off)]
    [InlineData("1", DiagnosticLevel.Verbose)]
    [InlineData("true", DiagnosticLevel.Verbose)]
    [InlineData("verbose", DiagnosticLevel.Verbose)]
    [InlineData("yes please", DiagnosticLevel.Verbose)]
    [InlineData("2", DiagnosticLevel.Trace)]
    [InlineData(" Trace ", DiagnosticLevel.Trace)]
    [InlineData("all", DiagnosticLevel.Trace)]
    public void Environment_values(string? value, DiagnosticLevel expected) {
        Assert.Equal(expected, DiagnosticSettings.ParseEnvironment(value));
    }
}
