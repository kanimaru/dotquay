namespace Dotquay.Core.Init;

/// <summary>Outcome of <see cref="InitService"/>. A struct rather than a bare bool + string so
/// callers (the CLI command and its tests) can't accidentally swap the two.</summary>
public readonly record struct InitResult(bool WasCreated, string ManifestPath) {
    public static InitResult Created(string manifestPath) => new(true, manifestPath);

    public static InitResult AlreadyExists(string manifestPath) => new(false, manifestPath);
}
