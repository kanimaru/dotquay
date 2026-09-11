using System.Diagnostics;

namespace Dotquay.Core.SelfUpdate;

/// <summary>Real <see cref="IExecutableProbe"/>: starts the binary with <c>--version</c> and reads
/// the first line it prints.</summary>
/// <remarks>
/// Every failure is null rather than an exception. The caller's question is "may I trust this file
/// enough to make it the dotquay on this machine", and the answer to a binary that will not start,
/// hangs, or is not executable is the same no in each case. A timeout is part of that: a hung
/// candidate must not hang the update that is trying to reject it.
/// </remarks>
public sealed class ProcessExecutableProbe : IExecutableProbe
{
    private readonly TimeSpan _timeout;

    public ProcessExecutableProbe(TimeSpan? timeout = null)
    {
        _timeout = timeout ?? TimeSpan.FromSeconds(20);
    }

    public async Task<string?> ReadVersionAsync(string path, CancellationToken cancellationToken = default)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = path,
                ArgumentList = { "--version" },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });

            if (process is null)
            {
                return null;
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_timeout);

            var output = await process.StandardOutput.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);

            if (process.ExitCode != 0)
            {
                return null;
            }

            var first = output
                .Split('\n')
                .Select(line => line.Trim())
                .FirstOrDefault(line => line.Length > 0);

            return string.IsNullOrEmpty(first) ? null : first;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            // Not executable, wrong architecture, or gone between the check and the start. All of
            // them mean the same thing to the caller.
            return null;
        }
    }
}
