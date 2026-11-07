using System.ComponentModel;
using System.Diagnostics;
using Dotquay.Core.Abstractions;
using Dotquay.Core.Diagnostics;

namespace Dotquay.Core.Dotnet;

/// <summary>Shells out to <c>dotnet</c> on PATH (mirrors <see cref="Git.ProcessGitClient"/>'s
/// process-running shape — same rationale: let the real tool do the real work).</summary>
public sealed class ProcessDotnetCliRunner : IDotnetCliRunner {
    public async Task RestoreAsync(
        string csprojPath, IReadOnlyDictionary<string, string>? environmentVariables = null, CancellationToken cancellationToken = default) {
        var startInfo = new ProcessStartInfo("dotnet") {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("restore");
        startInfo.ArgumentList.Add(csprojPath);

        if (environmentVariables is not null) {
            // ProcessStartInfo.Environment starts pre-populated with the inherited environment
            // (PATH, etc.) — this only adds/overrides the given keys, nothing else is touched.
            foreach (var (key, value) in environmentVariables) {
                startInfo.Environment[key] = value;
            }
        }

        using var process = new Process { StartInfo = startInfo };
        var trace = ProcessTrace.Start("dotnet", startInfo.ArgumentList, workingDirectory: null);

        try {
            process.Start();
        } catch (Win32Exception ex) {
            trace.Failed(ex);
            throw new DotnetSyncException("Could not start 'dotnet'. Is the .NET SDK installed and on PATH?", ex);
        }

        var stdOutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stdErrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        await process.WaitForExitAsync(cancellationToken);

        var stdOut = await stdOutTask;
        var stdErr = await stdErrTask;

        trace.Completed(process.ExitCode, stdOut, stdErr);

        if (process.ExitCode != 0) {
            throw new DotnetSyncException(
                $"'dotnet restore {csprojPath}' failed (exit {process.ExitCode}):{Environment.NewLine}{stdErr}{stdOut}");
        }
    }
}
