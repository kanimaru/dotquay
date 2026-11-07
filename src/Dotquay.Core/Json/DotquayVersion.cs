using System.Reflection;

namespace Dotquay.Core.Json;

/// <summary>The CLI's own version string, reported in every <c>--json</c> envelope so a consumer can
/// tell an old dotquay from a new one before blaming the payload.</summary>
public static class DotquayVersion {
    // The entry assembly, not this one: the number that matters to a consumer is the executable's
    // (Dotquay.Cli carries the shipped version), and Dotquay.Core is a library that happens to host
    // this helper. Falls back to this assembly when there is no entry assembly at all — which is the
    // case inside a test host.
    private static readonly string Value = Read(Assembly.GetEntryAssembly()) ?? Read(typeof(DotquayVersion).Assembly) ?? "0.0.0";

    private static string? Read(Assembly? assembly) => assembly is null
        ? null
        : assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion is { Length: > 0 } informational
            // The SDK appends "+<commit sha>" to InformationalVersion; the sha is noise in a UI.
            ? informational.Split('+')[0]
            : assembly.GetName().Version?.ToString();

    public static string Current => Value;
}
