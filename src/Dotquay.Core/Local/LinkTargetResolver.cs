using Dotquay.Core.Abstractions;
using Dotquay.Core.Lockfile;
using Dotquay.Core.Manifest;
using Dotquay.Core.Workflows;

namespace Dotquay.Core.Local;

/// <summary>
/// Decides what kind of thing <c>dotquay link &lt;name&gt;</c> is being asked to link — a
/// <c>[dotnet.*]</c> package or, failing that, an addon — from what the project actually has.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why the lockfile is consulted, not just the manifest.</strong> A package can reach a
/// project two ways: declared by hand in <c>dotquay.toml</c>, or <em>adopted</em> from an addon's
/// own package manifest (an addon that ships a C# core says <c>[dotnet.Foo.Core]</c>, and
/// <c>update</c> writes the <c>PackageReference</c> into the consumer's <c>.csproj</c> and the
/// entry into the consumer's lockfile — but never into the consumer's <c>dotquay.toml</c>, which
/// stays the user's own words). The consumer builds against that package exactly like a declared
/// one, so developing it in place is the same need — and the whole point of v0.11 was the
/// <em>author</em> of such a library editing it while their own app consumes it. Looking only at
/// the manifest made that case fall through to an addon link named after a NuGet ID, which then
/// tried to junction <c>addons/Foo.Core</c>.
/// </para>
/// <para>
/// The lockfile is the right second source because it is the record of what the project has,
/// whatever route it arrived by (<c>install</c> reads nothing else). The manifest still wins when
/// both name the package, so the returned spelling is the one everything downstream keys on.
/// </para>
/// <para>
/// Matched case-insensitively because NuGet IDs are; the value returned is the project's own
/// spelling, so a differently-cased request cannot create a second link entry.
/// </para>
/// </remarks>
public static class LinkTargetResolver {
    /// <summary>
    /// The project's spelling of <paramref name="name"/> if it is a <c>[dotnet.*]</c> package this
    /// project declares or has adopted; null if it isn't, or if neither file can be read — in which
    /// case an addon link is the right default, as it was before v0.11.
    /// </summary>
    public static string? ResolveDotnetPackageId(IFileSystem fileSystem, string projectDirectory,
        string name) {
        var store = new ProjectFileStore(fileSystem);

        return FromManifest(fileSystem, store, projectDirectory, name)
            ?? FromLockfile(fileSystem, store, projectDirectory, name);
    }

    private static string? FromManifest(
        IFileSystem fileSystem, ProjectFileStore store, string projectDirectory, string name) {
        var path = store.ManifestPath(projectDirectory);
        if (!fileSystem.FileExists(path)) {
            return null;
        }

        try {
            return ManifestReader.ReadProject(fileSystem.ReadAllText(path), path)
                .Dotnet.Keys
                .FirstOrDefault(id => string.Equals(id, name, StringComparison.OrdinalIgnoreCase));
        } catch (ManifestParseException) {
            // A broken manifest is a problem for the commands that need it. Refusing to classify
            // over it would be `link` inventing a prerequisite it doesn't actually have.
            return null;
        }
    }

    private static string? FromLockfile(
        IFileSystem fileSystem, ProjectFileStore store, string projectDirectory, string name) {
        var path = store.LockfilePath(projectDirectory);
        if (!fileSystem.FileExists(path)) {
            return null;
        }

        try {
            return LockfileReader.Read(fileSystem.ReadAllText(path), path)
                .Dotnet
                .Select(d => d.Name)
                .FirstOrDefault(id => string.Equals(id, name, StringComparison.OrdinalIgnoreCase));
        } catch (LockfileParseException) {
            return null;
        }
    }
}
