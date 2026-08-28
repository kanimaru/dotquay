using Dotquay.Core.Manifest;

namespace Dotquay.Core.Resolve;

/// <summary>A NuGet feed declared by an addon's own package manifest, paired with the addon that
/// declared it (SPEC.md §11.3).</summary>
/// <remarks>
/// The addon name travels with the feed because every downstream decision needs it: whether to
/// trust it, what to tell the user when asking, and what attribution to write into the project's
/// manifest so the block can later be explained rather than merely tolerated.
/// </remarks>
public sealed record AddonDeclaredSource(string AddonName, NuGetSourceSpec Source);
