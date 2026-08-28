using Dotquay.Core.Abstractions;
using Dotquay.Core.Auth;
using Dotquay.Core.Manifest;

namespace Dotquay.Core.Dotnet;

/// <summary>
/// Decides which addon-declared NuGet feeds a project takes on (SPEC.md §11.3): already declared,
/// trusted by host, approved by the user, or refused.
/// </summary>
/// <remarks>
/// Pure decision-making, no I/O beyond the approval callback — so the policy that governs a real
/// security boundary is testable without a filesystem, a network, or a console.
/// </remarks>
public sealed class SourceAdopter
{
    private readonly ISourceApproval _approval;

    public SourceAdopter(ISourceApproval? approval = null)
    {
        _approval = approval ?? DenyAllSourceApproval.Instance;
    }

    /// <param name="Adopted">Feeds to add to the project's manifest and use for resolution.</param>
    /// <param name="Refused">Feeds neither trusted nor approved. The caller decides whether that is
    /// fatal — it only is if something actually needed a package from one.</param>
    public sealed record Result(
        IReadOnlyList<NuGetSourceSpec> Adopted, IReadOnlyList<SourceAdoptionRequest> Refused);

    /// <summary>
    /// Evaluates every feed declared by an installed addon against what the project already has.
    /// </summary>
    /// <param name="manifest">The consuming project's manifest — the only place trust lives.</param>
    /// <param name="declaredByAddons">Addon name to the feeds its package manifest declared.</param>
    /// <param name="packagesByAddon">Addon name to the <c>[dotnet]</c> package IDs it declared, so an
    /// approval prompt can name the actual stakes.</param>
    public Result Evaluate(
        ProjectManifest manifest,
        IReadOnlyList<(string AddonName, NuGetSourceSpec Source)> declaredByAddons,
        IReadOnlyDictionary<string, IReadOnlyList<string>> packagesByAddon)
    {
        var adopted = new List<NuGetSourceSpec>();
        var refused = new List<SourceAdoptionRequest>();

        // Matched on URL, not name: two projects may call the same feed different things, and the URL
        // is the only identity a feed actually has.
        var known = new HashSet<string>(manifest.NuGetSources.Select(s => s.Url), StringComparer.OrdinalIgnoreCase);
        var usedNames = new HashSet<string>(manifest.NuGetSources.Select(s => s.Name), StringComparer.Ordinal);

        foreach (var (addonName, source) in declaredByAddons)
        {
            if (!known.Add(source.Url))
            {
                continue; // already declared by the project, or by an earlier addon in this pass
            }

            var packageIds = packagesByAddon.TryGetValue(addonName, out var ids) ? ids : [];
            var request = new SourceAdoptionRequest(addonName, source, packageIds);

            if (!IsTrusted(manifest.TrustedHosts, source.Url) && !_approval.Approve(request))
            {
                refused.Add(request);
                continue;
            }

            adopted.Add(source with
            {
                Name = UniqueName(source.Name, usedNames),
                DeclaredByAddon = addonName,
            });
        }

        return new Result(adopted, refused);
    }

    /// <summary>
    /// Whether a feed's host is one the project already trusts.
    /// </summary>
    /// <remarks>
    /// Host comparison goes through <see cref="CredentialScope"/> — deliberately the same normaliser
    /// the credential store uses, because two normalisers that disagree about what "the same host"
    /// means is exactly how a security boundary quietly becomes a suggestion. Exact match on
    /// host-and-port; no subdomain globbing, since a <c>*.example.com</c> rule silently matching an
    /// attacker-controlled <c>evil.example.com</c> is a trap that reads as safe.
    /// </remarks>
    public static bool IsTrusted(IReadOnlyList<string> trustedHosts, string url)
    {
        if (trustedHosts.Count == 0 || !CredentialScope.TryParse(url, out var scope, out _))
        {
            return false;
        }

        return trustedHosts.Any(entry =>
            CredentialScope.TryParse(entry, out var trusted, out _)
            && string.Equals(trusted.Host, scope.Host, StringComparison.Ordinal));
    }

    /// <summary>
    /// Describes why a <c>trusted_hosts</c> entry cannot do what it appears to say, or null when it's
    /// fine.
    /// </summary>
    /// <remarks>
    /// Every case here fails <em>silently</em> today, which is why <c>doctor</c> reports them: a
    /// wildcard or a typo matches nothing, so feeds are never adopted and the user is prompted
    /// forever without knowing why; an entry carrying a path trusts more than it appears to, since
    /// only the host is compared. A security control that quietly does the opposite of what it reads
    /// as is worse than no control.
    /// </remarks>
    public static string? DescribeTrustedHostProblem(string entry)
    {
        // Checked before parsing: '*' can survive Uri parsing on some inputs, and the point is to
        // catch the intent, not the parse result.
        if (entry.Contains('*', StringComparison.Ordinal) || entry.Contains('?', StringComparison.Ordinal))
        {
            return "uses a wildcard, which is not supported — matching is exact host and port, so this "
                + "entry trusts nothing. List each host explicitly.";
        }

        if (!CredentialScope.TryParse(entry, out var scope, out var error))
        {
            return $"is not a usable host: {error}";
        }

        if (scope.Path.Length > 0)
        {
            return "includes a path, which is ignored — trust is per host, so this entry trusts the "
                + $"whole of {scope.Host}. Drop the path to say what it actually does.";
        }

        if (!scope.IsSecure)
        {
            return "is plain HTTP. Packages from it would be fetched over cleartext.";
        }

        return null;
    }

    /// <summary>Keeps an adopted feed from colliding with a source name the project already uses —
    /// two <c>[nuget.sources.x]</c> blocks is a parse error, and silently overwriting the project's
    /// own source with an addon's would be worse.</summary>
    private static string UniqueName(string preferred, HashSet<string> used)
    {
        var candidate = NuGetSourceSpec.IsValidName(preferred) ? preferred : "adopted";

        var name = candidate;
        for (var suffix = 2; !used.Add(name); suffix++)
        {
            name = $"{candidate}-{suffix}";
        }

        return name;
    }
}
