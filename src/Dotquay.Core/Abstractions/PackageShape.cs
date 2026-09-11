namespace Dotquay.Core.Abstractions;

/// <summary>
/// What a package says about itself, read from its <c>.nuspec</c> — currently just whether its author
/// declared it a development dependency.
/// </summary>
/// <remarks>
/// <para>A record rather than a bare bool so the next fact read from the same document (a license, a
/// readme, a framework list) does not change every signature between here and the workflow.</para>
/// <para>Measured 2026-09-10 on SDK 10.0.101, and worth stating because the obvious assumption is
/// wrong: <c>&lt;developmentDependency&gt;true&lt;/developmentDependency&gt;</c> reaches the nuspec via
/// <c>dotnet pack</c> and then has <em>no effect whatsoever</em> on a <c>PackageReference</c> consumer.
/// Two packages differing only in that flag produce identical <c>project.assets.json</c> entries and
/// identical dependency lines in the consuming package's own nuspec. That inertness is precisely what
/// makes it a good signal to read: it is an authored statement of intent with no behaviour of its own
/// to second-guess, and it costs one extra request against a resource dotquay already discovers.</para>
/// </remarks>
/// <param name="IsDevelopmentDependency">The author declared this a build-time-only package — an
/// analyzer, a source generator, a build-props package. Null when the nuspec could not be read at all,
/// which is not an error: it means "no signal", and dotquay stays quiet rather than guessing.</param>
public sealed record PackageShape(bool? IsDevelopmentDependency)
{
    /// <summary>Nothing learned — an unreadable or absent nuspec. Distinct from
    /// <c>IsDevelopmentDependency: false</c>, which is the author positively not claiming it.</summary>
    public static readonly PackageShape Unknown = new((bool?)null);

    /// <summary>True only when the author actually said so. A missing signal is never read as a
    /// generator: dotquay asks a question the user has to answer, and asking it about an ordinary
    /// library would be noise on every single <c>add</c>.</summary>
    public bool IsGenerator => IsDevelopmentDependency == true;
}
