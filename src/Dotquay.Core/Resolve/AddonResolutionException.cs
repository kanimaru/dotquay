namespace Dotquay.Core.Resolve;

/// <summary>Thrown when an <see cref="Manifest.AddonSpec"/> can't be resolved to a commit — an
/// invalid version constraint, or no tag satisfying it. The message is written to be shown to the
/// user as-is and always names the addon (SPEC.md P0-2's "clear message" acceptance criterion).</summary>
public sealed class AddonResolutionException : Exception {
    public AddonResolutionException(string message)
        : base(message) {
    }
}
