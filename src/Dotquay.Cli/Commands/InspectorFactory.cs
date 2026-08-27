using Dotquay.Core.Git;
using Dotquay.Core.Graph;
using Dotquay.Core.IO;

namespace Dotquay.Cli.Commands;

/// <summary>The read-only twin of <see cref="WorkflowFactory"/>: builds a <see cref="GraphInspector"/>
/// wired to the real filesystem, real git, and the same shared <c>~/.dotquay/cache</c> every other
/// command uses (SPEC.md §9 step 1, decided OQ-5) — so a graph walk reuses fetches the installer
/// already paid for.</summary>
internal static class InspectorFactory
{
    public static GraphInspector Create() =>
        new(new SystemFileSystem(), new ProcessGitClient(), CachePaths.Root());
}

/// <summary>The one definition of where the shared cache lives, so the workflow and the inspector can
/// never point at two different directories.</summary>
internal static class CachePaths
{
    public static string Root() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotquay", "cache");
}
