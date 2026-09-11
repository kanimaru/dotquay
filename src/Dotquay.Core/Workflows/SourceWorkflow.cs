using Dotquay.Core.Abstractions;
using Dotquay.Core.Dotnet;
using Dotquay.Core.Lockfile;
using Dotquay.Core.Manifest;

namespace Dotquay.Core.Workflows;

/// <summary>What a <see cref="SourceWorkflow"/> call actually did to the manifest.</summary>
public enum SourceChange
{
    Added,
    Removed,

    /// <summary>The manifest already said exactly this — nothing was written. Distinguished for the
    /// same reason <see cref="OverrideChange.Unchanged"/> is: a caller shouldn't report a change it
    /// didn't make.</summary>
    Unchanged,
}

/// <summary>The manifest edit a source request produced.</summary>
/// <param name="DroppedPins">Human-readable descriptions of every <c>source =</c> pin and lockfile
/// record a forced removal cleared. Empty on every other path. Reported rather than silently applied,
/// because unpinning a package changes which feed it resolves from next time.</param>
public sealed record SourceResult(
    string Name,
    SourceChange Change,
    NuGetSourceSpec? Source,
    string ManifestPath,
    IReadOnlyList<string> DroppedPins)
{
    public SourceResult(string name, SourceChange change, NuGetSourceSpec? source, string manifestPath)
        : this(name, change, source, manifestPath, [])
    {
    }
}

/// <summary>
/// Declares, lists, and removes <c>[nuget.sources.&lt;name&gt;]</c> feeds (SPEC.md §7.1, §11.1) —
/// the one manifest section that, through v0.13, could only be changed with a text editor.
/// </summary>
/// <remarks>
/// <para>Before this, the only writers of that section were <c>init --nuget-source</c>'s scaffold
/// (which writes exactly one feed, at project creation) and <see cref="SourceAdopter"/> (v0.8, which
/// writes feeds an <em>addon</em> declared, behind the trusted-host/approval gate). Neither is
/// user-initiated after init, so adding the second feed to a project meant hand-editing TOML — the
/// same gap <see cref="OverrideWorkflow"/> was created to close for <c>[overrides.*]</c>, and closed
/// the same way.</para>
/// <para>Deliberately separate from <see cref="DependencyWorkflow"/> and deliberately installs
/// nothing: declaring a feed is a statement about where packages come from, not a request to fetch
/// any. It touches git and the cache not at all, and the network only to verify the feed answers.</para>
/// </remarks>
public sealed class SourceWorkflow
{
    private readonly ProjectFileStore _projectFiles;
    private readonly INuGetClientFactory _nuGetClientFactory;

    public SourceWorkflow(IFileSystem fileSystem, INuGetClientFactory nuGetClientFactory)
    {
        _projectFiles = new ProjectFileStore(fileSystem);
        _nuGetClientFactory = nuGetClientFactory;
    }

    /// <summary>The project's declared feeds in declared order — never the implicitly appended
    /// nuget.org, which the project did not declare and cannot remove.</summary>
    public IReadOnlyList<NuGetSourceSpec> List(string projectDirectory) =>
        _projectFiles.ReadManifestOrThrow(projectDirectory).Manifest.NuGetSources;

