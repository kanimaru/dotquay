using Dotquay.Core.Abstractions;

namespace Dotquay.Tests.Support;

/// <summary>
/// <see cref="IEnvironmentReader"/> test double.
/// </summary>
/// <remarks>
/// Replaces the save-set-restore-the-real-process-variable pattern the credential tests used before
/// v0.7. That pattern was safe within one class (xunit runs a class's facts sequentially) and unsafe
/// across classes, which is precisely how it was being used — two classes mutating the same two
/// <c>DOTQUAY_NUGET_*</c> variables, run in parallel by default. This removes the shared state
/// instead of serialising around it.
/// </remarks>
public sealed class FakeEnvironmentReader : IEnvironmentReader
{
    private readonly Dictionary<string, string> _variables = new(StringComparer.Ordinal);

    public FakeEnvironmentReader(string? homeDirectory = "/home/kani")
    {
        HomeDirectory = homeDirectory;
    }

    public string? HomeDirectory { get; set; }

    public FakeEnvironmentReader Set(string name, string value)
    {
        _variables[name] = value;
        return this;
    }

    public string? GetVariable(string name) => _variables.GetValueOrDefault(name);
}
