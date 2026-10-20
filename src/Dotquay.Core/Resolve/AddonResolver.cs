using System.Diagnostics;
using Dotquay.Core.Abstractions;
using Dotquay.Core.Diagnostics;
using Dotquay.Core.Manifest;
using Dotquay.Core.Versioning;

namespace Dotquay.Core.Resolve;

/// <summary>
/// Turns an <see cref="AddonSpec"/> into a pinned commit — the resolver stage of the pipeline
/// (SPEC.md §5). Reads only the manifest and live git refs; never touches the lockfile or disk
/// (that's the sync engine's job). No dependency-graph walk here — package-manifest-declared
/// dependencies and unification are P1-2/v0.4 (SPEC.md §16), out of scope for v0.1.
/// </summary>
public sealed class AddonResolver
{
    private readonly IGitClient _gitClient;

    public AddonResolver(IGitClient gitClient)
    {
        _gitClient = gitClient;
    }

    public Task<ResolvedAddon> ResolveAsync(AddonSpec spec, CancellationToken cancellationToken = default) =>
        spec.SourceKind switch
        {
            AddonSourceKind.Rev => Task.FromResult(ResolveRev(spec)),
            AddonSourceKind.Branch => ResolveBranchAsync(spec, cancellationToken),
            AddonSourceKind.Version => ResolveVersionAsync(spec, cancellationToken),
            _ => throw new UnreachableException($"Unknown {nameof(AddonSourceKind)}: {spec.SourceKind}"),
        };

    private static ResolvedAddon ResolveRev(AddonSpec spec)
    {
        // A hard pin: the manifest already names the exact commit. No pseudo-version needed —
        // display the rev as given (SPEC.md §7.1, "hard pin for untagged repos").
        DiagnosticLog.Write(DiagnosticLevel.Verbose, "resolve", $"{spec.Name}: rev pin {spec.Rev}, no lookup needed");
        return new(spec.Name, spec.Git, spec.Rev!, DisplayVersion: null, spec.EffectivePath, spec.InstallAs);
    }

    private async Task<ResolvedAddon> ResolveBranchAsync(AddonSpec spec, CancellationToken cancellationToken)
    {
        var sha = await _gitClient.ResolveBranchTipAsync(spec.Git, spec.Branch!, cancellationToken);
        DiagnosticLog.Write(DiagnosticLevel.Verbose, "resolve", $"{spec.Name}: branch '{spec.Branch}' tip is {sha}");
        return new ResolvedAddon(spec.Name, spec.Git, sha, DisplayVersion: null, spec.EffectivePath, spec.InstallAs);
    }

    private async Task<ResolvedAddon> ResolveVersionAsync(AddonSpec spec, CancellationToken cancellationToken)
    {
        if (!VersionConstraint.TryParse(spec.Version!, out var constraint) || constraint is null)
        {
            throw new AddonResolutionException($"'{spec.Name}': '{spec.Version}' is not a valid version constraint.");
        }

        var refs = await _gitClient.ListRemoteRefsAsync(spec.Git, cancellationToken);
        var match = TagSelector.SelectHighestMatching(refs, constraint);

        DiagnosticLog.Write(
            DiagnosticLevel.Verbose,
            "resolve",
            $"{spec.Name}: '{spec.Version}' against {refs.Count(r => r.Kind == GitRefKind.Tag)} tag(s) -> "
                + (match is null ? "no match" : $"{match.Version.OriginalTag} ({match.Ref.CommitSha})"));

        if (match is null)
        {
            var hint = refs.Any(r => r.Kind == GitRefKind.Tag)
                ? "no tag satisfies the constraint — check the addon's releases, or pin with 'branch'/'rev' instead."
                : "this repository has no tags at all — pin with 'branch' or 'rev' instead of 'version' (SPEC.md requirement P0-2).";
            throw new AddonResolutionException($"'{spec.Name}': {hint}");
        }

        return new ResolvedAddon(
            spec.Name, spec.Git, match.Ref.CommitSha, match.Version.OriginalTag, spec.EffectivePath, spec.InstallAs);
    }
}
