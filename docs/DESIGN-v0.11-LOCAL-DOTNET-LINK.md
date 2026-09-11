# ADR / Design — v0.11: Local .NET project link

**Status:** **Implemented and verified against a real NativeAOT binary and a real source generator**,
2026-09-03. 724 C# tests green. See *As built* for where it departed from this design.
**Date:** 2026-09-03
**Deciders:** Kani
**Amended:** 2026-09-03 — the `.csproj` is no longer mutated; the swap became a gitignored MSBuild
overlay (see *Decision* and *How the overlay works*). Generator/analyzer support added as a
first-class case.

Motivating case: developing a shared C# library (say `Kani.Core`, or a package like `VBeami.Core`
already referenced in `dotquay.toml`) that a Godot project also consumes via `[dotnet.<id>]`, without
a commit → push → `dotquay update` loop per edit. `dotquay link` (SPEC.md §7.4) already exists to kill
exactly this loop for GDScript addons; the .NET side has no equivalent, and a user hit this gap
directly (this doc follows that conversation).

## Context

`dotquay link <name> <path>` (v0.6) replaces `addons/<folder>` with a directory link into a local
checkout. It works because a GDScript addon's identity *is* a folder of files, and Godot reads
whatever is at that path — a junction is invisible to it. `[dotnet.*]` dependencies are a different
shape entirely: SPEC.md §11 is explicit that "dotquay does not reimplement NuGet" — it only writes
`<PackageReference>` into the consuming project's `.csproj` (`CsprojPackageReferenceWriter.AddOrUpdate`,
called from `DotnetInstaller.InstallAsync`) and shells out to `dotnet restore`. There is no folder
dotquay owns on the .NET side, so there is nothing for a directory link to swap. A `PackageReference`
resolves to a *built, versioned, immutable* package pulled from a NuGet feed — editing the library's
source and rebuilding it does nothing for the consumer until it's packed, published, and re-restored.

I confirmed (reading `docs/SPEC.md` end to end, plus `DESIGN-v0.10-PACKAGE-ASSETS.md`, the newest
unshipped design) that no local-linking mechanism for `[dotnet.*]` exists or has even been proposed.
v0.10 solves a different problem — asset metadata on a `PackageReference`, not local sourcing — and
after the overlay amendment the two designs touch disjoint code, so neither blocks the other. They do
meet at one point worth keeping in view: for an analyzer or source generator, v0.10 is how consumers
wire up the *published* package and this design is how its author edits it. See *The link is the dev
loop, not the distribution channel*.

The MSBuild-native answer to "reference the actual source, not a built package" is
`<ProjectReference>` instead of `<PackageReference>`. That's the shape this design adopts.

## Decision

Extend `dotquay.local.toml` (already per-machine, gitignored, owned by `Dotquay.Core.Local.LocalLinks`)
to also link `[dotnet.*]` entries, each to a local `.csproj`.

The swap is applied as a **gitignored MSBuild overlay**, not by editing the consuming `.csproj`.
dotquay generates `dotquay.local.targets` beside `dotquay.local.toml`, and the committed `.csproj`
carries one inert conditional `<Import>` that picks it up when present. The `.csproj` keeps its
`<PackageReference>` verbatim; the overlay removes it and substitutes a `<ProjectReference>` for this
machine only.

Unlinking deletes the entry — and with it the overlay — restoring the committed state exactly,
because that state was never modified.

### Why not edit the `.csproj`

The first draft of this design wrote the `<ProjectReference>` into the consuming `.csproj` directly.
That is wrong for the same reason `dotquay.local.toml` is gitignored, and the file's own doc comment
says so:

