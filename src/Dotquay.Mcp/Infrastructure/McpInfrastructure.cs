using Dotquay.Core.Abstractions;
using Dotquay.Core.Auth;
using Dotquay.Core.Dotnet;
using Dotquay.Core.Git;
using Dotquay.Core.Graph;
using Dotquay.Core.IO;
using Dotquay.Core.Progress;
using Dotquay.Core.Workflows;

namespace Dotquay.Mcp.Infrastructure;

/// <summary>
/// Builds a <see cref="DependencyWorkflow"/>/<see cref="GraphInspector"/>/<see cref="OverrideWorkflow"/>/<see cref="SourceWorkflow"/>
/// wired to the real filesystem, real git, and the same shared <c>~/.dotquay/cache</c> every dotquay
/// command uses (SPEC.md §9 step 1, decided OQ-5).
/// </summary>
/// <remarks>
/// Mirrors <c>Dotquay.Cli.Commands.WorkflowFactory</c>/<c>InspectorFactory</c>/<c>CredentialFactory</c>
/// rather than reusing them: those are <c>internal</c> to <c>Dotquay.Cli</c>, and <c>Dotquay.Cli</c>
/// references <c>Dotquay.Mcp</c> (for the <c>dotquay mcp</c> command) rather than the other way
/// around — <c>Dotquay.Mcp</c> depending back on <c>Dotquay.Cli</c> would be circular, and would also
/// make the MCP server unusable by anything that isn't dotquay's own CLI. The duplication is a
/// handful of lines; the credential fallback below is in fact slightly more defensive than the CLI's
/// own <c>CredentialFactory</c>, matching <c>DependencyWorkflow</c>'s own default instead (a
/// long-running server is more likely than a one-shot CLI invocation to run somewhere with no
/// <c>$HOME</c>, e.g. certain container setups).
/// </remarks>
public static class McpInfrastructure
{
    public static string CacheRoot() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotquay", "cache");

    /// <summary>Every Phase 1 tool is read-only, so the workflow it builds never adopts an
    /// addon-declared NuGet feed (<see cref="DenyAllSourceApproval"/>) and never opts a source
    /// generator into being shared (<see cref="ShareGeneratorDecision"/>, the pre-v0.14 default) —
    /// there is no human on the other end of this process to ask, and no Phase 1 tool resolves a
    /// package for the first time regardless. Phase 2's write tools that DO need to adopt a feed or
    /// answer the generator question call <see cref="CreateWorkflow(IProgressReporter?, ISourceApproval?, IGeneratorAssetDecision?)"/>
    /// instead, after the write guard (<see cref="Dotquay.Mcp.Guards.IWriteGuard"/>) has already
    /// decided the call may proceed.</summary>
    public static DependencyWorkflow CreateWorkflow(IProgressReporter? progress = null) =>
        CreateWorkflow(progress, sourceApproval: null, generatorAssetDecision: null);

    /// <summary>
    /// The write-tool overload: takes the <see cref="ISourceApproval"/>/<see cref="IGeneratorAssetDecision"/>
    /// a guard policy resolved from the call's arguments (<see cref="Dotquay.Mcp.Guards.SourceApprovalPolicy.Resolve"/>,
    /// <see cref="Dotquay.Mcp.Guards.GeneratorPrivacyPolicy.Resolve"/>), mirroring
    /// <c>Dotquay.Cli.Commands.WorkflowFactory.CreateWithProgress</c>'s own parameters for the same
    /// two seams. Null falls back to the Phase 1 defaults above, so a write tool that never adopts
    /// feeds or resolves generators (<c>dotquay_install</c>, <c>dotquay_remove</c>, …) can call this
    /// exactly like <see cref="CreateWorkflow(IProgressReporter?)"/> without a caller having to know
    /// the difference.
    /// </summary>
    public static DependencyWorkflow CreateWorkflow(
        IProgressReporter? progress, ISourceApproval? sourceApproval, IGeneratorAssetDecision? generatorAssetDecision)
    {
        var workflow = new DependencyWorkflow(new SystemFileSystem(), new ProcessGitClient(), CacheRoot())
        {
            SourceApproval = sourceApproval ?? DenyAllSourceApproval.Instance,
            GeneratorAssetDecision = generatorAssetDecision ?? ShareGeneratorDecision.Instance,
        };

        return progress is null ? workflow : workflow.WithProgress(progress);
    }

    public static GraphInspector CreateInspector() =>
        new(new SystemFileSystem(), new ProcessGitClient(), CacheRoot());

    /// <summary>Pure manifest edits — no git, no NuGet, no cache. See
    /// <c>Dotquay.Cli.Commands.WorkflowFactory.CreateOverrides</c>'s remarks for why that separation
    /// is deliberate; it applies here unchanged.</summary>
    public static OverrideWorkflow CreateOverrides() => new(new SystemFileSystem());

    public static SourceWorkflow CreateSources() =>
        new(new SystemFileSystem(), new NuGetV3ClientFactory(CreateCredentialResolver()));

    public static CredentialResolver CreateCredentialResolver()
    {
        var fileSystem = new SystemFileSystem();
        var environment = new SystemEnvironmentReader();

        ICredentialStore store = environment.HomeDirectory is null
            ? new EmptyCredentialStore()
            : new TomlCredentialStore(fileSystem, TomlCredentialStore.DefaultPath(environment, fileSystem));

        return new CredentialResolver(environment, store);
    }
}
