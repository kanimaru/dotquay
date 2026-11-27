using System.Diagnostics;
using Dotquay.Core.IO;
using Dotquay.Core.Local;
using Xunit;

namespace Dotquay.Tests.Local;

/// <summary>
/// Evaluates a generated overlay with a real MSBuild, because
/// <see cref="LinkedReferenceConditionTests"/> only reads the XML and cannot see whether MSBuild
/// parses the combined condition the way it reads.
/// </summary>
/// <remarks>
/// Evaluation only (<c>-getItem</c>): no restore, no build, no network.
/// </remarks>
public sealed class LinkedReferenceConditionRealProcessTests : IDisposable {
    private const string PackageId = "Test.Server";
    private const int EvaluateTimeoutMilliseconds = 120_000;

    private readonly string _root;
    private readonly string _consumerDirectory;

    public LinkedReferenceConditionRealProcessTests() {
        _root = Directory.CreateTempSubdirectory("dotquay-link-condition-").FullName;
        _consumerDirectory = Path.Combine(_root, "App");
        var linkedDirectory = Path.Combine(_root, "Server");
        Directory.CreateDirectory(_consumerDirectory);
        Directory.CreateDirectory(linkedDirectory);
        File.WriteAllText(Path.Combine(linkedDirectory, "Server.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\" />");

        // Debug-only in a group, Windows-only on a Choose's Otherwise: both shapes the combined
        // condition has to keep, including the negation.
        File.WriteAllText(Path.Combine(_consumerDirectory, "App.csproj"), $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
              <ItemGroup Condition="'$(Configuration)' == 'Debug'">
                <PackageReference Include="{PackageId}" Version="1.0.0" />
              </ItemGroup>
              <Choose>
                <When Condition="'$(Configuration)' == 'Debug'" />
                <Otherwise>
                  <ItemGroup>
                    <PackageReference Include="{PackageId}" Version="1.0.0"
                        Condition="'$(Flavour)' == 'Testing'" />
                  </ItemGroup>
                </Otherwise>
              </Choose>
            </Project>
            """);

        var links = new Dictionary<string, DotnetLink> {
            [PackageId] = new DotnetLink(PackageId, Path.Combine(linkedDirectory, "Server.csproj")),
        };
        LocalTargetsWriter.Sync(new SystemFileSystem(), _consumerDirectory, links);
    }

    [Theory]
    [InlineData("Debug", "", true)]
    [InlineData("Release", "", false)]
    [InlineData("ExportRelease", "", false)]
    [InlineData("Release", "Testing", true)]
    public async Task TheLinkedReference_AppliesWhereThePackageWould(
        string configuration, string flavour, bool expected) {
        var (exitCode, output) = await EvaluateAsync(
            $"-p:Configuration={configuration}", $"-p:Flavour={flavour}");

        Assert.True(exitCode == 0, output);
        Assert.Equal(expected, output.Contains("Server.csproj", StringComparison.Ordinal));
        Assert.DoesNotContain($"\"{PackageId}\"", output);
    }

    public void Dispose() {
        try {
            Directory.Delete(_root, recursive: true);
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            // Best-effort: a temp folder left behind never fails a test whose assertions passed.
        }
    }

    private async Task<(int ExitCode, string Output)> EvaluateAsync(params string[] properties) {
        var startInfo = new ProcessStartInfo("dotnet") {
            WorkingDirectory = _consumerDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in new[] {
                "msbuild", "App.csproj", "-getItem:ProjectReference", "-getItem:PackageReference",
            }.Concat(properties)) {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)!;
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();

        using var timeout = new CancellationTokenSource(EvaluateTimeoutMilliseconds);
        await process.WaitForExitAsync(timeout.Token);
        return (process.ExitCode, await output + await error);
    }
}
