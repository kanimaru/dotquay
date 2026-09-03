# Design — v0.10: PackageReference asset metadata

Status: **implemented and verified against a real NativeAOT binary**, 2026-09-03.
707 C# tests green. See *As built* at the end for where the implementation departed from this design.

Motivating case: gdUnit4, whose three packages cannot currently be expressed by dotquay.

```xml
<PackageReference Include="gdUnit4.api" Version="5.1.0-rc1"/>
<PackageReference Include="gdUnit4.test.adapter" Version="3.0.0"/>
<PackageReference Include="gdUnit4.analyzers" Version="1.0.0">
  <PrivateAssets>none</PrivateAssets>
  <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
</PackageReference>
```

## The problem

`DotnetSpec` carries `Name`, `Version`, and an optional feed `Source`.
`CsprojPackageReferenceWriter.AddOrUpdate` emits exactly `Include` and `Version`. There is no
representation, anywhere in the pipeline, for NuGet's asset-flow metadata — so the third reference
above is not something dotquay can write, record, or check.

This matters much less for a project's own `.csproj` than it first appears. The `.csproj` is
committed, and `AddOrUpdate` only calls `SetAttributeValue("Version", …)` on an existing element —
it never touches child elements. So a hand-added `<PrivateAssets>` survives clones, survives
`update`, and survives `install`. Annoying, not broken.

**The real hole is addon-declared packages.** `DependencyGraphResolver` walks every installed addon's
package manifest, unions its `[dotnet.*]` entries (requirement P1-5, shipped v0.4), and installs them
into the consuming project through that same writer. An addon that needs an analyzer therefore
*cannot ship a correct dependency*: the consumer gets a bare `Include`+`Version`, the analyzer's
assets flow wrongly or not at all, nothing in the manifest is wrong to look at, and `doctor` has
nothing to report. The addon author has no workaround — they cannot hand-edit a `.csproj` they
don't own.

That is a correctness gap in a feature already shipped, not a convenience request. It is the reason
to do this.

## Decisions

