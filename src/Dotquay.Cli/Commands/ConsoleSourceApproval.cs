using Dotquay.Core.Abstractions;

namespace Dotquay.Cli.Commands;

/// <summary>
/// Asks the user, at the terminal, whether to adopt a NuGet feed an addon declared (SPEC.md §11.3).
/// </summary>
/// <remarks>
/// Only ever constructed for an interactive, non-<c>--json</c> run. Everything else — CI, the editor
/// plugin, <c>install</c> — uses <see cref="DenyAllSourceApproval"/>, so a run that cannot ask a
/// human never adopts on their behalf.
/// </remarks>
internal sealed class ConsoleSourceApproval : ISourceApproval {
    /// <summary>The prompt, or <see cref="DenyAllSourceApproval"/> when there is no one to ask —
    /// <c>--json</c>, or redirected input, which covers CI and the plugin.</summary>
    public static ISourceApproval For(bool json) =>
        json || Console.IsInputRedirected ? DenyAllSourceApproval.Instance : new ConsoleSourceApproval();

    public bool Approve(SourceAdoptionRequest request) {
        // Raised mid-resolve, with the spinner sitting on the current line and still repainting.
        // Without this the question is written onto the spinner's row and then scribbled over while
        // the user reads it — which, for a question about trusting somebody else's feed, is the
        // worst one to make hard to read.
        using var quiet = ConsoleProgressReporter.Pause();

        ConsoleOutput.WriteLine(
            ConsoleColor.Yellow,
            $"Addon '{request.AddonName}' wants to add a NuGet source:");
        Console.WriteLine($"  {request.Source.Url}");

        // Naming the packages turns an abstract "trust this URL?" into a concrete question about
        // something the user actually asked for.
        if (request.PackageIds.Count > 0) {
            Console.WriteLine($"  for: {string.Join(", ", request.PackageIds)}");
        }

        Console.WriteLine(
            "  Packages would be fetched from there. Only accept a source you trust as much as the addon's code.");
        Console.Write("Add it to dotquay.toml? [y/N] ");

        var answer = Console.ReadLine()?.Trim();

        // Anything but an explicit yes is no, including a bare Enter. A default-yes on a security
        // question is a default-yes on someone else's supply chain.
        return string.Equals(answer, "y", StringComparison.OrdinalIgnoreCase)
            || string.Equals(answer, "yes", StringComparison.OrdinalIgnoreCase);
    }
}
