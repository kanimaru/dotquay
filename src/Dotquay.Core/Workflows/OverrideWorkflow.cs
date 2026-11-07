using Dotquay.Core.Abstractions;
using Dotquay.Core.Manifest;

namespace Dotquay.Core.Workflows;

/// <summary>What an <see cref="OverrideWorkflow"/> call actually did to the manifest.</summary>
public enum OverrideChange {
    Added,
    Replaced,
    Removed,

    /// <summary>The requested override already said exactly this — nothing was written. Worth
    /// distinguishing so a caller doesn't run a resync (and a reinstall) for a no-op.</summary>
    Unchanged,
}

/// <summary>The manifest edit an override request produced.</summary>
public sealed record OverrideResult(string Name, OverrideChange Change, AddonSpec? Spec, string ManifestPath);

/// <summary>
/// Writes and clears <c>[overrides.&lt;name&gt;]</c> entries — the project's unconditional escape
/// hatch out of an unresolvable dependency conflict (SPEC.md §7.1/§8), and the one manifest edit the
/// Godot editor plugin's conflict picker performs (SPEC.md §18).
/// </summary>
/// <remarks>
/// Deliberately separate from <see cref="DependencyWorkflow"/>, and deliberately does not install
/// anything: writing an override is a decision about resolution, and applying it is
/// <see cref="DependencyWorkflow.ResyncAsync"/>'s job. Keeping the two apart is what lets a caller
/// write an override, see the resync fail, and clear it again without an install having half-happened
/// in between. It also keeps this class free of git, NuGet, and the network entirely — it only ever
/// reads and writes one TOML file.
/// </remarks>
public sealed class OverrideWorkflow {
    private readonly ProjectFileStore _projectFiles;

    public OverrideWorkflow(IFileSystem fileSystem) {
        _projectFiles = new ProjectFileStore(fileSystem);
    }

    public IReadOnlyDictionary<string, AddonSpec> List(string projectDirectory) =>
        _projectFiles.ReadManifestOrThrow(projectDirectory).Manifest.Overrides;

    /// <summary>
    /// Forces <paramref name="name"/> to one concrete source.
    /// </summary>
    /// <param name="git">The repository to pin to. Optional: when omitted it's inherited from the
    /// project's own <c>[addons.&lt;name&gt;]</c> entry, or failing that from the lockfile — because
    /// the common case (settling a version conflict) never involves changing the repo, and asking a
    /// user to restate a URL they already committed is a good way to get a typo'd one.</param>
    /// <param name="path">Subtree override (SPEC.md §7.1, OQ-2), inherited the same way.</param>
    /// <param name="installAs">Folder to install into, inherited the same way. This is what settles a
    /// <see cref="Resolve.GraphConflictKind.DisagreeingInstallFolders"/> conflict.</param>
    public OverrideResult Set(
        string projectDirectory,
        string name,
        AddonSourceKind kind,
        string value,
        string? git = null,
        string? path = null,
        string? installAs = null) {
        if (string.IsNullOrWhiteSpace(name)) {
            throw new WorkflowException("An override needs an addon name.");
        }

        if (string.IsNullOrWhiteSpace(value)) {
            throw new WorkflowException($"'{name}': an override needs a version, branch, or rev to pin to.");
        }

        var (manifest, manifestPath) = _projectFiles.ReadManifestOrThrow(projectDirectory);

        var resolvedGit = git ?? InferGit(projectDirectory, manifest, name)
            ?? throw new WorkflowException(
                $"'{name}' isn't declared in {manifestPath} and isn't locked, so its git URL can't be inferred — "
                + "pass the repository explicitly.");

        var resolvedPath = path ?? InferPath(manifest, name);
        var resolvedInstallAs = installAs ?? InferInstallAs(manifest, name);

        var spec = new AddonSpec {
            Name = name,
            Git = resolvedGit,
            Version = kind == AddonSourceKind.Version ? value : null,
            Branch = kind == AddonSourceKind.Branch ? value : null,
            Rev = kind == AddonSourceKind.Rev ? value : null,
            Path = resolvedPath,
            InstallAs = resolvedInstallAs,
        };

        if (manifest.Overrides.TryGetValue(name, out var existing) && SameSource(existing, spec)) {
            return new OverrideResult(name, OverrideChange.Unchanged, existing, manifestPath);
        }

        _projectFiles.WriteManifest(manifestPath, manifest with {
            Overrides = new Dictionary<string, AddonSpec>(manifest.Overrides, StringComparer.Ordinal) { [name] = spec },
        });

        return new OverrideResult(name, existing is null ? OverrideChange.Added : OverrideChange.Replaced, spec, manifestPath);
    }

    /// <summary>Drops the override, handing the name back to normal unification (SPEC.md §8) — which
    /// may well resurface the conflict it was hiding. That's the point: an override is a decision the
    /// user can take back.</summary>
    public OverrideResult Clear(string projectDirectory, string name) {
        var (manifest, manifestPath) = _projectFiles.ReadManifestOrThrow(projectDirectory);

        if (!manifest.Overrides.ContainsKey(name)) {
            return new OverrideResult(name, OverrideChange.Unchanged, null, manifestPath);
        }

        _projectFiles.WriteManifest(manifestPath, manifest with {
            Overrides = manifest.Overrides
                .Where(kv => !string.Equals(kv.Key, name, StringComparison.Ordinal))
                .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal),
        });

        return new OverrideResult(name, OverrideChange.Removed, null, manifestPath);
    }

    private string? InferGit(string projectDirectory, ProjectManifest manifest, string name) {
        if (manifest.Addons.TryGetValue(name, out var declared)) {
            return declared.Git;
        }

        if (manifest.Overrides.TryGetValue(name, out var existing)) {
            return existing.Git;
        }

        // A transitive addon has no manifest entry of its own, so the lockfile is the only local
        // record of which repository the name means (SPEC.md §7.3).
        var (lockfile, _) = _projectFiles.ReadOrCreateLockfile(projectDirectory);
        return lockfile.Addons.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.Ordinal))?.Git;
    }

    /// <summary>Only ever inherits an <em>explicit</em> path. <see cref="AddonSpec.EffectivePath"/>'s
    /// default is recomputed from the name anyway, so writing it out would add a redundant line to the
    /// manifest that then has to be maintained by hand if the addon ever moves.</summary>
    private static string? InferPath(ProjectManifest manifest, string name) =>
        manifest.Addons.TryGetValue(name, out var declared) ? declared.Path
        : manifest.Overrides.TryGetValue(name, out var existing) ? existing.Path
        : null;

    /// <summary>Inherited only from what the project itself already states. A transitive addon's
    /// folder is exactly what a folder conflict is about, so silently adopting one requirer's answer
    /// would decide the thing the user is being asked to decide.</summary>
    private static string? InferInstallAs(ProjectManifest manifest, string name) =>
        manifest.Addons.TryGetValue(name, out var declared) ? declared.InstallAs
        : manifest.Overrides.TryGetValue(name, out var existing) ? existing.InstallAs
        : null;

    private static bool SameSource(AddonSpec a, AddonSpec b) =>
        string.Equals(a.Git, b.Git, StringComparison.Ordinal)
        && a.SourceKind == b.SourceKind
        && string.Equals(AddonRequirementConstraint(a), AddonRequirementConstraint(b), StringComparison.Ordinal)
        && string.Equals(a.Path, b.Path, StringComparison.Ordinal)
        && string.Equals(a.InstallAs, b.InstallAs, StringComparison.Ordinal);

    private static string AddonRequirementConstraint(AddonSpec spec) => Resolve.AddonRequirement.ConstraintText(spec);
}
