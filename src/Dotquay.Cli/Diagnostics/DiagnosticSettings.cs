using System.CommandLine;
using Dotquay.Cli.Commands;
using Dotquay.Core.Diagnostics;

namespace Dotquay.Cli.Diagnostics;

/// <summary>
/// Decides how verbose this run is, from <c>--verbose</c>/<c>--trace</c> and <c>DOTQUAY_VERBOSE</c>
/// (docs/DESIGN-v0.24-VERBOSE.md).
/// </summary>
/// <remarks>
/// <para>
/// The environment variable exists for callers that cannot add a flag: git invoking
/// <c>dotquay credential-helper</c>, the Godot editor plugin's fixed argument lists, an MCP client's
/// configured command line, a CI job that wants every dotquay call in a script logged. Set it once
/// and every nested invocation inherits it.
/// </para>
/// <para>
/// Flags and the variable combine by taking the more verbose of the two - never the other way
/// round. Someone who exported <c>DOTQUAY_VERBOSE=trace</c> to chase a bug and then types
/// <c>--verbose</c> out of habit is asking for at least that much, not for less.
/// </para>
/// </remarks>
internal static class DiagnosticSettings
{
    public const string EnvironmentVariable = "DOTQUAY_VERBOSE";

    public static DiagnosticLevel Resolve(ParseResult parseResult, string? environmentValue)
    {
        bool verbose;
        bool trace;

        try
        {
            verbose = parseResult.GetValue(GlobalOptions.Verbose);
            trace = parseResult.GetValue(GlobalOptions.Trace);
        }
        catch (InvalidOperationException)
        {
            // A command line that failed to parse still runs (to print its error); it just doesn't
            // get flag-driven diagnostics. The variable still applies.
            verbose = false;
            trace = false;
        }

        return Resolve(verbose, trace, environmentValue);
    }

    public static DiagnosticLevel Resolve(bool verbose, bool trace, string? environmentValue)
    {
        var fromFlags = trace ? DiagnosticLevel.Trace
            : verbose ? DiagnosticLevel.Verbose
            : DiagnosticLevel.Off;

        var fromEnvironment = ParseEnvironment(environmentValue);
        return fromFlags > fromEnvironment ? fromFlags : fromEnvironment;
    }

    /// <summary>
    /// <c>0</c>/<c>false</c>/<c>off</c>/empty is off; <c>2</c>/<c>trace</c> is trace; anything else
    /// that is set is verbose.
    /// </summary>
    /// <remarks>"Anything else" rather than an error: a person who set the variable to <c>yes</c>
    /// wants output, and refusing to start a package manager over the spelling of a debug switch
    /// helps nobody.</remarks>
    internal static DiagnosticLevel ParseEnvironment(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return DiagnosticLevel.Off;
        }

        return value.Trim().ToLowerInvariant() switch
        {
            "0" or "false" or "off" or "no" or "none" => DiagnosticLevel.Off,
            "2" or "trace" or "all" => DiagnosticLevel.Trace,
            _ => DiagnosticLevel.Verbose,
        };
    }
}
