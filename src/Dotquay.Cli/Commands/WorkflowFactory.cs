using Dotquay.Core.Abstractions;
using Dotquay.Core.Diagnostics;
using Dotquay.Core.Git;
using Dotquay.Core.IO;
using Dotquay.Core.Workflows;

namespace Dotquay.Cli.Commands;

/// <summary>Builds a <see cref="DependencyWorkflow"/> wired to the real filesystem, real git, and
/// the shared <c>~/.dotquay/cache</c> (SPEC.md §9 step 1, decided OQ-5) — the one place CLI commands
/// construct real (non-test-double) infrastructure, so every command wires it the same way.</summary>
internal static class WorkflowFactory
{
    public static DependencyWorkflow Create(
        ISourceApproval? sourceApproval = null,
        IGeneratorAssetDecision? generatorAssetDecision = null,
        ISubtreeChoice? subtreeChoice = null) =>
        new(new SystemFileSystem(), new ProcessGitClient(), CachePaths.Root())
        {
            SourceApproval = sourceApproval ?? DenyAllSourceApproval.Instance,
            GeneratorAssetDecision = generatorAssetDecision ?? ShareGeneratorDecision.Instance,
            SubtreeChoice = subtreeChoice ?? DeclineSubtreeChoice.Instance,
        };

    /// <summary>
    /// A workflow that draws progress, plus the handle that stops it.
    /// </summary>
    /// <remarks>
    /// The reporter must be disposed before the command prints anything of its own, or its last
    /// spinner frame is left sitting on the line the results are about to be written to. Returning
    /// the <see cref="IDisposable"/> alongside the workflow makes that the caller's obvious
    /// responsibility rather than something to remember.
    /// </remarks>
    /// <param name="allowSourceAdoption">Whether this command may adopt a NuGet feed an addon
    /// declared (SPEC.md §11.3). True only for <c>add</c> and <c>update</c>, which are already
    /// resolving and rewriting the manifest. <c>install</c> passes false on principle even though it
    /// never walks the graph — a command whose contract is "replay the lockfile" should not be one
    /// refactor away from acquiring a feed.</param>
    /// <param name="preApprovedSourceUrls">Feed URLs the user already agreed to, from a prior
    /// <c>add --preview</c>. These take precedence over the console prompt, which is what lets the
    /// editor plugin — a caller that cannot answer a prompt — complete an adoption it asked about in
    /// its own UI. Ignored entirely when <paramref name="allowSourceAdoption"/> is false, so
    /// <c>install</c> cannot be talked into a feed by a flag.</param>
    /// <param name="generatorAssetDecision">Who answers the source-generator question (v0.14). Null
    /// leaves it at <see cref="ShareGeneratorDecision"/>, the pre-v0.14 behaviour, which is what every
    /// command other than <c>add</c> wants: nothing else resolves a package for the first time, so
    /// nothing else has a decision to make.</param>
    /// <param name="subtreeChoice">Who confirms a guessed subtree (v0.20). Null leaves it at
    /// <see cref="DeclineSubtreeChoice"/>, which is what every command other than a real <c>add</c>
    /// wants: nothing else derives a subtree from a URL, so nothing else has a guess to confirm.</param>
    public static (DependencyWorkflow Workflow, IDisposable Progress) CreateWithProgress(
        bool json,
        bool allowSourceAdoption = false,
        IReadOnlyList<string>? preApprovedSourceUrls = null,
        IGeneratorAssetDecision? generatorAssetDecision = null,
        ISubtreeChoice? subtreeChoice = null)
    {
        var reporter = ConsoleProgressReporter.Create(json);
        var approval = allowSourceAdoption
            ? SourceApprovalFor(json, preApprovedSourceUrls)
            : DenyAllSourceApproval.Instance;

        return (
            // The diagnostic wrapper goes outside the spinner, so the log keeps every step even
            // under --json, where the spinner itself is the null reporter.
            Create(approval, generatorAssetDecision, subtreeChoice).WithProgress(DiagnosticProgressReporter.Wrap(reporter)),
            reporter as IDisposable ?? NullDisposable.Instance);
    }

    /// <summary>
    /// Pre-approved URLs win over the console prompt when there are any.
    /// </summary>
    /// <remarks>
    /// Not layered — "the whitelist, then fall back to prompting" would re-ask about every feed the
    /// user's preview showed but they declined, turning a considered "no" into a prompt loop. Naming
    /// any URL is a statement that this run's approvals are already decided; feeds outside the list
    /// are refused, and the resulting error explains exactly that.
    /// </remarks>
    private static ISourceApproval SourceApprovalFor(bool json, IReadOnlyList<string>? preApprovedSourceUrls) =>
        preApprovedSourceUrls is { Count: > 0 }
            ? new PreApprovedSourceApproval(preApprovedSourceUrls)
            : ConsoleSourceApproval.For(json);

    /// <summary>
    /// The same shape as <see cref="CreateWithProgress"/>, with nothing to report and nothing to
    /// dispose.
    /// </summary>
    /// <remarks>
    /// For a command that only sometimes does slow work — <c>status</c> is instant until
    /// <c>--explain</c> asks it to fetch. Its own parameter rather than passing <c>json: true</c> to
    /// suppress the spinner: that argument also selects how NuGet source approval behaves, and
    /// borrowing it to mean "quiet" is how the second meaning eventually gets used by accident.
    /// <para>
    /// Quiet means no spinner, not no log: under <c>--verbose</c> its steps still reach the
    /// diagnostic log, which is where someone asking why <c>status --explain</c> is slow will look.
    /// </para>
    /// </remarks>
    public static (DependencyWorkflow Workflow, IDisposable Progress) CreateQuiet() =>
        (DiagnosticLog.IsEnabled(DiagnosticLevel.Verbose)
            ? Create().WithProgress(new DiagnosticProgressReporter(Dotquay.Core.Progress.NullProgressReporter.Instance))
            : Create(),
         NullDisposable.Instance);

    private sealed class NullDisposable : IDisposable
    {
        public static readonly NullDisposable Instance = new();

        public void Dispose()
        {
        }
    }

    /// <summary>Overrides are a pure manifest edit — no git, no NuGet, no cache (see
    /// <see cref="OverrideWorkflow"/>'s remarks for why that separation is deliberate).</summary>
    public static OverrideWorkflow CreateOverrides() => new(new SystemFileSystem());

    /// <summary>
    /// Declaring a feed needs the manifest and a NuGet client — the client only to verify the feed
    /// answers before the manifest is written. No git, no cache, nothing installed.
    /// </summary>
    /// <remarks>
    /// Wired through the same <see cref="Dotquay.Core.Dotnet.NuGetV3ClientFactory"/> and the same
    /// credential stack every other NuGet call uses, so `source add` verifies with exactly the
    /// credentials a later `add` will resolve with. Verifying with a different lookup would let a feed
    /// pass here and 401 on first use.
    /// </remarks>
    public static SourceWorkflow CreateSources() =>
        new(new SystemFileSystem(), new Dotquay.Core.Dotnet.NuGetV3ClientFactory(CredentialFactory.CreateResolver()));
}
