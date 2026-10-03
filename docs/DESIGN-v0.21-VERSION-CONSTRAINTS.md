# DESIGN v0.21 — one version-constraint language, honoured on both sides

Status: shipped 2026-09-24 (all four phases), verified against a real NativeAOT binary and nuget.org — see §8.

## 1. The gap

dotquay has two version fields and treats them in two unrelated ways.

- `[addons.*] version` goes through `VersionConstraint`: a bare version is exact, `~I.J` is a
  Cargo-style tilde range, and `update` picks the highest tag inside it.
- `[dotnet.*] version` is documented as "delegated to NuGet/dotnet restore verbatim" (SPEC.md §7.1,
  `DotnetSpec.Version`), but nothing reads it after `add`. `SyncDotnetAsync` resolves every package
  with `versionOverride: null`, so `update` moves every NuGet package to the newest **stable**
  version on its feed, whatever the manifest says.

Reported case (BotV2, 2026-09-23): the manifest pinned `gdUnit4.api = "5.1.0-rc1"` and
`gdUnit4.test.adapter = "3.0.0"`. `update` wrote `gdUnit4.api 5.0.0` (the prerelease pin was dropped
for an *older* stable) and `gdUnit4.test.adapter 3.1.1` (the exact pin was ignored). Adapter 3.1.1
requires `gdUnit4.api >= 5.1.0-rc5`, `dotnet restore` failed with NU1605, and the whole Godot project
stopped compiling. Two more things made it worse:

- The `.csproj` had already been rewritten when the restore failed, and the lockfile had not, so the
  project was left with a csproj that matched neither the manifest nor the lockfile.
- Addon-declared `[dotnet.*]` entries are merged first-discovered-wins
  (`DependencyGraphResolver.WalkAsync`), with a comment saying NuGet ranges cannot be intersected.
  Once dotquay reads the ranges itself, they can.

## 2. Survey — how other package managers write constraints

| Tool | Bare `1.2.3` | Compatible updates | Ranges | Prereleases |
| --- | --- | --- | --- | --- |
| npm / Yarn / pnpm (node-semver) | exact | `^1.2.3` → `<2.0.0` (the default `npm install` writes); `~1.2.3` → `<1.3.0` | `1.2.x`, `*`, `>=1 <2`, `1.2 - 1.4`, `\|\|` | only when a comparator names a prerelease of the same `x.y.z` |
| Helm (Masterminds/semver) | exact | `^` and `~` as npm, same 0.x rule | as npm, comma also means AND | excluded unless the constraint has one |
| Cargo | `^1.2.3` | `^`, `~` as npm | comparators, comma AND, `*` | only when named |
| Composer | exact | `^` as npm, **`~1.2` → `<2.0`** | as npm | stability flags |
| Poetry | exact | `^`, `~` as npm | PEP 440 too | opt-in |
| pip / uv (PEP 440) | not allowed, `==` | `~=1.2.3` → `>=1.2.3, ==1.2.*` | `>=`, `<`, `!=`, `==1.2.*`, comma AND | excluded unless named or `--pre` |
| Maven | soft hint, mediation may override | — | `[1.0,2.0)`; `[1.2.3]` is a hard pin | ordinary ordering |
| Gradle | required, may be raised | `1.+`, rich versions | Maven intervals | ordinary ordering |
| NuGet | **minimum**, restore picks the **lowest** fit | `6.*` floats to the highest | Maven intervals | `-*` floating or explicit |
| APT | relation only, `pkg (>= 1.2)` | — | `<<`, `<=`, `=`, `>=`, `>>` | `~` sorts first (`1.0~rc1 < 1.0`) |

The majority grammar is node-semver: bare is exact, `^` is "compatible", `~` is "patch level",
x-ranges and comparators for everything else, and prereleases stay out unless asked for. Helm, Cargo,
Composer and Poetry are dialects of it. The outliers are the JVM/NuGet interval family, where a bare
version is a minimum, and PEP 440.

## 3. Decision

One grammar for every `version` field in every dotquay manifest (project and package, `[addons.*]`
and `[dotnet.*]`): **node-semver**, with two dotquay extensions.

It is a superset of what dotquay accepts today. `"1.5.0"`, `"~1"`, `"~1.4"`, `"~2.44"` and SPEC's
own `"13.0.*"` example all keep their meaning, with one exception in §3.3.