    /// <summary>
    /// Declares a feed, appending it after every feed already declared.
    /// </summary>
    /// <remarks>
    /// <para><strong>Appends, never prepends, and offers no way to choose.</strong> Declaration order
    /// is the resolution rule — an unpinned package takes the first feed that has it (SPEC.md §11.1)
    /// — so position is a security property rather than a preference. A convenience command that put a
    /// new feed ahead of the existing ones would let one <c>source add</c> silently re-point every
    /// unpinned package in the project, which is dependency confusion arriving through the front door.
    /// Reordering stays a deliberate hand-edit, where it reads as the decision it is.</para>
    /// <para>Verification is not politeness either. §11.1 already makes a service index that fails to
    /// answer <em>fatal at resolve time</em>, so checking it here applies the same rule earlier and
    /// more cheaply — and it means a typo'd URL fails while the user is still looking at the URL they
    /// typed, rather than during an unrelated <c>add</c> later.</para>
    /// </remarks>
    /// <param name="verify">False to write without reaching the feed — for a machine that has no
    /// network yet, or a feed that is legitimately offline right now.</param>
    /// <exception cref="WorkflowException">The name or URL is unusable, or the name is already taken
    /// by a different URL, or the URL is already declared under a different name.</exception>
    public async Task<SourceResult> AddAsync(
        string projectDirectory,
        string name,
        string url,
        bool verify = true,
        CancellationToken cancellationToken = default)
    {
        ValidateName(name);
        ValidateUrl(name, url);

        var (manifest, manifestPath) = _projectFiles.ReadManifestOrThrow(projectDirectory);

        var existingByName = manifest.NuGetSources.FirstOrDefault(
            source => string.Equals(source.Name, name, StringComparison.Ordinal));

        if (existingByName is not null)
        {
            // Idempotent when it already says exactly this: re-running the command a script already
            // ran is not a mistake worth failing. A *different* URL under the same name is, because
            // silently repointing a name every `source =` pin refers to is the one edit here that can
            // change where an existing package comes from.
            if (string.Equals(existingByName.Url, url, StringComparison.OrdinalIgnoreCase))
            {
                return new SourceResult(name, SourceChange.Unchanged, existingByName, manifestPath);
            }

            throw new WorkflowException(
                $"[nuget.sources.{name}] already points at {existingByName.Url}. Remove it first with "
                + $"'dotquay source remove {name}' if you mean to repoint it — every source = \"{name}\" pin "
                + "in this project follows that name.");
        }

        var existingByUrl = manifest.NuGetSources.FirstOrDefault(
            source => string.Equals(source.Url, url, StringComparison.OrdinalIgnoreCase));

        if (existingByUrl is not null)
        {
            // Two names for one feed makes first-match ordering meaningless (both "positions" are the
            // same feed) and gives the project two credential lookups for one host. SourceAdopter
            // already treats a duplicate URL as the collision worth naming rather than merging.
            throw new WorkflowException(
                $"That URL is already declared as [nuget.sources.{existingByUrl.Name}]. One feed under two "
                + "names would make the search order meaningless — pin packages to "
                + $"'{existingByUrl.Name}' instead of declaring it twice.");
        }

        var source = new NuGetSourceSpec(name, url);

        if (verify)
        {
            // Before the write, not after: a feed that cannot be reached should leave the manifest
            // exactly as it was, so a failed `source add` is never something the user has to undo.
            await VerifyAsync(source, cancellationToken);
        }

        _projectFiles.WriteManifest(manifestPath, manifest with
        {
            NuGetSources = [.. manifest.NuGetSources, source],
        });

        return new SourceResult(name, SourceChange.Added, source, manifestPath);
    }

