using System.Diagnostics;
using Dotquay.Core.IO;
using Dotquay.Core.Local;
using Xunit;

namespace Dotquay.Tests.Local;

/// <summary>
/// Runs the staging target in <c>dotquay.local.targets</c> through a real MSBuild, because every
/// other test of it (<see cref="LocalTargetsTests"/>) reads the generated XML and cannot see
/// whether MSBuild does what the XML says.
/// </summary>
/// <remarks>
/// <para>
/// What it reproduces: a Roslyn host holds an analyzer assembly open with no sharing, and the
/// linked project that produced it is then rebuilt. The host is simulated by a
/// <c>FileShare.None</c> handle, which is all a Roslyn host's lock is. Windows only for the lock
/// itself — Unix does not refuse to replace an open file, so there the test still covers the
/// stage-and-sweep path but proves nothing about the lock.
/// </para>
/// <para>
/// The "analyzer" is an ordinary class library rather than a real source generator, so the test
/// needs no NuGet package and no network. That is enough for the lock, the hash-named directory
/// and the sweep; it is <em>not</em> enough for the CS8034 trap (a renamed copy that Roslyn
/// refuses to load), which needs a generator that actually runs — that one stays covered by the
/// string assertion on the file name and by the by-hand run recorded in
/// docs/HANDOVER-link-analyzer-staging.md.
/// </para>
/// </remarks>
public sealed class LinkedAnalyzerStagingRealProcessTests : IDisposable
{
    private const string PackageId = "Gen.Pkg";
    private const int BuildTimeoutMilliseconds = 180_000;

    private readonly string _root;
    private readonly string _generatorDirectory;
    private readonly string _consumerDirectory;

    public LinkedAnalyzerStagingRealProcessTests()
    {
        _root = Directory.CreateTempSubdirectory("dotquay-analyzer-staging-").FullName;
        _generatorDirectory = Path.Combine(_root, "Gen");
        _consumerDirectory = Path.Combine(_root, "App");
        Directory.CreateDirectory(_generatorDirectory);
        Directory.CreateDirectory(_consumerDirectory);

        File.WriteAllText(Path.Combine(_generatorDirectory, "Gen.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """);
        WriteGeneratorSource(revision: 1);

        File.WriteAllText(Path.Combine(_consumerDirectory, "App.csproj"), $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="{PackageId}" Version="1.0.0" />
              </ItemGroup>
            </Project>
            """);
        File.WriteAllText(
            Path.Combine(_consumerDirectory, "Program.cs"), "System.Console.WriteLine(\"app\");");

        var links = new Dictionary<string, DotnetLink>
        {
            [PackageId] = new DotnetLink(
                PackageId, Path.Combine(_generatorDirectory, "Gen.csproj"), Analyzer: true),
        };
        LocalTargetsWriter.Sync(new SystemFileSystem(), _consumerDirectory, links);
    }

    [Fact]
    public async Task AHostHoldingTheStagedAnalyzer_DoesNotBlockTheGeneratorsRebuild()
    {
        var first = await BuildAsync(_consumerDirectory, "App.csproj");
        Assert.True(first.ExitCode == 0, first.Output);

        var staged = StagedAnalyzers();
        var single = Assert.Single(staged);
        Assert.Equal("Gen.dll", Path.GetFileName(single));

        // A Roslyn host loads the copy it was told about and keeps it open.
        using var host = new FileStream(single, FileMode.Open, FileAccess.Read, FileShare.None);

        WriteGeneratorSource(revision: 2);
        var rebuild = await BuildAsync(_generatorDirectory, "Gen.csproj", "-t:Rebuild");
        Assert.True(rebuild.ExitCode == 0, rebuild.Output);
        Assert.DoesNotContain("MSB3", rebuild.Output);

        var second = await BuildAsync(_consumerDirectory, "App.csproj");
        Assert.True(second.ExitCode == 0, second.Output);
        Assert.Contains("0 Warning(s)", second.Output);

        // The rebuilt generator landed in a directory of its own. Where the OS enforces the lock
        // the held one survives the sweep; where it does not (Unix), the sweep takes it too.
        var after = StagedAnalyzers();
        if (OperatingSystem.IsWindows())
        {
            Assert.Contains(single, after);
            Assert.Equal(2, after.Count);
        }
        else
        {
            Assert.DoesNotContain(single, after);
            Assert.Single(after);
        }
    }

    [Fact]
    public async Task AStaleStagedDirectory_IsSweptOnceNothingHoldsIt()
    {
        Assert.Equal(0, (await BuildAsync(_consumerDirectory, "App.csproj")).ExitCode);
        var original = Assert.Single(StagedAnalyzers());

        WriteGeneratorSource(revision: 2);
        Assert.Equal(0, (await BuildAsync(_generatorDirectory, "Gen.csproj")).ExitCode);
        var build = await BuildAsync(_consumerDirectory, "App.csproj");
        Assert.True(build.ExitCode == 0, build.Output);

        var remaining = Assert.Single(StagedAnalyzers());
        Assert.NotEqual(original, remaining);
    }

    [Fact]
    public async Task WithStagingDisabled_TheSameLockBreaksTheRebuild()
    {
        // The control: without it the tests above could pass for a reason that has nothing to do
        // with staging, e.g. a host lock that never actually bites.
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var first = await BuildAsync(
            _consumerDirectory, "App.csproj", "-p:DisableDotquayAnalyzerStaging=true");
        Assert.True(first.ExitCode == 0, first.Output);

        var original = Directory
            .EnumerateFiles(Path.Combine(_generatorDirectory, "bin"), "Gen.dll", SearchOption.AllDirectories)
            .Single();
        using var host = new FileStream(original, FileMode.Open, FileAccess.Read, FileShare.None);

        WriteGeneratorSource(revision: 2);
        var rebuild = await BuildAsync(
            _generatorDirectory,
            "Gen.csproj",
            "-t:Rebuild",
            "-p:CopyRetryCount=1",
            "-p:CopyRetryDelayMilliseconds=1");

        Assert.NotEqual(0, rebuild.ExitCode);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Best effort: a build node still winding down must not fail a test that has passed.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private List<string> StagedAnalyzers()
    {
        var objDirectory = Path.Combine(_consumerDirectory, "obj");
        return Directory
            .EnumerateFiles(objDirectory, "Gen.dll", SearchOption.AllDirectories)
            .Where(p => p.Contains("linked-analyzers", StringComparison.Ordinal))
            .ToList();
    }

    private void WriteGeneratorSource(int revision)
    {
        File.WriteAllText(
            Path.Combine(_generatorDirectory, "Gen.cs"),
            $"public static class Gen{revision} {{ public const int Revision = {revision}; }}");
    }

    private static async Task<BuildResult> BuildAsync(
        string workingDirectory, string project, params string[] extraArguments)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("build");
        startInfo.ArgumentList.Add(project);
        startInfo.ArgumentList.Add("-nologo");
        startInfo.ArgumentList.Add("-v:m");
        startInfo.ArgumentList.Add("-nodeReuse:false");
        foreach (var argument in extraArguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        startInfo.Environment["MSBUILDDISABLENODEREUSE"] = "1";

        using var process = Process.Start(startInfo)!;
        process.StandardInput.Close();

        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();

        using var timeout = new CancellationTokenSource(BuildTimeoutMilliseconds);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            await Task.WhenAll(stdout, stderr);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }

        return new BuildResult(process.ExitCode, await stdout + await stderr);
    }

    private sealed record BuildResult(int ExitCode, string Output);
}