NuGet's own range syntax never reaches the user. dotquay resolves the range itself and writes an
exact `Version="…"` into the `.csproj`, as it already does, so NuGet's "bare means minimum, lowest
wins" rule has nowhere to leak through.

### 3.1 Grammar

```
range-set  := range ( '||' range )*
range      := hyphen | comparator ( (' ' | ',') comparator )* | ''
hyphen     := partial ' - ' partial
comparator := ( '<' | '<=' | '>' | '>=' | '=' )? partial
            | '~' partial
            | '^' partial
partial    := 'v'? xr ( '.' xr ( '.' xr ( '.' xr )? )? )? ( '-' prerelease )?
xr         := 'x' | 'X' | '*' | digits
```

| Written | Means |
| --- | --- |
| `1.2.3` / `=1.2.3` | exactly 1.2.3 |
| `1.2` / `1.2.x` / `1.2.*` | `>=1.2.0 <1.3.0` |
| `1` / `1.x` | `>=1.0.0 <2.0.0` |
| `*`, `x`, empty | any stable version |
| `~1.2.3` | `>=1.2.3 <1.3.0` |
| `~1.2` | `>=1.2.0 <1.3.0` |
| `~1` | `>=1.0.0 <2.0.0` |
| `^1.2.3` | `>=1.2.3 <2.0.0` |
| `^0.2.3` | `>=0.2.3 <0.3.0` |
| `^0.0.3` | `>=0.0.3 <0.0.4` |
| `^1.2` / `^1` | `>=1.2.0 <2.0.0` / `>=1.0.0 <2.0.0` |
| `1.2 - 1.4.5` | `>=1.2.0 <=1.4.5` |
| `>=1.2 <2`, `>=1.2, <2` | both bounds |
| `^1.0 \|\| ^2.0` | either |

### 3.2 dotquay extensions

1. **A fourth component.** Godot addons (Phantom Camera `v0.11.0.3`) and NuGet (`Revision`) both use
   one. It is compared like the others and treated as finer than patch: `~` and `^` never bump it, and
   `1.2.3` as an exact pin matches `1.2.3` and `1.2.3.0` only.
2. **A comma is a synonym for a space** inside one range, because Cargo, Helm and PEP 440 users write
   `">=1.2, <2"` and nothing reads it differently.

A leading `v` stays accepted everywhere, and tags keep the lenient parse of SPEC.md §15 (2–4
components, `-stable` counts as the release).

### 3.3 The one change in meaning

A bare *partial* version used to be exact: `version = "2.44"` matched the tag `2.44`/`2.44.0` and
nothing else. Under node-semver it is the x-range `2.44.x`. A project that pinned a two-component tag
this way now gets `2.44.1` if one is published. `dotquay add` never wrote a bare partial (it writes
`~I.J`), so this only reaches hand-written manifests, and `update` reports the move like any other.
An exact pin is written with all three components, `2.44.0`, or with `=`.

### 3.4 Prereleases

npm's rule, unchanged: a prerelease version satisfies a range only if it is inside the bounds **and**
some comparator of the same range names a prerelease of the same `major.minor.patch`.

- `^5.0` never selects `5.1.0-rc1`.
- `^5.1.0-rc1` accepts `5.1.0-rc5`, then `5.1.0`, then any `5.x` release, but never `5.2.0-rc1`.
- `5.1.0-rc1` pins that prerelease.

Prerelease ordering is SemVer 2.0's: dot-separated identifiers, numeric ones compared as numbers
(`rc.2 < rc.10`), alphanumeric ones ordinally, and a shorter list first when all else is equal.

### 3.5 What `update` does

For every name, git or NuGet: the highest version that satisfies **every** constraint on it.

- A NuGet package declared by the project uses the project's range. A project declaration wins over
  any addon's, as it already does for asset metadata (SPEC.md §8).
- A NuGet package declared only by addons uses the intersection of all their ranges. No version in
  the intersection is a hard error naming each addon and its range, the same shape as
  `GraphConflictKind.NoCommonVersion` for git addons.
- A range with no matching version is a hard error naming the range and the newest version that
  exists, so the fix is visible from the message.

`install` is unchanged: it replays the lockfile and never resolves.

### 3.6 What `add` writes

- `dotquay add <git-url>` keeps writing `~I.J`. Godot addons follow semver loosely, and a minor
  release that breaks things is common enough that tilde is the safer default.
- `dotquay add <package-id>` writes `^<resolved>`, as `npm install` does. NuGet packages follow semver
  much more reliably, and an exact pin here would reproduce the old failure the other way round: a
  project that never picks up a fix.
