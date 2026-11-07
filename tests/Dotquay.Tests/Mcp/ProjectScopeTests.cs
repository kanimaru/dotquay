using Dotquay.Mcp.Scope;
using Xunit;

namespace Dotquay.Tests.Mcp;

/// <summary>
/// Pure <see cref="ProjectScope"/> behaviour — no server, no client, no fakes needed: this is plain
/// path bookkeeping over the fixed set of roots the server was started with.
/// </summary>
public sealed class ProjectScopeTests
{
    [Fact]
    public void Resolve_accepts_a_root_and_anything_under_it()
    {
        var root = Directory.CreateTempSubdirectory("dotquay-scope-root-").FullName;

        try
        {
            var scope = new ProjectScope([root]);
            var nested = Path.Combine(root, "sub", "project");

            Assert.Equal(Path.GetFullPath(root), scope.Resolve(root));
            Assert.Equal(Path.GetFullPath(nested), scope.Resolve(nested));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Resolve_defaults_a_missing_project_to_the_first_root()
    {
        var first = Directory.CreateTempSubdirectory("dotquay-scope-first-").FullName;
        var second = Directory.CreateTempSubdirectory("dotquay-scope-second-").FullName;

        try
        {
            var scope = new ProjectScope([first, second]);

            Assert.Equal(Path.GetFullPath(first), scope.Resolve(null));
            Assert.Equal(Path.GetFullPath(first), scope.Resolve("   "));
        }
        finally
        {
            Directory.Delete(first, recursive: true);
            Directory.Delete(second, recursive: true);
        }
    }

    [Fact]
    public void Resolve_refuses_a_directory_outside_every_root_and_names_the_allowed_ones()
    {
        var root = Directory.CreateTempSubdirectory("dotquay-scope-root-").FullName;
        var outside = Directory.CreateTempSubdirectory("dotquay-scope-outside-").FullName;

        try
        {
            var scope = new ProjectScope([root]);

            var ex = Assert.Throws<ScopeException>(() => scope.Resolve(outside));

            Assert.Contains(Path.GetFullPath(root), ex.Message);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            Directory.Delete(outside, recursive: true);
        }
    }

    [Fact]
    public void Resolve_refuses_a_sibling_that_only_shares_the_root_as_a_name_prefix()
    {
        var root = Directory.CreateTempSubdirectory("dotquay-scope-prefix-").FullName;
        var sibling = root + "-sibling";

        try
        {
            var scope = new ProjectScope([root]);

            Assert.Throws<ScopeException>(() => scope.Resolve(sibling));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void No_roots_means_the_working_directory_is_the_only_root()
    {
        var scope = new ProjectScope([]);

        Assert.Equal([Path.GetFullPath(Directory.GetCurrentDirectory())], scope.Roots);
    }
}