> committing it would break every teammate and every CI run, and it would make the lockfile's
> reproducibility claim (goal #1) a lie

A `<ProjectReference Include="F:\projects\BlackHaul\...">` in a **tracked** file is that failure with
extra steps: it routes around the gitignore the feature depends on. Best case the user maintains a
permanently dirty working tree and remembers never to stage it; worst case they commit it and every
teammate gets a path that does not exist on their machine. The overlay removes the possibility rather
than documenting the hazard.

## How the overlay works

MSBuild evaluates a project **in order**, and `<PackageReference Remove="…"/>` deletes an item from
the list *as it exists at that point*. Removing something not yet declared is a legal, silent no-op.
So the overlay must be imported **after** the project body.

Committed to git, once, immediately before `</Project>`:

```xml
<Import Project="dotquay.local.targets" Condition="Exists('dotquay.local.targets')" />
```

Generated, gitignored, regenerated wholesale from `dotquay.local.toml` on every link change:

```xml
<Project>
  <ItemGroup>
    <PackageReference Remove="dev.kani.godot.generator" />
    <ProjectReference Include="F:\projects\BlackHaul\dev.kani.godot.generator\dev.kani.godot.generator.csproj"
                      OutputItemType="Analyzer"
                      ReferenceOutputAssembly="false" />
  </ItemGroup>
</Project>
```

Load-bearing details, each of which is silent when wrong:

- **`.targets`, never `.props`.** `Directory.Build.props` is imported *before* the project body, so a
  `Remove` there would run against an empty list, no-op silently, and leave the project quietly
  building against the published package instead of the local source — precisely the bug this feature
  exists to prevent, with no diagnostic. This is the single highest-consequence detail in the design,
  the counterpart to the addon link's `DeleteDirectory` invariant.
- **A dotquay-named file with an explicit `<Import>`, not `Directory.Build.targets`.** MSBuild imports
  only the *first* `Directory.Build.targets` found walking up the tree and then stops, so generating
  one could silently shadow a parent the project already relies on. An own-named file collides with
  nothing and is greppable.
- **The `<Import>` is committed and inert.** `Condition="Exists(…)"` means a teammate or CI clone
  evaluates it to nothing. This one line is the *only* permanent change to a tracked file, added
  idempotently on first link.
- **`dotquay.local.targets` is gitignored**, same as `dotquay.local.toml`, and the generated file
  carries the same self-describing header comment saying so.

Consequences of the overlay shape, all simplifications:

- The `.csproj` is never mutated for linking, so the "did dotquay write this or did the user"
  ownership problem that `DESIGN-v0.10-PACKAGE-ASSETS.md` has to solve for `PackageReference`
  **does not arise here at all**. The overlay is 100% dotquay-owned and regenerated wholesale, like
  `LocalLinks.Write` — not merged into user XML like `CsprojPackageReferenceWriter`.
- Unlinking needs no lockfile write-back. The original design restored a `<PackageReference>` from
  `LockedDotnet.Resolved`; there is nothing to restore, because nothing was removed.
- The v0.10 interaction disappears with it. Under the original design, unlinking a package that
  carried asset metadata would have dropped it; the overlay leaves that metadata untouched in the
  committed file throughout.

## Source generators and analyzers — a first-class case

The motivating link is `dev.kani.godot.generator`, a Roslyn source generator consumed by several
projects. A generator is not referenced like a library, and getting this wrong is silent:

| Attribute | Without it |
|---|---|
| `OutputItemType="Analyzer"` | MSBuild routes the built DLL to the ordinary reference list. The generator is never loaded as an analyzer and simply **never runs** — no error, no warning, just absent output |
| `ReferenceOutputAssembly="false"` | The consumer also compiles against the generator's own types, which it has no business seeing |

So the writer cannot emit a bare `<ProjectReference Include="…"/>`. But the attributes are not a
constant either: linking an ordinary shared library (`Kani.Core`) wants the defaults — you *do*
compile against it. The link entry has to say which:

```toml
[link.dotnet."dev.kani.godot.generator"]
path = "F:/projects/BlackHaul/dev.kani.godot.generator"
analyzer = true    # expands to OutputItemType="Analyzer" ReferenceOutputAssembly="false"

[link.dotnet."Kani.Core"]
path = "F:/projects/Kani.Core"
```

### Why a preset here, when v0.10 rejected one

`DESIGN-v0.10-PACKAGE-ASSETS.md` explicitly refuses a `kind = "analyzer"` shorthand for
`PackageReference`, so a boolean here needs to not be arbitrary. The two cases differ in a way that
decides it:

- On the **package** side the preset has a live counterexample. The conventional analyzer incantation
  is `PrivateAssets=all`, but gdUnit4 ships `PrivateAssets=none` to flow the analyzer onward
  deliberately. A preset would emit the opposite of what the motivating package needs, silently.
- On the **project** side there is no such split. `OutputItemType="Analyzer"` and
  `ReferenceOutputAssembly="false"` always travel together for a generator-as-project-reference;
  there is no established shape wanting one without the other, because "load it as an analyzer" and
  "don't compile against it" are the same intent.

A preset is right exactly when the values it hides are not a real choice. That holds here and does
not hold in v0.10.

### The two consumption modes, and where they collide

The generator's normal path is a published package: consuming projects declare
`[dotnet."dev.kani.godot.generator"]` and the analyzer wires itself up from the package's own
`analyzers/dotnet/cs/` layout, needing no asset metadata at all unless they want *non-default* flow —
which is v0.10's territory, not this design's. The relative `<ProjectReference>` exists in exactly one
place: the project where the generator itself is developed.

dotquay does not interfere with that reference. `CsprojPackageReferenceWriter` reads and writes
`PackageReference` descendants exclusively and never inspects `ProjectReference`, so a hand-written
one survives every dotquay command untouched. That arrangement needs nothing from this design.

**The collision is when the dev project is itself dotquay-managed and also declares the generator in
its `dotquay.toml`.** dotquay then writes a `<PackageReference>` beside the hand-written
`<ProjectReference>`, and the build carries both — the published analyzer and the local one, same
types, running together. Two ways out: keep it out of that project's manifest, or link it, which is
exactly what the overlay's `<PackageReference Remove>` is for. The second is why this design matters
for a generator and not only for a shared library.

## Options Considered

### Option A: Extend local links + PackageReference↔ProjectReference swap (recommended)

| Dimension | Assessment |
|---|---|
| Complexity | Medium — one new writer class, one branch in an existing installer, one schema extension to an existing per-machine file, plus a one-line idempotent `<Import>` hook in the consuming `.csproj` |
| Consistency | High — reuses `LocalLinks`, `CsprojLocator`, the "lockfile untouched while linked" invariant, and the `Linked` status kind, all already proven for addons |
| Risk | Low — no directory-link/`DeleteDirectory` hazard exists on this side at all (see Consequences); XML writing is already AOT-proven via `CsprojPackageReferenceWriter`. The reproducibility hazard the first draft carried is designed out by the overlay rather than mitigated |
| Dev-loop fidelity | High — `dotnet build` picks up source changes in the referenced project immediately, no pack/publish step |

**Pros:** Mirrors a workflow the user already knows (`dotquay link`); the "live edit" property is real,
not simulated; small blast radius (one file family, one installer method).
**Cons:** `dotquay.local.toml`'s parser currently assumes flat, non-dotted keys — package IDs like
`Newtonsoft.Json` or `VBeami.Core` need the same dotted-table handling `DotnetSpec`/`ManifestReader`
already had to solve for `[dotnet.*]` in the project manifest.

### Option B: Local file-based NuGet feed

Add a folder-based NuGet source (`<add key="local" value="C:\path\to\packages" />`) and have the user
`dotnet pack` the library into it.

| Dimension | Assessment |
|---|---|
| Complexity | Low for dotquay, high for the user |
| Dev-loop fidelity | Low — every edit needs a re-pack, and NuGet's local-feed cache can serve a stale version by package-version collision unless the user bumps a version or clears cache each time |
| Consistency | Low — introduces a second, NuGet-specific notion of "local" alongside the addon one, rather than one shared concept |

**Pros:** Zero new C# code in dotquay; it's just config.
**Cons:** Reintroduces exactly the commit/publish-style loop `dotquay link` exists to eliminate, just
shortened from git push to `dotnet pack`. Rejected — doesn't solve the actual problem.

### Option C: Separate `link-dotnet` command

A dedicated verb instead of teaching `dotquay link` to handle both addon and dotnet identities.

| Dimension | Assessment |
|---|---|
| Complexity | Similar to Option A |
| Consistency | Low — two verbs for one concept ("point this dependency at my local checkout"), two things to remember, and `dotquay.local.toml` would need either two sections anyway or two files |

**Pros:** Sidesteps needing to look up whether `<name>` is an addon or a dotnet package.
**Cons:** That lookup (checking `ProjectManifest` for which section declares `<name>`) is a few lines
and already how `add`/`remove`/`update` distinguish the two. Splitting the command is more surface
area for no real gain. Rejected.

### Option D: MSBuild-level source inclusion (`<Compile Include>` / solution merge)

Pull the referenced project's source files or its `.sln` directly into the consumer's build.

| Dimension | Assessment |
|---|---|
| Complexity | High |
| Consistency | Low — crosses a boundary SPEC.md §11 draws deliberately ("dotquay does not reimplement NuGet," "Resolution, caching, and lockfiles on the NuGet side stay NuGet's job") |

**Pros:** None specific to this problem; `<ProjectReference>` already gives live source builds without
this.
**Cons:** Solves nothing `<ProjectReference>` doesn't already solve, at much higher risk (touches the
consumer's own build graph beyond one dependency). Rejected.

## Trade-off Analysis

The addon-link design leans on a filesystem trick (a junction) because Godot's unit of "a dependency"
*is* a folder. The .NET equivalent of that trick is not a filesystem trick at all — it's telling
MSBuild to resolve this dependency's *build output* from a project instead of a package, which is
exactly what `<ProjectReference>` is for. Option A reuses `CsprojLocator`, `LocalLinks`, and the
"lockfile untouched while linked" invariant, and is strictly less code and less risk than either
simulating a package (Option B, wrong shape, poor fidelity) or reaching further into MSBuild than
dotquay currently goes (Option D, violates an explicit non-goal in SPEC.md §11). Option C's separate
command buys nothing Option A's manifest lookup doesn't already give for free.

Note what the overlay decision changed about Option A's shape: the new writer is **not** a sibling of
`CsprojPackageReferenceWriter` after all. That class exists to merge carefully into a file the user
also owns and edits; the overlay is a file only dotquay ever writes, so it is regenerated wholesale
and needs none of that machinery. The delicate writer stayed where it was, which is the better
outcome — it means `DESIGN-v0.10-PACKAGE-ASSETS.md` and this design touch disjoint code, and neither
blocks the other.

## Consequences

- **Easier:** iterating on a shared C# library consumed by a Godot project, with the same one-command
  ergonomics `dotquay link` already gives GDScript addons.
- **Harder, but bounded:** `LocalLinks` (`Dotquay.Core.Local.LocalLinks`) needs dotted-key TOML parsing
  it doesn't have today. This should reuse whatever recursive-table-walk `ManifestReader` already
  implements for `[dotnet.<PackageId>]` (§7.2's own comment flags this as non-obvious: "reading these
  back out of TOML's dotted-table syntax needs a recursive walk") rather than reimplementing it —
  duplicating that logic is exactly the kind of drift this codebase's own conventions warn against.
- **Namespace collision, solved structurally:** `dotquay.local.toml` today has one flat `[link.<name>]`
  section. An addon and a dotnet package could theoretically share a name. This design splits it into
  `[link.addon.<name>]` and `[link.dotnet.<id>]` — mirroring the existing `[addons.*]` / `[dotnet.*]`
  split in `dotquay.toml` itself. This is a breaking change to `dotquay.local.toml`'s schema, but that
  file is per-machine, gitignored, and trivially regenerated by re-running `dotquay link` — there is no
  migration cost across teammates or CI, because it never leaves one machine.
- **New wire-contract surface:** a linked dotnet entry needs its own status (`Linked`, mirroring
  `AddonStatusKind.Linked`) and a `doctor` check for a link whose target `.csproj` has vanished. Per
  this project's own documented pitfall, any new enum member here is a JSON contract change the Godot
  plugin (`core/dotquay_wire.gd`) must be updated to recognize, or it silently drops/mishandles it —
  this is a required companion change, not a follow-up.
- **Explicit non-goal:** dotquay does not validate that the linked project's target framework or
  transitive package versions are compatible with the consumer. `dotnet build`/`restore` will surface
  that the normal way (a build error); teaching dotquay to pre-check it is out of scope, matching
  SPEC.md §11's existing boundary of leaving resolution to NuGet/MSBuild.
- **No data-loss hazard, but a different one in its place.** The addon link's highest-consequence
  invariant — `IFileSystem.DeleteDirectory` must not follow a directory link, or dotquay could destroy
  a developer's working copy — has no equivalent here. dotquay never deletes anything belonging to the
  linked C# project, and there is no directory-ownership transfer to get wrong.

  The hazard on this side is *reproducibility leakage*, not data loss: a machine-local absolute path
  reaching a tracked file. The overlay (see *Decision*) is what answers it — with the `.csproj`
  untouched, there is no path by which a link can be committed. The residual sharp edge is the
  `.props`/`.targets` ordering: an overlay imported too early no-ops in silence and leaves the build
  quietly using the published package. That is this design's counterpart to the addon link's
  `DeleteDirectory` rule, and it needs a test that would fail if the import moved.

## As built

Four things this design left open, settled during implementation.

**A linked package is never resolved remotely.** Not stated here, and load-bearing: the motivating case
is a library that *isn't published yet*, so resolving it would refuse exactly the situation the
feature exists for. `SyncDotnetAsync` now keeps a linked package's previously locked version, or on a
first link the version the manifest declares. The lockfile therefore keeps describing what an
unlinked machine would get, which is what makes unlinking a no-op rather than a repair.

**The dotted-key walk was written separately, not shared.** Action item 1 said to reuse
`ManifestReader`'s recursive walk. In the event the two differ in what makes a leaf (`version` vs.
`path`) and in what an unrecognised key means (a hard error vs. a nested name), so a shared helper
would have taken both as parameters and been longer than either copy. `LocalLinks.Leaves` is fifteen
lines and says why in its own remarks. This only matters for a hand-edit, since dotquay's writer
always quotes the key.

**Unlinking leaves the `<Import>` behind.** It is inert without the overlay, and removing it would
churn a committed file on every link/unlink cycle — visible to teammates, for a reason that exists on
one machine only. The line carries a comment saying it is safe to commit.

**`--no-install` writes the link file only, overlay included.** Consistent with how the flag already
behaves for addon links, and the command already tells you to run `install` to apply it.

**Amended 2026-09-11 — adopted packages are linkable too.** `link` classified a name as a package
only if the consumer's own `dotquay.toml` had a `[dotnet.<id>]` entry. A package *adopted* from an
addon's package manifest (vbeami's `[dotnet.VBeami.Core]`, pulled into BotV2 by `update`) lands in the
consumer's lockfile and `.csproj` but never in its manifest — so the author of exactly the library
this design was written for could not link it, and the name fell through to an addon link that tried
to junction `addons/VBeami.Core`. The decision now lives in `Dotquay.Core.Local.LinkTargetResolver`
and consults the manifest first, then the lockfile's `[[dotnet]]` entries (the record of what the
project has, whatever route it arrived by); the manifest's spelling wins when both name it. The
install side never needed the manifest — the overlay removes a `PackageReference` by ID — so nothing
else changed. `LinkTargetResolverTests` pins it against the in-memory file system.

