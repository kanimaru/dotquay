using System.CommandLine;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Dotquay.Cli.Commands;
using Dotquay.Core.Diagnostics;
using Dotquay.Core.Json;

namespace Dotquay.Cli.Diagnostics;

/// <summary>
/// The first and last lines of every verbose run: what was run, by which build, against which
/// project - and how it ended.
/// </summary>
/// <remarks>
/// The opening line answers the questions every bug report needs answered before the rest of the
/// log means anything, and that people reliably forget to include: which dotquay, JIT or NativeAOT
/// (the two have diverged before - see the NativeAOT notes in docs/SPRINT-01.md), which OS, and which
/// directory <c>--project</c> actually resolved to.
/// </remarks>
internal sealed class RunDiagnostics
{
    private readonly long _started = Stopwatch.GetTimestamp();

    private RunDiagnostics()
    {
    }

    public static RunDiagnostics Begin(IReadOnlyList<string> args, ParseResult parseResult)
    {
        var run = new RunDiagnostics();

        if (!DiagnosticLog.IsEnabled(DiagnosticLevel.Verbose))
        {
            return run;
        }

        DiagnosticLog.Write(
            DiagnosticLevel.Verbose,
            "dotquay",
            $"dotquay {DotquayVersion.Current} ({Runtime()}) on {RuntimeInformation.OSDescription} {RuntimeInformation.OSArchitecture}");
        DiagnosticLog.Write(
            DiagnosticLevel.Verbose,
            "dotquay",
            $"args: {string.Join(' ', SecretRedactor.RedactArguments(args))}");
        DiagnosticLog.Write(DiagnosticLevel.Verbose, "dotquay", $"project: {GlobalOptions.Resolve(parseResult)}");
        DiagnosticLog.Write(DiagnosticLevel.Verbose, "dotquay", $"cache: {CachePaths.Root()}");
        DiagnosticLog.Write(DiagnosticLevel.Verbose, "dotquay", $"diagnostics: {Describe(DiagnosticLog.Current.Level)}");

        return run;
    }

    public void End(int exitCode)
    {
        DiagnosticLog.Write(
            DiagnosticLevel.Verbose,
            "dotquay",
            $"exit {exitCode} after {DiagnosticLog.FormatDuration(Stopwatch.GetElapsedTime(_started))}");
    }

    // A switch, not Enum.ToString: enum names are reflection metadata, and this project has been
    // bitten by the NativeAOT trimmer removing exactly that kind of thing before.
    private static string Describe(DiagnosticLevel level) => level switch
    {
        DiagnosticLevel.Trace => "trace",
        DiagnosticLevel.Verbose => "verbose",
        _ => "off",
    };

    private static string Runtime() => RuntimeFeature.IsDynamicCodeSupported ? "JIT" : "NativeAOT";
}
