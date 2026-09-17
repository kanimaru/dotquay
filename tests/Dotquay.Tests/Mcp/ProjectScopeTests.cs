using Dotquay.Mcp.Scope;
using Xunit;

namespace Dotquay.Tests.Mcp;

/// <summary>
/// Pure <see cref="ProjectScope"/> behaviour — no server, no client, no fakes needed: this is plain
/// path bookkeeping, exactly the kind of logic <see cref="ClientRootsSync"/>'s own tests (which do
/// need a real client/server pair — see <see cref="RootsIntegrationTests"/>) should not have to
/// re-verify.
/// </summary>
public sealed class ProjectScopeTests
{
    [Fact]
    public void AddRoots_widens_what_Resolve_accepts()
    {
        var root = Directory.CreateTempSubdirectory("dotquay-scope-root-").FullName;
        var added = Directory.CreateTempSubdirectory("dotquay-scope-added-").FullName;

        try
        {
            var scope = new ProjectScope([root]);

            Assert.Throws<ScopeException>(() => scope.Resolve(added));

            scope.AddRoots([added]);

            Assert.Equal(Path.GetFullPath(added), scope.Resolve(added));
            Assert.Contains(Path.GetFullPath(added), scope.Roots);
            Assert.Contains(Path.GetFullPath(root), scope.Roots);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            Directory.Delete(added, recursive: true);
        }
    }

    [Fact]
    public void AddRoots_never_duplicates_a_root_already_present()
    {
        var root = Directory.CreateTempSubdirectory("dotquay-scope-dup-").FullName;

        try
        {
            var scope = new ProjectScope([root]);

            scope.AddRoots([root, root]);
            scope.AddRoots([root + Path.DirectorySeparatorChar]);

            Assert.Single(scope.Roots);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void AddRoots_ignores_blank_entries()
    {
        var root = Directory.CreateTempSubdirectory("dotquay-scope-blank-").FullName;

        try
        {
            var scope = new ProjectScope([root]);

            scope.AddRoots(["", "   ", null!]);

            Assert.Single(scope.Roots);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
