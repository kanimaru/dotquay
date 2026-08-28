namespace Dotquay.Core.Abstractions;

/// <summary>
/// Approves exactly the feed URLs it was given, and refuses everything else (SPEC.md §11.3).
/// </summary>
/// <remarks>
/// The other half of the two-phase flow the editor plugin uses: <c>add --preview</c> reports which
/// feeds an addon would bring in, the user agrees to specific ones, and the real <c>add</c> replays
/// that decision through this approval. It is deliberately a whitelist of exact URLs rather than a
/// "yes to everything" flag, because between the preview and the confirmation the upstream addon may
/// have changed what it declares — approving a URL the user never saw is the failure mode this whole
/// seam exists to prevent.
/// <para>
/// Matching is on URL and ignores case, the same identity <see cref="Dotquay.Core.Dotnet.SourceAdopter"/>
/// uses when deciding whether a feed is already known. Two different notions of "the same feed" is how
/// a security boundary quietly becomes a suggestion.
/// </para>
/// </remarks>
public sealed class PreApprovedSourceApproval : ISourceApproval
{
    private readonly HashSet<string> _urls;

    public PreApprovedSourceApproval(IEnumerable<string> urls)
    {
        _urls = new HashSet<string>(urls, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The URLs this approval will say yes to — exposed so a caller can report back exactly
    /// what it was asked to pre-approve, including entries that turned out to match nothing.</summary>
    public IReadOnlyCollection<string> Urls => _urls;

    public bool Approve(SourceAdoptionRequest request) => _urls.Contains(request.Source.Url);

    /// <summary>Falls back to <see cref="DenyAllSourceApproval"/> when nothing was pre-approved, so a
    /// caller never has to special-case an empty list into a different implementation.</summary>
    public static ISourceApproval For(IReadOnlyList<string>? urls) =>
        urls is null || urls.Count == 0 ? DenyAllSourceApproval.Instance : new PreApprovedSourceApproval(urls);
}