### Verified end to end (published NativeAOT binary, real Roslyn generator)

A consuming project declaring `[dotnet."Dev.Kani.Gen"]` — a package that exists on no feed anywhere —
with a one-line `Program.cs` that references a symbol only the generator produces. The build is the
assertion: it cannot succeed unless every part of the chain is right.

- **Before linking:** `NU1101: Unable to find package Dev.Kani.Gen`. The fixture has teeth.
- **`dotquay link Dev.Kani.Gen <folder> --analyzer`** resolved the folder to its `.csproj`, wrote
  `[link.dotnet."Dev.Kani.Gen"]`, generated the overlay with both analyzer attributes, and added the
  hook *after* the `ItemGroup`.
- **`dotnet build` succeeded and the program printed the generated constant.** That single result
  proves three separate things: `restore` honours the overlay's `PackageReference Remove` (or NU1101
  again), the import sits late enough for that `Remove` to bind (a `.props`-position import would have
  no-opped silently), and `OutputItemType="Analyzer"` took effect (or the symbol would not exist).
- **`doctor --json`** was healthy while linked, and reported `local_overlay_not_imported` once the
  hook was deleted by hand — the silent-failure case, caught.
- **`dotquay link --remove`** deleted the overlay and the link file, left the committed `.csproj`
  byte-identical, and `restore` went back to failing with NU1101 — which is the correct answer, and
  the demonstration that unlinking restores exactly what the lockfile always said.
