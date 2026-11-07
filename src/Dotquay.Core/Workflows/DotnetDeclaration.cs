using Dotquay.Core.Dotnet.SourceBuild;
using Dotquay.Core.Lockfile;

namespace Dotquay.Core.Workflows;

/// <summary>What <see cref="DependencyWorkflow.DeclareDotnetAsync"/> did with a package ID
/// (DESIGN-v0.19 §7a.1).</summary>
public enum DotnetDeclarationOutcome {
    /// <summary>The project didn't know the package; it is now declared, locked and referenced.</summary>
    Added,

    /// <summary>The package was locked but not declared by the project (an addon declared it, or it was
    /// an orphan). The project now declares it at the requested version, which wins over any addon's
    /// declaration (requirement P1-5).</summary>
    Pinned,

    /// <summary>The project already declared the package; its declared version was changed.</summary>
    Changed,

    /// <summary>The project already declared exactly this version, and it is what's locked. Nothing was
    /// written and no feed was asked.</summary>
    Unchanged,
}

/// <param name="Locked">The package as it is locked now.</param>
/// <param name="Outcome">Which of the three declaration cases applied.</param>
/// <param name="PreviousResolved">The version locked before this call, or null when nothing was
/// locked. Reported as <c>changes[].from</c>.</param>
public sealed record DotnetDeclaration(LockedDotnet Locked, DotnetDeclarationOutcome Outcome, string? PreviousResolved) {
    /// <summary>Set when the project builds the package from source, so its <c>.csproj</c> got no
    /// <c>PackageReference</c> (docs/DESIGN-v0.26-SOURCE-BUILT-PACKAGES.md).</summary>
    public IReadOnlyList<SourceBuiltPackage> SourceBuilt { get; init; } = [];

    /// <summary><see cref="Describe"/> followed by any source-build note: the <c>messages</c> the CLI and
    /// the MCP tool both send.</summary>
    public IReadOnlyList<string> Messages(string? pinnedSourceName) =>
        [Describe(pinnedSourceName), .. SourceBuilt.Select(s => s.Describe())];

    /// <summary>A one-line human summary, shared by the CLI and the MCP tool so both say the same
    /// thing about the same outcome.</summary>
    public string Describe(string? pinnedSourceName) {
        var pinnedTo = pinnedSourceName is null ? "" : $", pinned to '{pinnedSourceName}'";
        var kept = Locked.Assets.Private is null ? "" : ", kept private to this project";

        return Outcome switch {
            DotnetDeclarationOutcome.Pinned =>
                $"Pinned '{Locked.Name}' to {Locked.Resolved} in your dotquay.toml (was {PreviousResolved ?? "unlocked"}, "
                + $"chosen by an addon){pinnedTo}{kept}. Your entry now wins over the addon's; remove it to hand the "
                + "choice back.",
            DotnetDeclarationOutcome.Changed =>
                $"Changed '{Locked.Name}' from {PreviousResolved ?? "unlocked"} to {Locked.Resolved} (NuGet) -> .csproj, "
                + $"restored{pinnedTo}{kept}",
            DotnetDeclarationOutcome.Unchanged =>
                $"'{Locked.Name}' is already declared and locked at {Locked.Resolved}; nothing to change.",
            _ => $"Added '{Locked.Name}' @ {Locked.Resolved} (NuGet) -> .csproj, restored{pinnedTo}{kept}",
        };
    }
}
