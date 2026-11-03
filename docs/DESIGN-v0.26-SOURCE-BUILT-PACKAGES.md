# ADR / Design — v0.26: Packages the project already builds from source

**Status:** **Accepted 2026-11-03, implementation in progress.** Written 2026-11-02 from a read of the code and of
vbeami's history; the three open questions were decided on 2026-11-03 (see *Decisions*).
**Deciders:** Kani

A project can declare a NuGet package in `dotquay.toml` and *also* build that same library from source through a
hand-written `<ProjectReference>` in its `.csproj`. That is the normal shape of the repository where a library and the
addons that use it are developed together. dotquay does not notice the `ProjectReference`, so every `install` writes a
`<PackageReference>` next to it, and the published package can then shadow the source build without any error.

This design makes dotquay read the `.csproj` it already edits, treat an unconditional `ProjectReference` to the
package's own project as covering that package, and stop writing the `PackageReference` that contradicts it.

Facts below are read from code unless marked *(inferred)*.

## The case that prompted this: vbeami

- `godot/dotquay.toml` declares `[dotnet.VBeami.Core] version = "0.7.20"`, with the private feed for `VBeami.*`. It has
  done so since `17d62b4` (2026-08-27).
- `godot/VBeami.csproj` builds Core from source: `<ProjectReference Include="..\src\VBeami.Core\VBeami.Core.csproj" />`.
  A comment on the next item group explains why it must not also be a `PackageReference`: a stray one, "previously
  pinned at a stale 0.0.5, out of sync with dotquay.toml's 0.7.20) shadowed that and was removed as dead weight".
- vbeami commit `75578f7` (2026-10-21) is titled "chore(csproj): drop the Core PackageReference dotquay re-added".
- On 2026-11-01 a `dotquay install --force` (run to repair a stale `addons/vrm`) added
  `<PackageReference Include="VBeami.Core" Version="0.7.20" />` again. It was reverted by hand.
- `src/VBeami.Core/VBeami.Core.csproj` declares `PackageId` `VBeami.Core`, `AssemblyName` `VBeami.Core` and
  `Version` **0.7.50**. So the source build is not even the version the lockfile names. With both references present,
  which one compiles is MSBuild's choice, not the project's.

The cost is quiet: an edit to Core, a rebuild, and a game that runs against the published package. vbeami has paid it
at least once already (the 0.0.5 shadow).

## What happens today

- `CsprojPackageReferenceWriter` (`Dotnet/CsprojPackageReferenceWriter.cs`) reads and writes `PackageReference`
  descendants only. It never looks at a `ProjectReference`. `FindReference` matches `Include` case-insensitively.
- `DotnetInstaller.InstallAsync` (`Dotnet/DotnetInstaller.cs`) is the single place that calls `AddOrUpdate`, followed by
  `NuGet.Config` and an optional restore.
- Three paths call it, through `DependencyWorkflow.InstallDotnetOrThrowAsync`:
  - `install` writes **every** locked package, unconditionally (`DependencyWorkflow.cs`, around line 511). This is the
    path that re-added Core in vbeami.
  - `add` and the `dotnet` declare path write the package being added.
  - `update`'s graph sync writes a package only when its version, feed or asset metadata changed (around line 3091).
    So a no-op `update` leaves the file alone, and the duplicate appears again on the next real version bump.
