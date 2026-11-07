using Dotquay.Core.Abstractions;
using Dotquay.Core.Lockfile;
using Dotquay.Core.Manifest;
using Dotquay.Core.Resolve;

namespace Dotquay.Core.Sync;

/// <summary>
/// The one place that answers "where does this addon live on disk?".
/// </summary>
/// <remarks>
/// Before <c>install_as</c> (SPEC.md §7.1) the answer was the literal expression
/// <c>addons/&lt;name&gt;</c>, repeated at nine call sites across install, status, doctor, patch
/// capture, remove, and orphan pruning. That was survivable only while the answer was a constant;
/// with a configurable folder, nine independent copies is nine chances for the installer and the
/// dirty check to disagree about which directory they're talking about — and a disagreement there
/// means either refusing to install a clean addon or overwriting a modified one.
/// </remarks>
public static class InstallPaths {
    /// <summary>The directory every addon is installed beneath. Godot's own convention, not
    /// configurable — the editor only scans <c>res://addons/</c>.</summary>
    public const string AddonsDirectory = "addons";

    public static string AddonsRoot(IFileSystem fileSystem, string projectDirectory) =>
        fileSystem.CombinePath(projectDirectory, AddonsDirectory);

    public static string For(IFileSystem fileSystem, string projectDirectory, LockedAddon locked) =>
        ForFolder(fileSystem, projectDirectory, locked.EffectiveFolder);

    public static string For(IFileSystem fileSystem, string projectDirectory, AddonSpec spec) =>
        ForFolder(fileSystem, projectDirectory, spec.EffectiveFolder);

    public static string For(IFileSystem fileSystem, string projectDirectory, ResolvedAddon resolved) =>
        ForFolder(fileSystem, projectDirectory, resolved.EffectiveFolder);

    public static string ForFolder(IFileSystem fileSystem, string projectDirectory, string folder) =>
        fileSystem.CombinePath(projectDirectory, AddonsDirectory, folder);
}