- The generated `dotquay.local.toml` was checked as **raw bytes**, not through a console: UTF-8, no
  BOM, `E2 80 94` for the em-dash. `Get-Content` renders it as mojibake, which is a display artifact
  and not what is on disk — worth knowing before "fixing" it.

## Action Items

1. [ ] Extend `dotquay.local.toml` schema to `[link.addon.<name>]` / `[link.dotnet.<id>]`; update
   `LocalLinks` (parsing + `Set`/`Remove`/`Read`) accordingly, reusing `ManifestReader`'s existing
   dotted-table-walk logic rather than reimplementing it.
2. [ ] Validate a dotnet link's path via `CsprojLocator.Locate` against the *linked* directory (same
   "exactly one `.csproj`, or error" rule already used for the consumer's own project) — accept either
   a folder or a direct `.csproj` path.
3. [ ] Add `analyzer` (bool, default false) to `[link.dotnet.*]`, expanding to
   `OutputItemType="Analyzer"` + `ReferenceOutputAssembly="false"`.
4. [ ] Add `LocalTargetsWriter`: generate `dotquay.local.targets` **wholesale** from the current links,
   deleting the file when no dotnet links remain. Model it on `LocalLinks.Write` (dotquay owns the
   whole file) — explicitly *not* on `CsprojPackageReferenceWriter`, which merges into a file the user
   also edits. No ownership tracking is needed here because nothing else may write this file.
