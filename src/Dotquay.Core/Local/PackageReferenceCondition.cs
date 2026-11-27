using System.Xml.Linq;

namespace Dotquay.Core.Local;

/// <summary>
/// The MSBuild condition under which a <c>.csproj</c> declares a <c>PackageReference</c>, so the
/// <c>ProjectReference</c> a link swaps in for it applies exactly where the package would have.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why it matters.</strong> The overlay's <c>PackageReference Remove</c> is harmless
/// unconditionally — removing an item that was never added does nothing — but its
/// <c>ProjectReference</c> is not. A package the user declared only for Debug builds would come
/// back, linked, in every configuration: a test-only package then lands in an export build.
/// </para>
/// <para>
/// <strong>What is read.</strong> Every <c>PackageReference</c> whose <c>Include</c> list names the
/// package, outside any <c>Target</c> (items a target adds are never restored). Its condition is
/// the conjunction of its own <c>Condition</c> and those of its enclosing <c>ItemGroup</c> and
/// <c>Choose</c> branches — a <c>When</c> only applies if every <c>When</c> before it failed, an
/// <c>Otherwise</c> only if all of them did. A package declared several times applies when any
/// declaration does, so one unconditional declaration makes the link unconditional.
/// </para>
/// <para>
/// <strong>What it cannot see.</strong> A declaration in an imported file
/// (<c>Directory.Build.props</c> and the like) is not read, and neither is a later
/// <c>PackageReference Remove</c> in the <c>.csproj</c>. The overlay is imported at the end of the
/// project, so a property its condition reads that the <c>.csproj</c> changes <em>after</em> the
/// declaration is seen with its later value. None of these is how a package is normally
/// conditioned; each leaves the link as unconditional as it always was, never less.
/// </para>
/// </remarks>
public static class PackageReferenceCondition {
    /// <summary>
    /// The condition the <paramref name="project"/> declares <paramref name="packageId"/> under, or
    /// null when it applies unconditionally or is not declared at all.
    /// </summary>
    public static string? Read(XElement project, string packageId) {
        var declarations = new List<string>();

        foreach (var reference in project.Descendants("PackageReference")) {
            if (!Includes(reference, packageId) || InsideTarget(reference)) {
                continue;
            }

            var terms = TermsOf(reference);
            if (terms.Count == 0) {
                return null;
            }

            declarations.Add(terms.Count == 1 ? terms[0].Text : string.Join(" and ",
                terms.Select(t => t.Wrapped)));
        }

        var distinct = declarations.Distinct(StringComparer.Ordinal).ToList();
        return distinct.Count switch {
            0 => null,
            1 => distinct[0],
            _ => string.Join(" or ", distinct.Select(d => $"({d})")),
        };
    }

    private static bool Includes(XElement reference, string packageId) {
        var include = (string?)reference.Attribute("Include");
        return include is not null && include.Split(';')
            .Any(i => string.Equals(i.Trim(), packageId, StringComparison.OrdinalIgnoreCase));
    }

    private static bool InsideTarget(XElement reference) =>
        reference.Ancestors().Any(a => a.Name.LocalName == "Target");

    /// <summary>The conditions on the way from the project root down to
    /// <paramref name="reference"/>, outermost first.</summary>
    private static List<Term> TermsOf(XElement reference) {
        var terms = new List<Term>();

        foreach (var element in reference.AncestorsAndSelf().Reverse()) {
            if (element.Name.LocalName is "When" or "Otherwise") {
                foreach (var earlier in element.ElementsBeforeSelf()
                    .Where(e => e.Name.LocalName == "When")) {
                    if (ConditionOf(earlier) is { } failed) {
                        terms.Add(new Term($"!({failed})", Atomic: true));
                    }
                }
            }

            if (ConditionOf(element) is { } condition) {
                terms.Add(new Term(condition, Atomic: false));
            }
        }

        return terms;
    }

    private static string? ConditionOf(XElement element) {
        var condition = ((string?)element.Attribute("Condition"))?.Trim();
        return string.IsNullOrEmpty(condition) ? null : condition;
    }

    private sealed record Term(string Text, bool Atomic) {
        public string Wrapped => Atomic ? Text : $"({Text})";
    }
}
