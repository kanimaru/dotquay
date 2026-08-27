using Dotquay.Core.Git;
using Dotquay.Core.IO;
using Dotquay.Core.Workflows;

namespace Dotquay.Cli.Commands;

/// <summary>Builds a <see cref="DependencyWorkflow"/> wired to the real filesystem, real git, and
/// the shared <c>~/.dotquay/cache</c> (SPEC.md §9 step 1, decided OQ-5) — the one place CLI commands
/// construct real (non-test-double) infrastructure, so every command wires it the same way.</summary>
internal static class WorkflowFactory
{
    public static DependencyWorkflow Create()
    {
        var cacheRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotquay", "cache");
        return new DependencyWorkflow(new SystemFileSystem(), new ProcessGitClient(), cacheRoot);
    }
}