5. [ ] Ensure the `<Import Project="dotquay.local.targets" Condition="Exists(…)" />` hook exists in the
   consuming `.csproj`, idempotently, immediately before `</Project>`. This is the only mutation to a
   tracked file in the whole design; adding it twice, or adding it above the `PackageReference`
   `ItemGroup`, are both bugs.
6. [ ] Branch `DotnetInstaller.InstallAsync`: when the package name has a local link, skip
   `NuGetConfigWriter`/credential-environment setup for that entry; still call `dotnet restore`
   afterward so the referenced project's own transitive dependencies resolve.
7. [ ] Unlink path: drop the entry and regenerate (or delete) the overlay, then restore. No lockfile
   write-back and no `.csproj` edit — the committed `<PackageReference>` was never removed.
8. [ ] `status`/`list`: report `Linked` for a locally-linked dotnet entry (mirror `AddonStatusKind`).
9. [ ] `doctor`: new issue kinds for (a) a linked dotnet entry whose target `.csproj` no longer exists,
   and (b) an overlay/hook mismatch — `dotquay.local.targets` present with no `<Import>` in the
   `.csproj`, which is a link that silently does nothing. **No plugin change is needed for these**:
   v0.10 established that `_report_doctor` renders each issue's `message` and never switches on
   `kind`, so a new `DoctorIssueKind` renders as-is. The lockstep rule applies to kinds the plugin
   maps, such as the `AddonStatusKind` badges — which the new `Linked` status in item 8 *does* need.