- `DESIGN-v0.11-LOCAL-DOTNET-LINK.md` already names this collision ("the dev project is itself dotquay-managed and also
  declares the generator") and offers two ways out. Neither fits a committed `ProjectReference`:
  - **Keep the package out of that project's manifest.** This holds only until an addon the project installs declares
    the package in its own package manifest; the graph walk then adds it back as an addon-declared dependency.
  - **Link it with `[link.dotnet.*]`.** That lives in `dotquay.local.toml`, which is per-machine and gitignored. vbeami's
    `ProjectReference` is committed and applies to every clone and to CI, which would all still get the package added.

## Decision

dotquay treats the `.csproj` as the statement of intent it already is. A package whose own project is referenced
unconditionally by the project's `.csproj` is **provided from source**, and dotquay does not write a
`PackageReference` for it. Resolution, the lockfile and `NuGet.Config` are unchanged. Only the `.csproj` write is
skipped, and the user is told why.

### A. Recognising a source-provided package

New type `Dotnet/SourceProvidedPackages.cs` (pure, `IFileSystem` only, no MSBuild dependency, same reasoning as the
writer's `System.Xml.Linq` choice):

```csharp
public sealed record SourceProvider(string ReferenceInclude, string ProjectPath, string PackageId, string? Version);

public static class SourceProvidedPackages
{
    /// The unconditional ProjectReference in csprojPath that builds packageId, or null.
    public static SourceProvider? Find(IFileSystem fileSystem, string csprojPath, string packageId);
}
```

A `ProjectReference` counts as the provider of `packageId` only when **all** of these hold:

1. **It is unconditional.** Neither the element nor any ancestor (`ItemGroup`, `Choose`/`When`/`Otherwise`) carries a
   `Condition`. A Debug-only source reference does not cover the Release build, and skipping the package there would
   break it. A conditional reference is reported by `doctor` (C) instead.
2. **Its target can be read.** `Include` is resolved relative to the `.csproj`'s directory, accepting either path
   separator (Godot projects are edited on Windows and built on Linux CI). A missing or unparsable target means
   "unknown", never "match".
3. **Its identity is the package id.** The target's `PackageId`, else its `AssemblyName`, else its file name without
   `.csproj`, matching MSBuild's own defaults. Compared case-insensitively, like `FindReference`. Only literal values
   count: a value containing `$(` cannot be evaluated without MSBuild, so it is "unknown". The fallback to the next
   source applies when a property is *absent*, not when it is unevaluable.
4. **Only the project's own `.csproj` is read**, the one `CsprojLocator` already finds. References that arrive through
   `Directory.Build.props/targets` or other imports are out of scope (see Alternatives).

Every "unknown" falls back to today's behaviour: the `PackageReference` is written. The feature can therefore only
ever remove a write that a readable, unconditional reference already makes redundant.

`Version` is the target's literal `<Version>` if present, for reporting only (B).

### B. What each command does

The check sits in `DotnetInstaller.InstallAsync`, the one choke point, so all three paths above inherit it:

- **Provided from source, no `PackageReference` present:** write nothing. Return the same `LockedDotnet` as today.
- **Provided from source, a `PackageReference` present:** remove it, and say so. dotquay cannot prove it wrote that
  line - the lockfile records that the package is managed, not who typed the reference - but a `PackageReference`
  beside an unconditional `ProjectReference` to the same package is never a valid setup: MSBuild picks one, and the
  project cannot tell which. Removing it is what makes an affected project (vbeami) repair itself on its next
  `install`. *(Decided 2026-11-03.)*
- **Not provided from source:** unchanged.
- **Restore** still runs when it would have. `NuGet.Config` is still written; a source mapping for an unused package
  is harmless.
- **`remove <package>`** keeps its current behaviour (removes the `PackageReference` if present). dotquay never writes,
  moves or deletes a `ProjectReference`. That stays an invariant: it reads them, and nothing else.
- **`update`** reaches the check only on a change. A `ProjectReference` added by hand later is therefore applied by the
  next `install`, or by the next version change; `doctor` reports the duplicate in between.

**Reporting.** One line in the operation's `messages`, which the CLI prints, `--json` carries and the plugin and MCP
already surface verbatim, so no wire change is needed:

```
VBeami.Core: built from source by ..\src\VBeami.Core\VBeami.Core.csproj (0.7.50), so no PackageReference was written.
```

When it removed a duplicate, the line says that too. A version difference between the source project and the
lockfile is **not** reported here: `update` reaches this code only on a change, so a warning here would appear
inconsistently. It belongs to `doctor`, which checks every time (C). *(Decided 2026-11-03.)*

### C. `doctor`: two new kinds

Both apply only to packages the lockfile records, so `doctor` keeps to what dotquay manages.

**`package_also_built_from_source`** (`DoctorIssueKind.PackageAlsoBuiltFromSource`) for a project whose `.csproj`
holds a `PackageReference` and a `ProjectReference` to the same package's project, in either of two forms:

- **Unconditional provider:** "`VBeami.Core` is built from source by `..\src\VBeami.Core\VBeami.Core.csproj` and also
  referenced as a package; the package can shadow the source build. `dotquay install` removes the package reference."
- **Conditional provider:** names the condition, says the package still covers the other configurations, and leaves the
  decision to the user. dotquay changes nothing here.

**`source_build_version_mismatch`** (`DoctorIssueKind.SourceBuildVersionMismatch`) when an unconditional provider
declares a literal `Version` that differs from the version the lockfile resolves: "`VBeami.Core` compiles from
source at 0.7.50 (`..\src\VBeami.Core\VBeami.Core.csproj`), but the lockfile resolves 0.7.20 from the feed. Anyone
installing this project's addons gets 0.7.20, which is not what this project builds and tests against." Compared
against the lockfile rather than the manifest constraint: the locked version always satisfies the constraint, and
it is what a consumer actually restores. A provider without a literal version is not reported. *(Decided
2026-11-03: `doctor` only, not `install`.)*

Both are **wire contract changes**: `core/dotquay_doctor.gd` needs an entry for each kind (title, severity, the fix
line), and `test_dotquay_doctor.gd` a case each, or the plugin drops them silently. Same pattern as
`package_asset_metadata_drift`.

### D. What does not change

- **The lockfile.** It still records the resolved published version, so the entry stays meaningful on a machine or
  branch without the `ProjectReference`, and addon-declared requirements still unify against it. Whether a package is
  source-provided is recomputed from the `.csproj` on every run, never stored. The `.csproj` is the only source of
  truth for it, and storing a copy would let the two drift.
- **Resolution and feeds.** Still resolved, still mapped.
- **The graph view.** Unchanged in this design. A "from source" badge on the NuGet node is a possible Phase 3.

### E. Interaction with `[link.dotnet.*]`

A local link adds an overlay `ProjectReference` and a `PackageReference Remove`. For a package already provided from
source, that overlay would add a *second* `ProjectReference`, possibly to a different checkout. `dotquay link` for such
a package **refuses**, naming the committed reference. This applies to any `ProjectReference` to the package's
project, conditional or not: a conditional one still builds from source in its configuration, and the overlay would
duplicate it there. *(Decided 2026-11-03.)*

## Alternatives considered

- **An explicit manifest field, `[dotnet.X] project = "../src/X/X.csproj"`.** Closest to dotquay's "don't infer layout"
  rule, and it would also cover references that come in through imports. Rejected as the primary mechanism: it repeats
  what the `.csproj` already says, fixes nothing until someone adds it, and gives two sources of truth that can
  disagree. It is still a clean extension if detection proves too narrow.
- **Leave the package out of the project manifest.** The v0.11 workaround. It breaks as soon as an installed addon
  declares the package.
- **Commit a `[link.dotnet.*]`.** Links are machine-local by design (v0.11: absolute paths, per-machine overlay).
  Making one committed would mean redesigning links.
- **Evaluate the project with MSBuild.** It would see imports and conditions exactly. Rejected for the same reason the
  writer avoids it: a large NativeAOT and trimming risk for one element kind.
- **Do nothing and document it.** The documentation already exists (v0.11), and vbeami hit the problem twice anyway.

## Testing and verification

- **Unit tests against `InMemoryFileSystem`**, for `SourceProvidedPackages`:
  - each identity source: `PackageId`, then `AssemblyName`, then file name;
  - an unevaluable `$(...)` value is "unknown";
  - a `Condition` on the element, on its `ItemGroup`, and inside `Choose`;
  - a missing target, an unparsable target, and both path separators;
  - case-insensitive id matching.
- **`DotnetInstaller` tests:** nothing written when the package is provided from source; an existing duplicate
  removed; the `PackageReference` written as today in every "unknown" case; the `messages` line present.
- **Workflow tests:** `install` twice on a project with the `ProjectReference` leaves the `.csproj` byte-identical;
  both `doctor` kinds, including both forms of the first; `link` refusing.
- **GDScript:** a `test_dotquay_doctor.gd` case per new kind, run through `run_tests.ps1`.
- **Real binary, NativeAOT:** on a copy of vbeami, `dotquay install` removes the duplicate once, then leaves
  `godot/VBeami.csproj` byte-identical and prints the source line; `doctor --json` reports the 0.7.50/0.7.20 mismatch;
  `dotquay link` on the package refuses; `dotnet build` of the copy succeeds.

## Phasing

1. **Core (A, B).** Detection, the installer change, the `messages` line. Fixes vbeami on its own; no plugin change.
2. **`doctor` (C)** plus the plugin entries for the two new wire kinds.
3. **The `link` refusal (E).**
4. **Optional, later:** a "from source" marker on the graph view's NuGet node.

## Decisions (2026-11-03, Kani)

1. **An existing duplicate `PackageReference` is removed** by `install` (and by any other path that writes the
   package), and the removal is reported.
2. **A version difference is reported by `doctor` only** (`source_build_version_mismatch`), never as an `install` or
   `update` warning.
3. **`dotquay link` refuses** a package that already has a `ProjectReference` to its project, conditional or not.