| # | Decision | Alternatives rejected |
|---|---|---|
| D1 | Three verbatim passthrough fields: `include_assets`, `exclude_assets`, `private_assets` | A `kind = "analyzer"` preset — see below, gdUnit4 is the counterexample that kills it. NuGet already has a preset mechanism (`developmentDependency` in the `.nuspec`), owned by the package author, which is the right layer |
| D2 | TOML arrays of tags; the writer joins with `;` | A semicolon string mirroring the XML — reads acceptably for `"none"` and badly for six tags, and two accepted spellings is drift waiting to happen |
| D3 | Unknown asset tags **warn and pass through**; only structural errors (mixing `all`/`none` with other tags, a non-array, a non-string element) are parse errors | Rejecting unknown tags at parse time. `install_as` is rejected because it aims a recursive delete (§7.1); an unknown asset tag's worst case is an MSBuild no-op, and a hard error would make dotquay the reason a future NuGet tag can't be used. Warning still catches the typo — this "changes the silence, not the resolution" (§7.2) |
| D4 | Where declared, the manifest **owns** the three elements; where absent, dotquay never touches them | Never removing (leaves stale metadata that the manifest disclaims); always owning (would delete today's hand-edits on the next `install`, silently) |
| D5 | The lockfile records what dotquay wrote | Without it, "the manifest stopped declaring `private_assets`" and "the user hand-added `<PrivateAssets>`" are the same observation, and D4 is unimplementable |
| D6 | Addon-vs-addon disagreement warns and first-wins; a project declaration always wins | A hard error like the `install_as` conflict (§8) — disproportionate for something whose failure mode is a build-time no-op, and the user can already resolve it by declaring the package themselves. Also rejected: unioning the sets — the union of `all` and `none` is meaningless, and a merged `PrivateAssets` is a third behaviour neither author asked for |
| D7 | A `doctor` check for drift between `.csproj` and manifest | Silent reconciliation on the next `install` — dotquay would be editing a file the user hand-edited, without saying so |

### Why passthrough and not presets

A `kind = "analyzer"` shorthand is the obvious ergonomic win and it is wrong. The conventional
analyzer incantation is `PrivateAssets=all` — deliberately *not* flowing to consumers. gdUnit4
declares `PrivateAssets=none`, deliberately flowing the analyzer onward so that a project consuming
a project that uses gdUnit4 still gets it.

A preset would emit the exact opposite of what the motivating package needs, and it would do so
silently. If the abstraction gets the one case that prompted it backwards, it is not the
abstraction.

## Semantics being modelled

From NuGet's `PackageReference` documentation, unchanged since NuGet 5.0:

| Tag | Meaning | Default |
|---|---|---|
| `IncludeAssets` | consumed by this project | `all` |
| `ExcludeAssets` | not consumed, subtracted from the above | `none` |
| `PrivateAssets` | consumed here, but not flowed to the parent project | `contentfiles;analyzers;build` |

Tag set — closed, and the only values dotquay recognises without warning:

`compile`, `runtime`, `contentFiles`, `build`, `buildMultitargeting`, `buildTransitive`,
`analyzers`, `native`, plus `all` and `none`.

`all` and `none` must appear alone; every other combination is a semicolon-separated list.
Comparison is case-insensitive (MSBuild's own rule — gdUnit4 writes `contentfiles`,
`buildtransitive`), and dotquay emits canonical casing.

`exclude_assets` is genuinely part of this set and not padding: `ExcludeAssets="compile"` is the
standard way to take a package's runtime assets without compiling against it, and it is the only
one of the three that is *subtractive*, so it cannot be expressed by the other two without
enumerating the complement by hand.

## Manifest surface — §7.1 / §7.2

```toml
[dotnet."gdUnit4.api"]
version = "5.1.0-rc1"

[dotnet."gdUnit4.test.adapter"]
version = "3.0.0"

[dotnet."gdUnit4.analyzers"]
version = "1.0.0"
private_assets = ["none"]
include_assets = ["runtime", "build", "native", "contentFiles", "analyzers", "buildTransitive"]
```

All three keys are optional. Omitted means "not declared", which under D4 means dotquay leaves that
element alone — *not* "declared as the NuGet default".

### Reader changes

`ManifestReader.CollectDotnetSpecs` currently throws on any non-table key that is not `version` or
`source`:

```csharp
else if (key is not ("version" or "source"))
{
    throw new ManifestParseException(…);
}
```

The whitelist extends to the three new keys, each read as a `TomlArray` of strings.

The dotted-package-ID ambiguity that `source` has does not recur. A package genuinely named
`Foo.include_assets` is written `[dotnet.Foo.include_assets]` — a *table* — while the metadata key
is an *array*, and TOML cannot have both under one key. Same disambiguation-by-type rule as `source`,
documented the same way on `DotnetSpec`.

Leaf detection is unchanged: a table is a package spec iff it carries a string `version`. Asset keys
on a table without `version` belong to a path segment and are ignored — worth a test, not worth a
diagnostic.

### Writer changes

`ManifestWriter.AppendDotnet` emits each present array after `version`/`source`, in the fixed order
`include_assets`, `exclude_assets`, `private_assets`, so a round-trip is byte-stable.

### Forward-compatibility — call this out in the release notes

Because the reader *throws* on unrecognised scalar keys rather than ignoring them, a manifest using
these fields is **unreadable to pre-v0.10 dotquay** — not degraded, rejected. For a project manifest
that is the user's own problem and obvious. For a *package* manifest it means an addon adopting this
feature breaks every consumer still on an older dotquay.

This is not new: `source` (v0.7) had exactly the same property. The precedent is to ship it and say
so. Addon authors with older consumers should not adopt the fields until they are willing to require
v0.10.

## `.csproj` ownership — D4 in detail

`CsprojPackageReferenceWriter.AddOrUpdate` gains an optional asset-metadata parameter. For each of
the three elements independently:

| Manifest | Lockfile says dotquay wrote it | Action |
|---|---|---|
| declares | — | write/replace the child element |
| silent | no | leave whatever is there (today's behaviour; hand-edits survive) |
| silent | yes | remove the child element |

The third row is the one the lockfile exists for. Element children other than the three — a
`Condition` attribute, a `NoWarn`, anything a user added — are never touched, which the existing
"preserves unrelated content" guarantee already implies and which needs a test that pins it.

**Both spellings must be recognised.** MSBuild treats item metadata written as an attribute and as a
child element as identical, so these mean the same thing:

```xml
<PackageReference Include="gdUnit4.analyzers" Version="1.0.0" PrivateAssets="none" />

<PackageReference Include="gdUnit4.analyzers" Version="1.0.0">
  <PrivateAssets>none</PrivateAssets>
</PackageReference>
```

dotquay **writes** the child-element form (it is what the ecosystem's documentation and gdUnit4's own
README use, and it diffs one value per line). But it must **match** either when looking for existing
metadata, because a hand-edited `.csproj` may well use attributes. Miss that and the writer adds a
child element beside an existing attribute — leaving one reference carrying the same metadata twice,
in two forms, with MSBuild's precedence deciding the winner rather than dotquay.

`Remove` is unchanged: it deletes the whole element.

## Lockfile — §7.3

```toml
[[dotnet]]
name = "gdUnit4.analyzers"
resolved = "1.0.0"
private_assets = ["none"]
include_assets = ["runtime", "build", "native", "contentFiles", "analyzers", "buildTransitive"]
```

`LockedDotnet` gains the three optional arrays, omitted when null — so lockfiles written before
v0.10 read unchanged, the same back-compat argument `Source` carries.

Recording it is what makes an addon-declared analyzer reproducible: the consuming project's manifest
never mentions the package, so without the lockfile the metadata exists only in whichever remote
manifest happened to be readable at install time.

## Resolution — D6

`DependencyGraphResolver` currently does `addonDeclaredDotnet.TryAdd(spec.Name, spec)` — first
addon wins, silently, for version and now for metadata too. Two addons declaring the same package
with *different* asset metadata gains a warning naming both addons and the winner, alongside the
existing §17 warnings. A project-level `[dotnet.*]` entry overrides any addon's, per §8's "project
always wins".

## Doctor — D7

New `DoctorIssueKind.PackageAssetMetadataDrift`: the `.csproj`'s asset elements for a package
disagree with what the manifest declares.

Note the wire contract on that enum — each member maps to a fixed string in `JsonMapper` and is read
by the editor plugin, so this is a JSON contract change, not an internal addition. The plugin needs
the new kind before it will render the issue.

Offline and cheap, like the other §7.2-era checks. It is what keeps D4 honest: "the manifest owns
these elements" is only true if something notices when the file says otherwise.

## JSON envelope

`DotnetJson` (`list --json`) and `PreviewDotnetJson` (`add --preview`) each gain the three optional
arrays, so the editor plugin can show what an install will write before it writes it — the whole
point of v0.9's preview.

These are new `IReadOnlyList<string>` members on existing records. Per the v0.9 lesson, verify
serialisation from a **published NativeAOT binary**, not `dotnet run`: a payload the source
generator does not cover builds and passes tests under JIT and fails only in the real binary.

## SPEC.md edits this implies

- §7.1 manifest example — a `[dotnet.*]` entry showing the three keys.
- §7.3 lockfile example — the same on `[[dotnet]]`.
- §11 — the asset-metadata table and the closed tag set.
- §17 — the unknown-tag warning and the addon-disagreement warning.
- Requirements table — a new P1 row for asset metadata, and a note on P1-5 that addon-declared
  packages now carry it.

## Non-goals

`Condition`, `GeneratePathProperty`, `Aliases`, `NoWarn`, `IsImplicitlyDefined`, and Central Package
Management / `Directory.Packages.props`.

The line is deliberate and worth stating in the doc rather than discovering per-request: the three
asset fields are a closed set answering one question — *which parts of this package do I consume,
and what flows onward* — which is a property of the dependency and therefore belongs to whoever
declares the dependency. Everything else on that list is per-project build wiring, which is a
property of the *project*, and dotquay touches the `.csproj` only through the one element kind it
manages. "We added `PrivateAssets`, so why not `Condition`" has an answer, and this is it.

## Test plan

Reader:

- each field individually and all three together, round-tripped through the writer byte-for-byte
- `["all"]` / `["none"]` accepted; `["all", "runtime"]` rejected with a message naming the rule
- a non-array value, and an array containing a non-string, rejected
- an unknown tag (`"analyzer"`, singular) warns, passes through, does not throw
- case-insensitive acceptance (`"contentfiles"`) normalised to canonical casing on write
- a package named `Foo.include_assets` still parses as a package, not as metadata on `Foo`
- asset keys on a table with no `version` are ignored

`CsprojPackageReferenceWriter`:

- add with metadata writes the child elements
- update with changed metadata replaces them, does not duplicate
- update with no metadata and no lockfile record leaves existing children untouched
- update with no metadata and a lockfile record removes them
- unrelated children and attributes (`Condition`, `NoWarn`) survive every path above
- an existing hand-written element with mismatched casing is matched, not duplicated
- metadata written by hand as an **attribute** (`PrivateAssets="none"`) is found, replaced in place,
  and never duplicated as a child element beside itself
- the same for removal: an attribute-form value the lockfile records is removed as an attribute

Lockfile: round-trip; a pre-v0.10 lockfile with no asset keys reads clean.

Resolver: two addons, same package, different metadata → one warning, first wins, both named.
Project entry beats both.

Doctor: drift detected; no drift on a freshly installed project; the new kind serialises.

End to end, per the standing rule that a green suite is not evidence for this project: a real
`dotquay add` of the three gdUnit4 packages against a published NativeAOT binary, then
`dotnet restore` and an actual gdUnit4 test run — the analyzer either loads or it doesn't, and that
is the only check that tests the thing the feature is for.

## As built

Five decisions were made during implementation that this design did not settle.

**D8 — no CLI flags; the fields are hand-edited.** `dotquay add` has flags for the addon-shaping keys
(`--path`, `--install-as`, `--version`, `--branch`, `--rev`) but none for `[dotnet.*] source`, the
directly analogous v0.7 field. Asset metadata follows `source`: declare it in `dotquay.toml`, and
dotquay honours it from then on. Three flags taking repeatable tag values would be an awkward CLI
surface for something written once per package and then left alone.

**`install` replays the lockfile's metadata; `update` picks up a manifest edit.** Install reproduces
the lockfile exactly (SPEC.md §5) — the same rule that stops it re-resolving a version or switching a
feed. So editing `dotquay.toml` and running `install` deliberately does nothing. That would be silent,
which is why the `doctor` check below is not optional garnish: it is what turns "nothing happened"
into a sentence.

**The doctor check needed no plugin change.** `_report_doctor` in `dotquay_panel.gd` renders each
issue's `message` and never switches on `kind`, so a new `DoctorIssueKind` renders correctly with no
GDScript edit. The lockstep warning on that enum applies to kinds the plugin *maps* (badges from
`AddonStatusKind`), not to doctor issues. Worth knowing before assuming the reverse.

**JSON carries the fields only where they are known.** `DotnetJson` gained the three lists, populated
from the lockfile by `list --json` — what is actually wired up. The graph view reports declarations
and resolves no lockfile, so it leaves them null rather than implying it checked; `PreviewDotnetJson`
was left alone for the same reason. Verified from the published binary, which is the only check that
catches a payload the source generator does not cover.

**Unknown tags surface as a property, not a reader warning.** `ManifestReader` has no warnings
channel — it throws or returns — and giving it one would have touched every call site. Unknown tags
are preserved on `PackageAssets` and exposed as `UnknownTags` for a caller that has a warnings channel
to report. The tags round-trip correctly today; nothing reports them yet.

### Verified end to end (published NativeAOT binary, real gdUnit4 packages)

A project declaring all three gdUnit4 packages, with the analyzer's `private_assets = ["none"]` and
its six-tag `include_assets`:

- **`doctor --json`** parsed the manifest's asset arrays, ran the drift check, and serialised
  `package_asset_metadata_drift` — exit 2 with the issue naming the package, before any write.
- **`install`** wrote the child elements into the `.csproj` and `dotnet restore` accepted the result,
  exit 0.
- **`doctor --json`** afterwards: healthy.
- **`list --json`** emitted `include_assets`/`private_assets` with `exclude_assets` null — the check
  that matters under AOT, since a payload missing from `DotquayJsonContext` builds and passes tests
  under JIT and fails only in the real binary.

Not yet verified: an actual gdUnit4 test run confirming the analyzer loads. The `.csproj` now says
exactly what gdUnit4's README asks for and restore is happy with it, but "the analyzer runs" is a
claim only a real test run supports.

## Cost

Small and contained. Three optional properties on `DotnetSpec` and `LockedDotnet`, ~40 lines across
the reader/writer pair, an optional parameter and a child-element sync on the csproj writer, one
resolver warning, one doctor check, two JSON records. No new dependency, no new command, no change
to any existing default.
