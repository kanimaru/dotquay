using Dotquay.Core.Abstractions;
using Dotquay.Core.Local;

namespace Dotquay.Core.Sync;

/// <summary>What <c>addons/&lt;folder&gt;</c> currently is, relative to what the project
/// declares.</summary>
public enum AddonLinkState {
    /// <summary>A real folder (or nothing yet). dotquay owns it and may write it.</summary>
    NotLinked,

    /// <summary>A link pointing exactly where <c>dotquay.local.toml</c> says it should. Never
    /// written through.</summary>
    LinkedAsDeclared,

    /// <summary>A link, but at a folder this project's declaration does not name.</summary>
    TargetMismatch,

    /// <summary>A link this project declares nothing about at all.</summary>
    Undeclared,

    /// <summary>A link whose target is gone — the checkout was moved or deleted.</summary>
    TargetMissing,
}

/// <summary>The classification plus the two paths that produced it, so a message can name
/// both.</summary>
public sealed record AddonLinkVerdict(AddonLinkState State, string? ActualTarget,
    string? DeclaredTarget) {
    /// <summary>True when writing addon content to this destination would leave the folder dotquay
    /// owns.</summary>
    public bool ForbidsWriting =>
        State is AddonLinkState.TargetMismatch or AddonLinkState.Undeclared
        or AddonLinkState.TargetMissing;
}

/// <summary>
/// Decides whether it is safe to write an addon into <c>addons/&lt;folder&gt;</c>, by asking the
/// filesystem what is there rather than trusting <c>dotquay.local.toml</c> alone
/// (docs/DESIGN-v0.16-LINK-SAFETY.md).
///
/// <para>
/// A junction and a declaration are two independent statements about the same folder, maintained by
/// different acts at different times. They stop agreeing routinely — the checkout moves, the link
/// is created from one project while the declaration lives in another, the gitignored local file is
/// deleted while the junction survives. When they disagree, dotquay has been handed two
/// contradictory claims and must not guess which one the human meant: every silent branch ends with
/// package content written into a repository dotquay does not own, under a path neither project's
/// git is watching.
/// </para>
/// <para>
/// Kept separate from <see cref="AddonInstaller"/> so the rules can be read and tested on their
/// own, and so <c>doctor</c> reports exactly the states the installer refuses on — one source of
/// truth for "is this folder ours", not two implementations that drift.
/// </para>
/// </summary>
public static class AddonLinkGuard {
    /// <summary>
    /// Classify <paramref name="destinationPath"/> against <paramref name="declared"/> (the link
    /// this project declares for the addon, or null when it declares none).
    /// </summary>
    public static AddonLinkVerdict Inspect(IFileSystem fileSystem, string destinationPath,
        LocalLink? declared) {
        var actual = fileSystem.ResolveDirectoryLinkTarget(destinationPath);

        if (actual is null) {
            // Not a link. A declaration without a link on disk is not a problem: that is simply an
            // addon that has not been installed since it was linked, and installing is what fixes
            // it.
            return new AddonLinkVerdict(AddonLinkState.NotLinked, null, declared?.Path);
        }

        if (declared is null) {
            return new AddonLinkVerdict(AddonLinkState.Undeclared, actual, null);
        }

        if (!fileSystem.DirectoryExists(declared.Path)) {
            return new AddonLinkVerdict(AddonLinkState.TargetMissing, actual, declared.Path);
        }

        return SamePath(fileSystem, actual, declared.Path)
            ? new AddonLinkVerdict(AddonLinkState.LinkedAsDeclared, actual, declared.Path)
            : new AddonLinkVerdict(AddonLinkState.TargetMismatch, actual, declared.Path);
    }

    /// <summary>
    /// Whether two paths name the same folder. Spelling is not meaning: separators, trailing
    /// slashes and (on Windows) casing are all noise, the same reasoning <c>GitUrl</c> applies to
    /// remotes.
    /// </summary>
    public static bool SamePath(IFileSystem fileSystem, string left, string right) =>
        string.Equals(
            Canonical(fileSystem, left),
            Canonical(fileSystem, right),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal);

    private static string Canonical(IFileSystem fileSystem, string path) =>
        fileSystem.GetFullPath(path).Replace('\\', '/').TrimEnd('/');

    /// <summary>
    /// The refusal, naming both statements and both ways out. Either can be the stale one and
    /// dotquay cannot tell which, so it reports what it found and stops rather than picking.
    /// </summary>
    public static string Explain(string name, string folder, string incomingVersion,
        AddonLinkVerdict verdict) {
        var second = verdict.State switch {
            AddonLinkState.Undeclared =>
                $"but {LocalLinks.FileName} declares no link for '{name}'.",
            AddonLinkState.TargetMissing =>
                $"and {LocalLinks.FileName} declares this project's '{name}' link "
                + $"as{Environment.NewLine}  {verdict.DeclaredTarget}{Environment.NewLine}"
                + "which does not exist.",
            _ =>
                $"but {LocalLinks.FileName} declares this project's '{name}' link "
                + $"as{Environment.NewLine}  {verdict.DeclaredTarget}",
        };

        return $"addons/{folder} is a link to{Environment.NewLine}"
            + $"  {verdict.ActualTarget}{Environment.NewLine}"
            + $"{second}{Environment.NewLine}{Environment.NewLine}"
            + $"Installing {incomingVersion} would write into a folder dotquay does not own. "
            + $"Nothing was changed.{Environment.NewLine}"
            + $"Update the link to match:   dotquay link {name} "
            + $"{verdict.ActualTarget}{Environment.NewLine}Or drop it and install:     dotquay "
            + $"link {name} --remove";
    }
}