10. [ ] Gitignore: the generated overlay carries the same self-describing header comment
    `dotquay.local.toml` already uses, naming both files as per-machine and not for committing.
11. [ ] Tests against the existing in-memory fakes (`tests/Dotquay.Tests/Support/`) for the new writer,
    the installer branch, and the schema change — no real disk/git/NuGet, matching this repo's
    convention. **Include a test that pins the `<Import>` below the `PackageReference` `ItemGroup`**,
    so the `.props`/`.targets` ordering trap fails loudly if anyone moves it.
12. [ ] Verify `dotnet restore` — not just `dotnet build` — honours the overlay's
    `<PackageReference Remove>`. Restore evaluates the same project, so it should; this is exactly the
    class of assumption this project's history says to confirm against the real binary rather than
    reason about.
13. [ ] Real NativeAOT publish + run `link`, `install`, unlink, and `--json` against an actual local
    `.csproj`, per this project's standing verification discipline (a green `dotnet test` is not
    evidence the published binary's XML writing and restore invocation work end to end — though the
    overlay reuses an already-AOT-proven `System.Xml.Linq` code shape, so this is lower risk than a
    genuinely new dependency). Verify with a *real* generator: build the consumer and confirm the
    generator's output actually appears, since a mis-wired analyzer fails by producing nothing.
14. [ ] Out of scope for v1, explicit follow-up: Godot editor plugin surfacing of dotnet links (badge in
    the dependency graph view, matching how `linked`/`patched`/`conflicted` addons are already shown).