- `--version` takes any range in the grammar. It is resolved against the feed and written to the
  manifest exactly as typed.

## 4. A failed restore leaves the project as it was

`SyncDotnetAsync` writes each `PackageReference` and `NuGet.Config` before the single restore at the
end, and `update` writes the lockfile only after the restore succeeds. On a failed restore the
workflow now puts the `.csproj` and `NuGet.Config` back to what they held before the sync, then
raises the restore's error. The lockfile was never written. The project is back where it was, and the
error still carries NuGet's own message (e.g. NU1605 naming the package that needs a newer
dependency).

dotquay does not read nuspec dependencies to avoid combinations like adapter 3.1.1 + api 5.0.0 in
the first place. That is a full solver duplicating NuGet's, and the restore already knows. What
changes is that a combination NuGet refuses no longer damages the project.

## 5. Where it lives

- `Versioning/AddonVersion` becomes the one version type for tags and NuGet versions, with SemVer 2.0
  prerelease ordering. (Name kept, to keep this change focused; it now describes NuGet versions too.)
- `Versioning/VersionConstraint` is rewritten to the grammar above, keeping `TryParse` and
  `IsSatisfiedBy`, so `AddonResolver`, `AddonUnifier` and `TagSelector` do not change.
- `Dotnet/NuGetVersionSelector.SelectHighestMatching` picks from a feed's version list.
- `DependencyGraphResolver` records every addon's `[dotnet.*]` range per package
  (`DotnetRequirementsByName`) instead of discarding all but the first. The existing first-wins
  `AddonDeclaredDotnet` list stays for asset metadata and feed pins, where "first wins with a warning"
  is still the rule.
- `DependencyWorkflow.SyncDotnetAsync` resolves each package against the project's range or the
  addons' intersection, and snapshots the `.csproj`/`NuGet.Config` for §4.

## 6. Wire contract

No `--json` field changes. `version` strings in `graph`/`status` output are the manifest's text, as
before. The editor plugin's add form passes `--version` through unchanged, so it accepts ranges as
soon as the CLI does.

## 7. Phasing

| Phase | Scope |
| --- | --- |
| 1 | Grammar and prerelease rules in `VersionConstraint`/`AddonVersion`; git addons use it |
| 2 | `[dotnet.*]` ranges honoured by `update` and `add <package-id> --version`; `add <package-id>` writes `^` |
| 3 | Addon-declared NuGet ranges intersected; conflicts reported |
| 4 | `.csproj`/`NuGet.Config` restored after a failed restore |

## 8. Verification

- Unit tests for every row of the §3.1 table, the prerelease rules of §3.4, and the 4-component and
  comma extensions.
- Workflow tests: an exact pin survives `update` while a newer stable exists; a prerelease pin is
  kept; `^` moves within its major; two addons' ranges intersect; disjoint ranges fail naming both;
  a failed restore leaves the `.csproj` byte-identical.
- A real NativeAOT publish, run through `dotquay update` against BotV2's manifest, which reproduced
  the original failure.

### 8.1 Results (2026-09-24)

- Suite: 1352 passed, 0 failed. The two BotV2 reproduction tests
  (`UpdateAsync_DotnetExactVersionInManifest_KeepsThatVersion`,
  `UpdateAsync_DotnetPrereleaseVersionInManifest_KeepsThatVersion`) failed before the change with
  `3.1.1` and `5.0.0`. Both rollback tests fail with the restore disabled and pass with it.
- NativeAOT publish (`vcvars64` recipe), run against nuget.org in a scratch project carrying BotV2's
  pins:
  1. `18.0.0` / `5.1.0-rc1` / `3.0.0`: `update` keeps all three, and a second `update` reports them
     unchanged.
  2. `gdUnit4.api = "5.0.0"`, `gdUnit4.test.adapter = "^3.0"`: `update` selects adapter 3.1.1, NuGet
     refuses with the original NU1605, and the `.csproj` and lockfile are byte-identical afterwards
     (sha256).
  3. `^18.0` / `^5.1.0-rc1` / `^3.0`: `update` moves to 18.10.1, 5.1.0-rc5 and 3.1.1 and restores.
  4. `add Newtonsoft.Json` writes `version = "^13.0.4"`.
  5. `version = "[3.0,4.0)"` is refused with the grammar in the message.
  6. `add https://github.com/bitwes/Gut --version "^9.3"` resolves `v9.7.1` and writes the range as
     typed.