    /// <summary>
    /// Removes a declared feed.
    /// </summary>
    /// <remarks>
    /// Refuses while anything still names the feed, because removing it regardless would hand every
    /// pinned package back to first-match resolution across whatever feeds remain — which is the
    /// substitution SPEC.md §11.1's ordering rule exists to prevent, produced by a cleanup command.
    /// <paramref name="force"/> clears those references and reports each one, so the change is visible
    /// rather than inferred from a later surprise.
    /// </remarks>
    /// <param name="force">Also clear every <c>source =</c> pin and lockfile source record naming this
    /// feed. The lockfile records are cleared too, not just the manifest pins: leaving one behind
    /// points <c>&lt;packageSourceMapping&gt;</c> at a feed the project no longer declares.</param>
    public SourceResult Remove(string projectDirectory, string name, bool force = false)
    {
        var (manifest, manifestPath) = _projectFiles.ReadManifestOrThrow(projectDirectory);

        var existing = manifest.NuGetSources.FirstOrDefault(
            source => string.Equals(source.Name, name, StringComparison.Ordinal));

        if (existing is null)
        {
            return new SourceResult(name, SourceChange.Unchanged, null, manifestPath);
        }

        var pinnedInManifest = manifest.Dotnet.Values
            .Where(spec => string.Equals(spec.Source, name, StringComparison.Ordinal))
            .Select(spec => spec.Name)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();

        var (lockfile, lockfilePath) = _projectFiles.ReadOrCreateLockfile(projectDirectory);
        var pinnedInLockfile = lockfile.Dotnet
            .Where(locked => string.Equals(locked.Source, name, StringComparison.Ordinal))
            .Select(locked => locked.Name)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();

        if (!force && (pinnedInManifest.Count > 0 || pinnedInLockfile.Count > 0))
        {
            var referenced = pinnedInManifest.Concat(pinnedInLockfile).Distinct(StringComparer.Ordinal);

            throw new WorkflowException(
                $"[nuget.sources.{name}] is still used by {string.Join(", ", referenced)}. Removing it would "
                + "hand those packages back to a first-match search across your remaining feeds, which can "
                + $"resolve them somewhere else entirely. Pin them elsewhere first, or pass --force to clear "
                + "the pins and be told which ones went.");
        }

        var dropped = new List<string>();

        var dotnet = manifest.Dotnet;
        if (pinnedInManifest.Count > 0)
        {
            dotnet = manifest.Dotnet.ToDictionary(
                kv => kv.Key,
                kv => string.Equals(kv.Value.Source, name, StringComparison.Ordinal)
                    ? kv.Value.WithoutSource()
                    : kv.Value,
                StringComparer.Ordinal);

            dropped.AddRange(pinnedInManifest.Select(id => $"[dotnet.{id}] source = \"{name}\""));
        }

        _projectFiles.WriteManifest(manifestPath, manifest with
        {
            NuGetSources = manifest.NuGetSources
                .Where(source => !string.Equals(source.Name, name, StringComparison.Ordinal))
                .ToList(),
            Dotnet = dotnet,
        });

        if (pinnedInLockfile.Count > 0)
        {
            _projectFiles.WriteLockfile(lockfilePath, lockfile with
            {
                Dotnet = lockfile.Dotnet
                    .Select(locked => string.Equals(locked.Source, name, StringComparison.Ordinal)
                        ? locked.WithoutSource()
                        : locked)
                    .ToList(),
            });

            dropped.AddRange(pinnedInLockfile.Select(id => $"{id} (locked to '{name}')"));
        }

        return new SourceResult(name, SourceChange.Removed, existing, manifestPath, dropped);
    }

    /// <summary>
    /// Reaches the feed's service index, so a bad URL or a rejected credential is reported now.
    /// </summary>
    /// <remarks>
    /// Built over a single-source set rather than the project's whole set, so verification cannot
    /// accidentally pass because some <em>other</em> declared feed answered.
    /// </remarks>
    private async Task VerifyAsync(NuGetSourceSpec source, CancellationToken cancellationToken)
    {
        var client = _nuGetClientFactory.Create(NuGetSourceSet.From([source]), source.Name);
        await client.VerifySourceAsync(cancellationToken);
    }

    private static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new WorkflowException("A NuGet source needs a name, e.g. 'dotquay source add core <url>'.");
        }

        // Same rule, and the same reason, as the reader's: the name reaches a NuGet.Config XML
        // attribute and a NuGetPackageSourceCredentials_<key> environment variable, so it cannot be
        // arbitrary text. Rejected here rather than at the next command that has to use it.
        if (!NuGetSourceSpec.IsValidName(name))
        {
            throw new WorkflowException(
                $"'{name}' isn't a usable source name. Start with a letter or digit, then letters, digits, "
                + "'.', '_' or '-' — it becomes a NuGet.Config key and an environment variable name.");
        }
    }

    private static void ValidateUrl(string name, string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            throw new WorkflowException(
                $"'{name}' needs a V3 service-index URL, e.g. https://gitlab.example.com/api/v4/projects/1/packages/nuget/index.json.");
        }

        // http/https only, and absolute. A relative or file: URL parses as a Uri and then fails much
        // later inside the HTTP client, or worse, inside `dotnet restore` in a child process where
        // the message no longer mentions dotquay at all.
        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed)
            || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
        {
            throw new WorkflowException(
                $"'{url}' isn't an http(s) URL. A NuGet source is a V3 service index — for GitLab that is "
                + "/api/v4/projects/<numeric id>/packages/nuget/index.json.");
        }
    }
}
