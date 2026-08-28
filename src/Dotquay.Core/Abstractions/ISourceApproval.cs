using Dotquay.Core.Manifest;

namespace Dotquay.Core.Abstractions;

/// <summary>
/// Decides whether to adopt a NuGet feed an addon declared, when the feed's host isn't already
/// trusted (SPEC.md §11.3).
/// </summary>
/// <remarks>
/// A seam rather than a console prompt inside the workflow, for the same reason every other effect in
/// <c>Dotquay.Core</c> is one: the Godot editor plugin drives dotquay through captured stdout and has
/// no way to answer a question, and a workflow that blocks on <c>Console.ReadLine</c> is untestable.
/// The CLI implements it as a prompt; <c>--json</c>, CI, and <c>install</c> use
/// <see cref="DenyAllSourceApproval"/>.
/// </remarks>
public interface ISourceApproval
{
    /// <summary>True to adopt the feed. Never called for a host already in
    /// <c>[nuget] trusted_hosts</c>, and never called at all during <c>install</c>.</summary>
    bool Approve(SourceAdoptionRequest request);
}

/// <summary>What the user is being asked to agree to: which addon wants which feed, and which
/// packages depend on the answer.</summary>
/// <param name="AddonName">The addon whose package manifest declared the feed.</param>
/// <param name="Source">The feed itself.</param>
/// <param name="PackageIds">The addon's <c>[dotnet]</c> packages that would resolve from it — the
/// concrete stakes, so the question isn't "trust this URL?" in the abstract.</param>
public sealed record SourceAdoptionRequest(
    string AddonName, NuGetSourceSpec Source, IReadOnlyList<string> PackageIds);

/// <summary>
/// Refuses every request.
/// </summary>
/// <remarks>
/// The default, and deliberately so. Anywhere that can't ask a human — a CI run, <c>--json</c>, the
/// editor plugin, <c>install</c> replaying a lockfile — must not quietly adopt a feed on the user's
/// behalf; it should fail and say what to add. Denial is not an error in itself: the caller decides
/// whether an unadopted feed is fatal.
/// </remarks>
public sealed class DenyAllSourceApproval : ISourceApproval
{
    public static readonly DenyAllSourceApproval Instance = new();

    public bool Approve(SourceAdoptionRequest request) => false;
}
