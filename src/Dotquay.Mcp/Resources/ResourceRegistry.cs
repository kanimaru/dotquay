using Dotquay.Core.Abstractions;
using Dotquay.Core.Local;
using Dotquay.Core.Workflows;
using Dotquay.Mcp.Scope;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Dotquay.Mcp.Resources;

/// <summary>
/// Exposes each configured project's manifest, lockfile, local-link overlay, and captured patches as
/// MCP resources (design doc §7): read-only views an agent can fetch by URI without a tool call.
/// </summary>
/// <remarks>
/// Concrete resources, enumerated eagerly from the server's scope roots, rather than
/// <c>resources/templates/list</c>'s parameterized templates: the roots are exactly the set this
/// server is scoped to (see <see cref="IProjectScope"/>), so there is nothing a template would add —
/// every resource this server could ever serve is already knowable at list time. Template support can
/// be added later without breaking anything here; nothing about this shape forecloses it.
/// </remarks>
/// <remarks>
/// Reads <see cref="IProjectScope.Roots"/> live on every <see cref="ListAsync"/> call rather than
/// caching the roots the server started with: a client that supplies roots via the MCP <c>roots</c>
/// capability (design doc §4.3, folded in by <c>ClientRootsSync</c>) should see its own projects'
/// files show up in <c>resources/list</c> too, not just be able to name them in a tool call's
/// <c>project</c> argument.
/// </remarks>
public sealed class ResourceRegistry(IFileSystem fileSystem, IProjectScope scope)
{
    public ValueTask<ListResourcesResult> ListAsync(RequestContext<ListResourcesRequestParams> context, CancellationToken cancellationToken)
    {
        var resources = new List<Resource>();
        var store = new ProjectFileStore(fileSystem);
        var localLinks = new LocalLinks(fileSystem);

        foreach (var root in scope.Roots)
        {
            var label = Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

            if (fileSystem.FileExists(store.ManifestPath(root)))
            {
                resources.Add(new Resource
                {
                    Uri = DotquayResourceUri.Manifest(root),
                    Name = $"{label}/{ProjectFileStore.ManifestFileName}",
                    Description = $"Manifest for the dotquay project at {root}.",
                    MimeType = "application/toml",
                });
            }

            if (fileSystem.FileExists(store.LockfilePath(root)))
            {
                resources.Add(new Resource
                {
                    Uri = DotquayResourceUri.Lockfile(root),
                    Name = $"{label}/{ProjectFileStore.LockfileFileName}",
                    Description = $"Lockfile for the dotquay project at {root}.",
                    MimeType = "application/toml",
                });
            }

            if (fileSystem.FileExists(localLinks.PathFor(root)))
            {
                resources.Add(new Resource
                {
                    Uri = DotquayResourceUri.LocalLinks(root),
                    Name = $"{label}/{LocalLinks.FileName}",
                    Description = $"Local (per-machine, gitignored) addon links for the project at {root}.",
                    MimeType = "application/toml",
                });
            }

            resources.AddRange(ListPatches(root, label));
        }

        return ValueTask.FromResult(new ListResourcesResult { Resources = resources });
    }

    private IEnumerable<Resource> ListPatches(string root, string label)
    {
        var layout = ProjectLayout.For(fileSystem, root, manifest: null);
        var patchesDirectory = layout.PatchesPath(fileSystem);

        if (!fileSystem.DirectoryExists(patchesDirectory))
        {
            yield break;
        }

        foreach (var patchFile in fileSystem.EnumerateFiles(patchesDirectory))
        {
            var addonName = Path.GetFileNameWithoutExtension(patchFile);

            yield return new Resource
            {
                Uri = DotquayResourceUri.Patch(root, addonName),
                Name = $"{label}/patches/{addonName}",
                Description = $"Captured patch for '{addonName}' in the project at {root}.",
                MimeType = "text/x-diff",
            };
        }
    }

    public ValueTask<ReadResourceResult> ReadAsync(RequestContext<ReadResourceRequestParams> context, CancellationToken cancellationToken)
    {
        var uri = context.Params?.Uri;

        if (uri is null || !DotquayResourceUri.TryParse(uri, out var rawProjectDirectory, out var segments))
        {
            throw new McpException($"'{uri}' is not a dotquay resource URI.");
        }

        // Re-validated even though every URI this server ever handed out already named an in-scope
        // project: a client is free to construct its own URI, and a resource read must fail the same
        // way an out-of-scope tool call does, not read whatever the fabricated path happens to name.
        string projectDirectory;

        try
        {
            projectDirectory = scope.Resolve(rawProjectDirectory);
        }
        catch (ScopeException ex)
        {
            throw new McpException(Dotquay.Core.Diagnostics.SecretRedactor.Redact(ex.Message), ex);
        }

        var store = new ProjectFileStore(fileSystem);

        var (path, mimeType) = segments[0] switch
        {
            "manifest" => (store.ManifestPath(projectDirectory), "application/toml"),
            "lockfile" => (store.LockfilePath(projectDirectory), "application/toml"),
            "local-links" => (new LocalLinks(fileSystem).PathFor(projectDirectory), "application/toml"),
            "patches" when segments.Count == 2 =>
                (ProjectLayout.For(fileSystem, projectDirectory, manifest: null).PatchFilePath(fileSystem, segments[1]), "text/x-diff"),
            _ => (null, null),
        };

        if (path is null || !fileSystem.FileExists(path))
        {
            throw new McpException($"'{uri}' does not name a file this server can read.");
        }

        return ValueTask.FromResult(new ReadResourceResult
        {
            Contents =
            [
                new TextResourceContents
                {
                    Uri = uri,
                    MimeType = mimeType,
                    Text = fileSystem.ReadAllText(path),
                },
            ],
        });
    }
}
