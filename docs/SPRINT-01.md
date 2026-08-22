# Sprint 01 — v0.1 reproducible core

**Start:** 2026-08-21 · **Cadence:** no fixed end, dogfooding pace · **Team:** Kani (solo)

## Sprint goal

Ship the P0 set from `SPEC.md` §13: `add`/`install` on a real git addon and a real NuGet
package produce a lockfile-driven, byte-identical `addons/` folder on a fresh clone.

**Status: full v0.1 P0 set shipped and verified end-to-end (2026-08-21)** — see "Verified
end-to-end" sections below for both the git-addon and NuGet halves.

## Scope

Full v0.1 P0 set (`SPEC.md` §16, phase v0.1). No P1/P2 work this sprint.

## Backlog

Ordered by dependency — each row unblocks the ones below it.

| Priority | Item | Depends on | Status |
|---|---|---|---|
| P0-foundation | Solution scaffold (`Dependi.Core` / `Dependi.Cli` / `Dependi.Tests`), `IFileSystem`/`IGitClient`/`INuGetClient` seams | — | Done |
| P0-foundation | Manifest model + reader/writer (`dependi.toml`, §7.1) | Scaffold | Done |
| P0-foundation | Lockfile model + reader/writer (`dependi.lock`, §7.3) | Scaffold | Done |
| P0-1 | `dependi init` | Manifest writer | Done |
| P0-2 | Git source resolution: `version` (tags), `branch`, `rev` | `IGitClient` | Done |
| P0-8 | Global content-addressed cache (`~/.dependi/cache/<repo-hash>/<short-sha>/`) | `IGitClient` | Done |
| P0-4 | Subtree extraction (`path` into staging) | Cache | Done |
| P0-5 | Replace-not-merge sync engine (atomic swap, staging) | Subtree extraction | Done |
| P0-6 | Dirty detection (`content_hash` compare, `--force`) | Sync engine | Done |
| P0-1 | `dependi add <git-url \| nuget-id>` | Resolver + sync | Done — both source kinds |
| P0-3 | `dependi install` (lockfile → disk, errors on drift) | Sync engine | Done — addons and dotnet packages |
| P0-1 | `dependi update [name]` | Resolver + sync | Done — both source kinds |
| P0-1 | `dependi remove <name>` | Sync engine | Done — both source kinds |
| P0-1 | `dependi list` | Lockfile reader | Done — both source kinds |
| P0-7 | NuGet delegation (`.csproj` writes, `dotnet restore`, V3 metadata reads incl. private feeds) | `INuGetClient` | Done |

`dependi outdated`, `status`, `doctor`, `patch` are P1/§6-listed but not required for v0.1 — CLI
command stubs exist now so the tree is complete, bodies land in Sprint 02 (v0.2/v0.3).

## What shipped this sprint (so far)

- Solution scaffold: `Dependi.Core` (pipeline stages behind interfaces), `Dependi.Cli` (Spectre.Console.Cli
  command layer), `Dependi.Tests` (xUnit, no real disk/network — everything goes through
  `IFileSystem`/`IGitClient`/`INuGetClient`).
- `ProjectManifest`/`AddonSpec`/`DotnetSpec` models, Tomlyn-backed `ManifestReader` (validates the
  "exactly one of version/branch/rev" rule from §7.1) and `ManifestWriter`.
- `DependiLockfile`/`LockedAddon`/`LockedDotnet` models, `LockfileReader`/`LockfileWriter`.
- `dependi init` — scaffolds `dependi.toml`, refuses to overwrite an existing one.
- Full CLI command tree wired in `Program.cs`: every §6 command has a class; unimplemented ones
  print which requirement ID they're tracked under and exit non-zero (`Commands/NotYetImplemented.cs`).
- Unit tests for manifest parsing (valid + malformed), lockfile round-trip, and `init`
  (via an in-memory `IFileSystem` test double).
- `AddonVersion`/`VersionConstraint`/`TagSelector` (`Dependi.Core.Versioning`) — lenient git-tag
  parsing and Cargo-style `~` range matching (§15 tag-habit survey), with prereleases always
  excluded from `version` resolution.
- `ProcessGitClient` (`Dependi.Core.Git`) — real `IGitClient` shelling out to `git` on PATH:
  `ls-remote` for tags/branches/default-branch (`--symref`), shallow `fetch` by SHA with a full-fetch
  fallback, `checkout`.
- `AddonResolver` (`Dependi.Core.Resolve`) — turns an `AddonSpec` into a `ResolvedAddon` (pinned
  commit) for all three source kinds (`rev`/`branch`/`version`).
- `RepoCache`, `SubtreeExtractor`, `ContentHasher`, `DirtyChecker`, `SyncEngine`, `AddonInstaller`
  (`Dependi.Core.Sync`) — the global fetch cache, `path`-subtree extraction into staging
  (binary-safe), deterministic tree hashing, dirty detection, and the atomic staging→`addons/<name>`
  swap. `AddonInstaller` is the one piece `add` and `install` share.
- `DependencyWorkflow` (`Dependi.Core.Workflows`) — orchestrates manifest/lockfile I/O around the
  resolver and installer for `add`/`install`/`remove`/`list`; CLI commands are thin translations of
  parsed args into calls here.
- `dependi add <git-url> [--path <subtree>]`, `install [--force]`, `remove <name>`, `list` all wired
  to real implementations in `Dependi.Cli/Commands`. `add` auto-derives the addon name from the URL
  and picks a default source: the latest release tag's `~major.minor` range if the repo has tags,
  otherwise a hard `rev` pin to the default branch's tip (§6's "no tags found" behavior).
- `--path` on `add`: real-world repos routinely name their addon folder differently from the repo
  itself (see "Verified end-to-end" below) — exposed as a CLI flag rather than requiring a
  hand-edit-and-retry loop.
- `dependi update [name] [--force]` (`DependencyWorkflow.UpdateAsync`) — re-resolves one addon or
  every addon declared in the manifest fresh against its spec and reinstalls, leaving untargeted
  lockfile entries untouched. Also the recovery path for an addon a teammate hand-added to
  `dependi.toml` without running `add`: with no prior lock entry, `update` locks and installs it for
  the first time.
- NuGet delegation (`Dependi.Core.Dotnet`, requirement P0-7): `NuGetProtocolClient` (real
  `INuGetClient` via the official `NuGet.Protocol` library against nuget.org's v3 API),
  `NuGetVersionSelector` (latest-stable / explicit-version-exists queries — prereleases never picked
  by default), `CsprojLocator` (finds the project's single `.csproj`), `CsprojPackageReferenceWriter`
  (adds/updates/removes `<PackageReference>` via `System.Xml.Linq`, not a full MSBuild dependency),
  `ProcessDotnetCliRunner` (shells out to `dotnet restore` — same "let the real tool do the real
  work" philosophy as `ProcessGitClient`), `DotnetInstaller` (composes all of the above into one
  "sync a resolved package" step, shared by `add`/`install`/`update`).
- `dependi add <nuget-id> [--version <exact>]`, and the dotnet halves of `install`/`update`/
  `remove`/`list`, all wired into `DependencyWorkflow` alongside the git-addon paths. Dependi pins
  dotnet dependencies to an exact resolved version (not a floating range) at `add`/`update` time —
  simpler than reimplementing NuGet's range grammar, and `update` re-queries latest each time so the
  "move forward" semantics still work.
- `ProjectManifest` and `DependiLockfile` became `record` types (from plain classes) so
  `DependencyWorkflow` can build updated copies with `with { ... }` instead of re-listing every
  field at each of the now-many manifest/lockfile write sites.

## Definition of done

- [x] Each landed command works against a real git repo and a real NuGet package, not just tests —
      see both "Verified end-to-end" sections below.
- [x] `dependi.lock` always stores a full commit SHA, never a short one or a branch name
      (enforced by `LockfileReader`, confirmed against a real resolved SHA below).
- [x] A fresh `install` reproduces `addons/` byte-for-byte (content-hash verified) — confirmed via a
      clean re-`install` producing an identical `content_hash` with no diff.
- [x] No command writes to `addons/` except through the sync engine (`AddonInstaller` → `SyncEngine`
      is the only writer; `remove` deletes via `IFileSystem.DeleteDirectory`, not a raw path). The
      dotnet side has the equivalent property: only `DotnetInstaller`/`CsprojPackageReferenceWriter`
      touch the `.csproj`.
- [x] Every new `Dependi.Core` type has unit tests that don't touch the real filesystem, network, or
      `dotnet`/`git` process (156 tests, `dotnet test` green as of 2026-08-21).
- [ ] `docs/SPEC.md` requirement IDs are cited in code/tests where they're closed, so traceability
      survives without a tracker. (Partial — most Sync/Workflows/Dotnet types cite requirement IDs in
      XML doc comments; a full pass hasn't been done.)

## Verified so far (2026-08-21, via Windows MCP + .NET 10 SDK)

- `dotnet build` — clean (one expected `IL3050` warning: Spectre.Console.Cli's reflection-based
  `CommandApp` isn't NativeAOT-clean yet, matching the risk noted below).
- `dotnet test` — 29/29 passing.
- `dependi init <dir> --godot 4.4` — writes a valid, correctly UTF-8-encoded `dependi.toml`.
- Re-running `dependi init` on the same directory refuses to overwrite, as designed.
- `dependi add <url>` (and the other unimplemented commands) print the tracked requirement ID and
  exit non-zero, as designed.

### AOT verdict: Spectre.Console.Cli fails, swapped to System.CommandLine (2026-08-21)

The user ran `dotnet publish src\Dependi.Cli -c Release -r win-x64 -p:PublishAot=true` from a real
terminal (this sandbox's own attempts were blocked by a toolchain-detection quirk unrelated to the
code — see git history of this file). Result: **build succeeded with 25 warnings**, all
`Spectre.Console.Cli` internals (`ReflectionActivator`, `CommandModelBuilder`, `TypeDescriptor.GetConverter`,
etc.) — but running the published binary crashed immediately on every invocation, including
`--help`:

```
Unhandled exception. Spectre.Console.Cli.CommandRuntimeException: Could not get settings type for
command of type 'Dependi.Cli.Commands.InitCommand'.
```

The trimmer stripped reflection metadata Spectre.Console.Cli's command-binding needs, exactly as
the warnings predicted — a build that links cleanly is not proof a NativeAOT app actually runs.
This is the concrete failure SPEC.md §12/OQ-1 anticipated: swapped `Dependi.Cli` from
`Spectre.Console.Cli` to `System.CommandLine` 2.0.11 (the documented fallback). All 10 commands
rewritten as `Command`-returning static factories (`Commands/*.cs`), colored console output now
uses `Console.ForegroundColor` directly (`ConsoleOutput.cs`) instead of Spectre.Console, to avoid
re-introducing an unverified dependency mid-fix. `dotnet build`/`dotnet test` both green afterward
(29/29), and critically: **zero AOT/trim warnings from any System.CommandLine usage** in this build
(the only remaining warnings are Tomlyn's separately-tracked ones).

**Closed the loop**: published (`-r win-x64 -p:PublishAot=true`) and ran the resulting binary
directly — `--help` (full command list, exit 0), `init` (writes a correct manifest, exit 0),
re-running `init` on the same directory (refuses to overwrite, exit 1), and stub commands (report
their tracked requirement, exit 1). No crashes, unlike Spectre. System.CommandLine is confirmed
NativeAOT-safe for this app, not just analyzer-clean.

## Verified end-to-end (2026-08-21, real git repo via Windows MCP)

Ran the built (non-AOT) CLI against `github.com/nathanhoad/godot_dialogue_manager` — a real,
actively-tagged Godot addon repo, network access confirmed working earlier this sprint:

- `dependi init` in a scratch directory — writes a valid manifest.
- `dependi add https://github.com/nathanhoad/godot_dialogue_manager` — **first attempt failed as
  designed**: the repo's addon folder is `addons/dialogue_manager`, not `addons/godot_dialogue_manager`
  (the auto-derived default), so `SubtreeExtractor` correctly refused with "Configured path
  ... does not exist" instead of silently installing the wrong thing or the whole repo. This is
  exactly the scenario §7.1/OQ-2 anticipated (dependi never infers layout) — it just hadn't been hit
  against a real mismatched repo yet. Added `--path` to `add` in response (see "What shipped" above)
  rather than leaving hand-editing `dependi.toml` after a failed `add` as the only recovery.
- `dependi add ... --path addons/dialogue_manager` — resolved the latest tag (`v4.0.3`) to a full
  40-character commit SHA, fetched via a real shallow `git fetch --depth 1`, extracted the correct
  subtree (including binary files: `icon.svg.import`, `.uid` files), wrote `dependi.toml` and
  `dependi.lock` with a real `content_hash`, installed to `addons/godot_dialogue_manager/`.
- `dependi list` — shows `godot_dialogue_manager  v4.0.3  ffc0011`.
- `dependi install` on the unmodified tree — re-installs cleanly, same content hash, exit 0.
- Hand-edited a file under `addons/godot_dialogue_manager/`, then `dependi install` — **refused**
  with the P0-6 dirty-detection message and exit 1, leaving the hand-edit in place.
- `dependi install --force` — overwrote the hand-edit as designed, exit 0.
- `dependi remove godot_dialogue_manager` — removed the entry from both `dependi.toml` and
  `dependi.lock` and deleted `addons/godot_dialogue_manager/` from disk, exit 0.

Every exit code and error message matched what the unit tests already asserted against fakes —
the fakes' contract held up against a real git remote with no surprises beyond the path-mismatch
case above, which was itself informative rather than a bug.

## Verified end-to-end (2026-08-21, real NuGet package via Windows MCP)

Ran the built (non-AOT) CLI against a scratch `Microsoft.NET.Sdk` project and the real
`Newtonsoft.Json` package on nuget.org (no fakes, no `NuGet.Protocol` mocking):

- `dependi init`, then a hand-written minimal `Game.csproj`.
- `dependi add Newtonsoft.Json` — queried nuget.org for real, picked the actual current latest
  stable release (`13.0.4`), wrote it into `dependi.toml` (`[dotnet.Newtonsoft.Json]`), wrote a
  `<PackageReference Include="Newtonsoft.Json" Version="13.0.4" />` into `Game.csproj`, ran a real
  `dotnet restore`, exit 0. Confirmed `obj/project.assets.json` was actually produced.
- `dependi list` — shows `Newtonsoft.Json  13.0.4  (NuGet)`.
- `dependi update` — re-queried nuget.org, found no newer stable release, reported `(unchanged,
  NuGet)` and correctly skipped a redundant `dotnet restore`.
- `dependi install` — replayed the locked version, restored again, exit 0.
- `dependi remove Newtonsoft.Json` — removed the entry from `dependi.toml` and `dependi.lock` and
  the `<PackageReference>` from `Game.csproj`, exit 0.

Same result as the git-addon verification: every exit code and message matched what the fakes
(`FakeNuGetClient`, `FakeDotnetCliRunner`) already asserted — the real nuget.org v3 API and the real
`dotnet` CLI behaved exactly like their test doubles.

### AOT verdict: NuGet.Protocol fails too, swapped to a direct flat-container HTTP client (2026-08-21)

The user published a fresh AOT binary (`dotnet publish -r win-x64 -p:PublishAot=true`) and I ran it
against the full command surface, including the NuGet path this time (the earlier AOT verdict, above,
predates `add`/`install`/`update` existing at all). Git-addon commands (`init`, `add`, `list`,
`update` against a real repo) all worked cleanly. `dependi add Newtonsoft.Json` did not:

```
Unhandled exception: NuGet.Protocol.Core.Types.FatalProtocolException: Unable to get repository
signature information for source https://api.nuget.org/v3-index/repository-signatures/5.0.0/index.json.
 ---> Newtonsoft.Json.JsonSerializationException: Unable to find a constructor to use for type
NuGet.Protocol.RepositoryCertificateInfo. A class should either have a default constructor, one
constructor with arguments or a constructor marked with the JsonConstructor attribute.
```

Root cause: `NuGet.Protocol` always fetches repository signature metadata as part of resolving any
resource from a `SourceRepository`, and that path deserializes `RepositoryCertificateInfo` through
Newtonsoft.Json's reflection-based object binder — exactly the kind of thing NativeAOT trimming
strips, and exactly the failure mode predicted (correctly) as an open risk right after P0-7 landed.
This confirms the risk was real, not hypothetical.

**Fix**: replaced `NuGetProtocolClient` (`NuGet.Protocol`-backed) with `NuGetFlatContainerClient`
(`Dependi.Core/Dotnet/NuGetFlatContainerClient.cs`) — calls nuget.org's V3 Package Content ("flat
container") API directly via `HttpClient`, parsed with source-generated `System.Text.Json`
(`NuGetFlatContainerJsonContext`, zero reflection). Dropped the `NuGet.Protocol` package reference
entirely; kept `NuGet.Versioning` directly (just version parsing/comparison, no JSON, much lower
risk) since `NuGetVersionSelector` needs it. `docs/SPEC.md` §11 updated to record the swap and why,
same as the Spectre.Console.Cli entry.

While building the replacement, a real bug surfaced that no existing test caught: source-generated
`System.Text.Json` matches property names case-sensitively by default, and the feed's lowercase
`"versions"` key silently failed to bind to the `Versions` property — no exception, just an always-
empty list, which `dependi add` then correctly reported as "no stable release" (technically true
given what it saw, but wrong). Every other NuGet-path test in this project goes through
`FakeNuGetClient`, which bypasses real (de)serialization entirely — so nothing existing could have
caught it. Added `tests/Dependi.Tests/Dotnet/NuGetFlatContainerClientTests.cs`, which stubs
`HttpMessageHandler` instead of the whole client, specifically to exercise the real JSON parsing path
hermetically. Fixed with `[JsonPropertyName("versions")]`. 162/162 tests green after the fix,
re-verified functionally against real nuget.org (`add`/`list`/`update`/`install`/`remove` all correct
again) before asking for another AOT publish to close the loop.

**Status**: fixed and functionally re-verified against the real JIT build and real nuget.org. Not
yet re-confirmed against a fresh NativeAOT publish — that's the very next thing to check.

### Private NuGet feed support (2026-08-21)

The user's real usage is almost entirely private NuGet feeds (a self-hosted GitLab package
registry), so this was checked directly against their real feed and a real personal access token
rather than a synthetic example.

`NuGetFlatContainerClient` (hardcoded to nuget.org's fixed `v3-flatcontainer` URL) was generalized
into `NuGetV3Client`: it discovers the actual "Package Base Address" resource from the source's V3
service index (`index.json`) instead of assuming the path, since GitLab exposes the equivalent
resource at a completely different URL (`.../nuget/download`) than nuget.org's. Confirmed the same
response shape (`{"versions": [...]}`) on both. `NuGetV3ClientFactory` (new `INuGetClientFactory`)
resolves the right client per-call based on the manifest's `[project] nuget_source`, reading
`DEPENDI_NUGET_USERNAME`/`DEPENDI_NUGET_TOKEN` for HTTP Basic auth on any non-default source.

**Verified against the real GitLab feed**: `dotnet add VBeami.Core --version 0.0.1` successfully
resolved the version through the real authenticated request — proving discovery + auth on the
read side. It then failed at `dotnet restore`:

```
error NU1101: Unable to find package VBeami.Core. No packages exist with this id in source(s): nuget.org
```

`dotnet restore` is a separate process reading its own `NuGet.Config`, with no knowledge of
dependi's own HTTP client or the source it just successfully queried. Fixed with two additions to
`DotnetInstaller`, both automatic whenever the manifest names a `nuget_source`:

- `NuGetConfigWriter` ensures the project's `NuGet.Config` declares the source under a fixed
  `"dependi"` key (safe to commit — URL only, never credentials).
- The restore child process is given a `NuGetPackageSourceCredentials_dependi` environment variable
  (`Username={u};Password={p}`), NuGet's own documented convention for authenticating a named source
  via environment rather than config file — the credential never touches disk.

See `docs/SPEC.md` §11.1 for the full design.

**Status**: implemented, unit-tested (`NuGetConfigWriterTests`, extended `DotnetInstallerTests`/
`DependencyWorkflowTests`, 183/183 green), and re-verified against the real GitLab feed — the exact
scenario that surfaced the gap (`dependi add VBeami.Core --version 0.0.1`) now restores successfully.
Also exercised `init --nuget-source`, `install` (fresh restore, no `obj/`/`bin/` cache — proves the
`NuGet.Config` bridge works from a clean clone, not just right after `add`), `list`, and `remove`.
Confirmed `NuGet.Config`, `dependi.toml`, and `dependi.lock` contain only the source URL, never the
token. **Fully closed 2026-08-22** — re-verified against a real NativeAOT publish too (see "AOT
verdict" note below): `NuGetV3Client`'s HTTP+JSON path survives trimming for the private-feed case,
not just nuget.org.

### AOT verdict: NuGet.Protocol swap and private-feed support both confirmed under a real NativeAOT publish (2026-08-22)

The user republished (`dotnet publish src\Dependi.Cli -c Release -r win-x64 -p:PublishAot=true`,
`build.log` in repo root — succeeded with only the pre-existing Tomlyn trim warnings, 0 errors). The
resulting `dependi.exe` (9.7 MB, no runtime DLLs alongside it — genuinely native) was run through the
full command surface in fresh scratch directories:

- **Git addons**: `init`, `add https://github.com/bitwes/Gut.git`, `list`, `remove` — all correct.
- **Public NuGet** (`add Newtonsoft.Json` against real nuget.org, `list`, `update`, `remove`) — all
  correct. This exercises `NuGetV3Client`'s service-index discovery + source-generated JSON path,
  confirming the NuGet.Protocol swap (AOT verdict, 2026-08-21) still holds.
- **Private NuGet feed** (`init --nuget-source`, `add VBeami.Core --version 0.0.1` against the real
  GitLab feed with real credentials, `list`, `remove`) — all correct. This is the path that was new
  since the last AOT run and had only been proven under JIT until now: service-index discovery
  against a non-nuget.org host, HTTP Basic auth, `NuGet.Config` registration, and the credential-env
  bridge into `dotnet restore` all worked identically under NativeAOT trimming.

Confirmed no secret (`glpat-...`) appeared anywhere on disk across either scratch directory, and both
were deleted afterward.

**Status**: both open AOT risks below are now resolved. No known unverified NativeAOT path remains
for the v0.1 P0 feature set plus private-feed support.

## v0.2 — safety and visibility (2026-08-22)

P1-3 (`outdated`) and P1-4 (`status`, `doctor`) landed — the full SPEC.md §16 v0.2 scope. See §6.1
for implementation notes (design decisions, what each check does and skips).

**New pieces**: `NuGetVersionSelector.CheckForNewerStableAsync`; `AddonOutdatedInfo`/
`DotnetOutdatedInfo`/`OutdatedResult`, `AddonStatusKind`/`AddonStatusInfo`/`StatusResult`,
`DoctorIssueKind`/`DoctorIssue`/`DoctorResult` (all `Workflows/`); `Doctor/PluginCfgReader.cs`;
`DependencyWorkflow.OutdatedAsync`/`Status`/`DoctorAsync`; `IGitClient.IsAvailableAsync` +
`ProcessGitClient`/`FakeGitClient` implementations; `IFileSystem.EnumerateDirectories` +
`SystemFileSystem`/`InMemoryFileSystem` implementations. `OutdatedCommand`/`StatusCommand`/
`DoctorCommand` swapped from `NotYetImplemented` stubs to real implementations.

**Verified**: 218/218 unit tests green (up from 183), plus a real end-to-end pass against a live
project (real `git` addon — bitwes/Gut — and real nuget.org, JIT build):
- Healthy project: `outdated` reports "up to date", `status` reports `clean`, `doctor` reports "no
  issues".
- Hand-edited addon folder: `status` correctly reports `modified`.
- Hand-edited `plugin.cfg` version (real Gut `plugin.cfg`, confirming it really does parse as TOML
  as assumed): `doctor` correctly reports `plugin.cfg` version drift.
- Manifest entry removed by hand for both an addon and a dotnet package: `doctor` correctly reports
  both as dangling lockfile entries.
- Untracked folder created under `addons/`: `doctor` correctly reports it as orphaned.

**AOT-verified 2026-08-22**: the user republished (`build.log` — succeeded with the same class of
pre-existing Tomlyn `IL2026`/`IL3050` trim warnings, now also from `PluginCfgReader.cs`, 0 errors).
Ran the full `outdated`/`status`/`doctor` surface against the real AOT binary in a fresh scratch
project (real Gut repo, real nuget.org): healthy-project baseline (`outdated` "up to date", `status`
`clean`, `doctor` "no issues"), a hand-edited addon folder (`status` → `modified`), a hand-edited
**real** `plugin.cfg` version field parsed through the trimmed `Tomlyn.TomlSerializer` call in
`PluginCfgReader` (`doctor` → version-drift issue, confirming the one genuinely new AOT-risk code
path in this feature survives trimming), two dangling lockfile entries (addon and dotnet, by hand-
editing `dependi.toml`), and an orphaned `addons/` folder — all four `doctor` issue kinds fired
correctly in one combined run. Scratch directory cleaned up afterward. No known unverified NativeAOT
path remains for v0.1 P0 + private-feed support + v0.2.

## v0.3 — patches (2026-08-22)

P1-1 landed — the full SPEC.md §16 v0.3 scope: `dependi patch <name>` plus 3-way re-apply on
`install`/`update` with conflict detection. See §6.2 for implementation notes (git plumbing
choices, the patch-capture/re-apply pipeline, the `PatchMerger` per-file branching rules).

**New pieces**: `Dependi.Core.Patch` — `IPatchTool`/`ProcessPatchTool` (shells out to `git diff
--no-index`/`git apply`/`git merge-file`), `PatchException`, `PatchMerger` (pure per-file 3-way
merge over three directory trees), `PatchMergeResult`, `PatchReapplier` (reconstructs "ours" from
the pristine old base + patch, orchestrates the merge into the install pipeline's staging tree).
`LockedAddon.PatchBaseSha` (+ `LockfileReader`/`LockfileWriter` support for `patch_base_sha`).
`DependencyWorkflow.PatchAsync` (capture) and `PatchCaptureResult`. `AddonInstaller.InstallAsync`
now takes the previous `LockedAddon?` instead of a bare content hash, so it can see
`PatchHash`/`PatchBaseSha` and trigger re-apply. `PatchCommand` swapped from a `NotYetImplemented`
stub to a real implementation.

**Verified**: 246/246 unit tests green (up from 227) — including a dedicated `PatchMergerTests.cs`
exercising all eight base/ours/theirs presence combinations directly, and new
`AddonInstallerTests`/`DependencyWorkflowTests` cases for clean reapply, conflict, and delete edge
cases, all against fakes (`FakePatchTool`, extended `FakeGitClient` with per-(url, sha) fetch
content).

**Real end-to-end (JIT build, real local git repo, no fakes)**: two scenarios, both against a real
`git init`-created upstream repo tagged `v1.0.0`/`v1.0.1`, driven entirely through the built CLI:

- *Clean merge*: hand-edited `plugin.cfg`, `dependi patch demo_addon` captured it, upstream gained
  an unrelated new file (`new_feature.gd`) in `v1.0.1`, `dependi update demo_addon` cleanly merged
  both — the hand-edit survived, the new upstream file appeared, `resolved` advanced to the new SHA
  while `patch_base_sha` correctly stayed pinned to `v1.0.0`'s SHA, and `dependi status` reported
  `patched as expected` afterward.
- *Conflict*: hand-edited the same line of `plugin.cfg` that upstream also changed in `v1.0.1`.
  `dependi update demo_addon` exited 1 with a message naming the conflicting file; `addons/
  demo_addon/plugin.cfg` was byte-identical to before the attempt; `.dependi/staging/demo_addon/
  plugin.cfg` contained real `<<<<<<<`/`=======`/`>>>>>>>` markers from git's own `merge-file`; the
  lockfile was not rewritten.

This real-repo pass caught two bugs the fakes-only unit tests couldn't have caught, both fixed
before the above was captured (see §6.2 for the technical detail):

1. `git apply --directory=<absolute Windows path>` fails with `invalid path` — fixed by running
   `git apply` with its working directory set to the target instead of using `--directory`.
2. `dependi patch` wasn't updating the lockfile's `content_hash` to the newly-patched tree, so every
   later `install`/`update` of a patched addon refused with the P0-6 dirty-detection message even
   with no upstream change at all — fixed by recomputing `content_hash` as part of capture.

**AOT-verified 2026-08-22**: the user republished (`build.log` — succeeded with 12 warnings, all the
same pre-existing Tomlyn `IL2026`/`IL3050` class as v0.1/v0.2, zero new warnings from
`Dependi.Core.Patch`). Ran the same two real-repo scenarios above again, this time against the real
AOT binary (`dependi.exe`, 9.9 MB) in fresh scratch directories: the clean-merge case (hand-edit
survives, upstream's new file appears, `patch_base_sha` stays pinned, `status` → `patched as
expected`) and the conflict case (exit 1, real `<<<<<<<`/`>>>>>>>` markers in staging,
`addons/demo_addon/` byte-identical to before the attempt) both reproduced exactly as under the JIT
build. `ProcessPatchTool`'s pure `Process`/file-I/O shape (no reflection, same pattern as the
already-proven `ProcessGitClient`/`ProcessDotnetCliRunner`) held up as expected. Scratch directories
cleaned up afterward.

**Status**: v0.3 is now fully closed — functionally complete, real-repo verified under both a JIT
build and a real NativeAOT publish. No known unverified NativeAOT path remains for v0.1 P0 +
private-feed support + v0.2 + v0.3.

## v0.4 — dependency graph (2026-08-22)

P1-2 and P1-5 landed — the full SPEC.md §16 v0.4 scope: a real dependency graph for
dependi-aware addons (package manifests, unification, `[overrides]`) plus unioning
addon-declared `[dotnet.*]` deps into the project's `.csproj`. See SPEC.md §8 for the design
this implements.

**New pieces**: `Dependi.Core.Resolve` gained `GraphAddonEntry`/`GraphResolutionResult` (the
walked-and-unified graph's shape) and `DependencyGraphResolver` (BFS discovery over each
resolved addon's own `dependi.toml` `[package]` manifest, followed by a separate
unify-and-finally-resolve pass: single-requirer shortcut, git-URL-mismatch hard error, pin-vs-
pin/pin-vs-version hard error, else a custom multi-constraint tag intersection since
`TagSelector` only ever handled one constraint at a time). `LockedAddon.Dependencies` (already
existed, previously always empty) is now actually populated with each addon's discovered child
names — both so `install` can display the graph shape without re-reading remote manifests, and
so `RemoveAsync` can compute orphan reachability purely locally (see below).
`DependencyWorkflow` gained two private orchestration helpers: `SyncAddonGraphAsync` (the
"frozen root, transitive always live" pattern — an operation's untouched top-level addons are
pinned to their exact locked commit as far as the graph walk's own *identity* goes, but still
fully participate in discovery/unification, so a real conflict introduced by a newly-added
sibling is still caught) and `SyncDotnetAsync` (the dotnet-side twin: the project's own
`[dotnet.*]` entries always win a name collision; among addon-only declared entries,
first-discovered wins). `AddAsync`/`UpdateAsync`/`RemoveAsync` were all rewritten to route
through these instead of resolving addons one at a time.

**Deliberate scope limits** (documented in code, consistent with SPEC.md §8's own "not a full
constraint solver" framing): discovery peeks at a name's manifest using whichever spec is
chosen first (the override, or the first requirer seen) — if unification later settles on a
*different* commit, the recorded dependency-name list reflects the discovery-time manifest, not
necessarily the finally-chosen one. And "frozen" only applies to a root's own identity — once
discovered, everything it transitively requires is still resolved against live specs, so a
transitive dependency shared with an untouched addon can still float forward on any operation.

**Verified**: 261/261 unit tests green (up from 246) — a new `DependencyGraphResolverTests.cs`
(leaf addon, a `dependi.toml` that happens to exist but isn't a `[package]` manifest, diamond
unification, disjoint version ranges, conflicting `rev` pins, differing git URLs for the same
name, `[overrides]` winning unconditionally, addon-declared `[dotnet.*]` unioned, and a
dependency cycle not infinite-looping) plus six new `DependencyWorkflowTests.cs` cases
(diamond unification through `AddAsync`, the same scenario's disjoint-range conflict surfaced
as a `WorkflowException`, orphan pruning through both `RemoveAsync` and a full `UpdateAsync`,
a still-needed transitive dependency surviving a `RemoveAsync` of its sibling, and an
addon-declared dotnet package actually landing in `.csproj`).

**Real end-to-end (JIT build, real local git repos, no fakes)**: built three real repos —
`core` (tagged `v1.0.0`/`v1.1.0`/`v1.2.0`), `a` (its own `dependi.toml` declaring
`[addons.core] version = "~1"`), `b` (`[addons.core] version = "~1.2"`) — cloned via real
`file:///` git URLs through the built CLI:

- `dependi add <a>` alone already pulled `core` in transitively, correctly resolved to `v1.2.0`
  (the highest tag satisfying `~1`) purely from `a`'s own single constraint.
- `dependi add <b>` afterward triggered real unification: `a`'s `~1` and `b`'s `~1.2` overlap
  only at 1.2.x, and exactly one `addons/core` folder resulted, still at `v1.2.0` — confirming
  the frozen-but-still-discovered design actually re-derives `a`'s constraint at its pinned
  commit rather than silently dropping it once `a` is no longer the fresh root.
- A third repo `c` (`[addons.core] version = "~1.0"`) — disjoint from `b`'s `~1.2` — was added
  next: `dependi add <c>` failed with `'core': no tag satisfies every requested version
  constraint at once: 'a' wants ~1; 'b' wants ~1.2; 'c' wants ~1.0`, exit 1, and — critically —
  `dependi.toml`, `dependi.lock`, and `addons/` were all byte-for-byte unchanged afterward
  (confirmed by re-reading all three): a failed graph resolution during `add` doesn't corrupt
  or partially-write state.
- `dependi remove a` (with `b` still needing `core`): `core` correctly stayed on disk and
  locked, only `a` was removed.
- `dependi remove b` afterward (nothing needs `core` anymore): `core` was correctly pruned from
  both disk and the lockfile — the network-free `LockedAddon.Dependencies`-based reachability
  BFS worked identically to the fakes-only test.

Scratch directory (`C:\dependi-v04-e2e`, three local repos plus a project) deleted afterward.

**AOT-verified 2026-08-22**: the user republished (`build.log` — succeeded with the same 12
pre-existing Tomlyn `IL2026`/`IL3050` warnings as v0.1–v0.3, zero new warnings from
`Dependi.Core.Resolve`). Re-ran the exact same three-repo diamond scenario above against the
real AOT binary (`dependi.exe`, fresh scratch directory, no fakes): `add a` pulled in `core`
@ v1.2.0 transitively; `add b` unified to the same single `core` @ v1.2.0; `add c` produced the
identical hard error naming `a`/`b`/`c` with manifest/lockfile/disk confirmed unchanged;
`remove a` kept `core` (still needed by `b`); `remove b` afterward pruned it from both disk and
the lockfile. Every result matched the JIT run exactly. Scratch directory cleaned up afterward.

**Status**: v0.4 is now fully closed — functionally complete, real-repo verified under both a
JIT build and a real NativeAOT publish. No known unverified NativeAOT path remains for v0.1 P0 +
private-feed support + v0.2 + v0.3 + v0.4.

## Risks

| Risk | Impact | Mitigation |
|---|---|---|
| ~~Spectre.Console.Cli isn't fully NativeAOT-clean~~ Confirmed broken under NativeAOT (crashes on every command) | Distribution decision (§12) blocked | **Resolved** 2026-08-21 — swapped to System.CommandLine 2.0.11, confirmed working via a real AOT publish + run of every command path. See "AOT verdict" note below |
| Tomlyn 2.x shipped a breaking v1 redesign (`TomlSerializer.Deserialize<T>` replaced the old `Toml.Parse().ToModel()` API) and Spectre.Console.Cli 0.55 added a `CancellationToken` parameter to `Execute` | Both broke first-draft code written from stale docs/training data | Caught and fixed by an actual `dotnet build` run (see below) — verified against the installed package sources, not guessed twice |
| ~~3-way merge (`git merge-file`) behavior on Windows line endings~~ | Patch feature (v0.3) risk | **Resolved** 2026-08-22 — confirmed via real `git merge-file`/`git apply` runs during v0.3 implementation and again during real-repo end-to-end verification (conflict scenario produced real `<<<<<<<`/`>>>>>>>` markers, no line-ending corruption). Found and fixed a related, more impactful bug along the way: `git apply --directory=<absolute Windows path>` itself fails with `invalid path` — see §6.2 |
| ~~v0.3's new code (`Dependi.Core.Patch`, `LockedAddon.PatchBaseSha`, `AddonInstaller`'s new signature) hasn't been run under a real NativeAOT publish yet~~ | Unknown until proven | **Resolved** 2026-08-22 — re-verified against a real NativeAOT publish, both the clean-merge and conflict real-repo scenarios reproduced correctly. See "v0.3 — patches" section above |
| ~~`NuGet.Protocol`'s NativeAOT/trim compatibility is unverified~~ Confirmed broken under NativeAOT (crashes on `add <nuget-id>` specifically, repository-signature deserialization) | Blocked the NativeAOT distribution target (§12, OQ-6) for any NuGet-touching command | **Resolved** 2026-08-21, AOT-confirmed 2026-08-22 — swapped to a direct HTTP client (`NuGetV3Client`, formerly `NuGetFlatContainerClient`), dropped `NuGet.Protocol`. Re-verified against a real NativeAOT publish covering both nuget.org and the private-feed path. See "AOT verdict" note above |
| ~~`dotnet restore` doesn't know about a private `nuget_source` even after dependi's own client authenticates against it~~ Was blocking `add`/`install`/`update` with `NU1101` for any private-feed package, i.e. the user's primary real-world use case | `add`/`install`/`update` fail with `NU1101` for any private-feed package | **Resolved** 2026-08-21 — `NuGetConfigWriter` registers the source in `NuGet.Config`, `NuGetPackageSourceCredentials_dependi` env var carries credentials to the restore child process. Re-verified against the real GitLab feed: `add`/`install`/`list`/`remove` all correct |
| ~~v0.2's `outdated`/`status`/`doctor` code (Tomlyn plugin.cfg parsing, `IGitClient.IsAvailableAsync`, `IFileSystem.EnumerateDirectories`) hasn't been run under a real NativeAOT publish yet~~ | Unknown until proven | **Resolved** 2026-08-22 — re-verified against a real NativeAOT publish, all four `doctor` issue kinds and `status`/`outdated` confirmed correct. See "AOT-verified" note above |
| ~~v0.4's new code (`DependencyGraphResolver`'s multi-constraint tag matching, `ManifestReader.ReadPackage` for `[package]` manifests) hasn't been run under a real NativeAOT publish yet~~ | Unknown until proven | **Resolved** 2026-08-22 — re-verified against a real NativeAOT publish, the full diamond/disjoint-conflict/orphan-pruning scenario reproduced exactly. See "v0.4 — dependency graph" section above |

## Key dates

| Date | Event |
|---|---|
| 2026-08-21 | Sprint start, foundation + `init` landed |
| 2026-08-21 | Versioning, real `IGitClient`, resolver, cache/subtree/sync engine, `add`/`install`/`remove`/`list` all landed and verified end-to-end against a real repo (116/116 tests green) |
| 2026-08-21 | `update`, NuGet delegation (P0-7) landed — full v0.1 P0 backlog complete, verified end-to-end against both a real git repo and a real NuGet package (156/156 tests green) |
| 2026-08-21 | Private NuGet feed support (`nuget_source`, service-index discovery, `NuGetConfigWriter`/credential-env bridge) landed and verified end-to-end against a real private GitLab feed (183/183 tests green) |
| 2026-08-22 | Re-verified full command surface (git addons, public NuGet, private NuGet feed) against a real NativeAOT publish — all correct. No known unverified AOT path remains for v0.1 P0 + private-feed support |
| 2026-08-22 | v0.2 shipped: `outdated`, `status`, `doctor` (P1-3, P1-4) — full SPEC.md §16 v0.2 scope, verified end-to-end against a real git repo and real nuget.org (218/218 tests green) |
| 2026-08-22 | v0.2 re-verified against a real NativeAOT publish (git + NuGet + `doctor`'s Tomlyn `plugin.cfg` parsing) — no known unverified AOT path remains for v0.1 P0 + private-feed support + v0.2 |
| 2026-08-22 | v0.3 shipped: `dependi patch` + 3-way re-apply with conflict detection (P1-1) — full SPEC.md §16 v0.3 scope, verified end-to-end against a real local git repo for both a clean merge and a genuine conflict (246/246 tests green) |
| 2026-08-22 | v0.3 re-verified against a real NativeAOT publish — both real-repo scenarios (clean merge, conflict) reproduced correctly. No known unverified AOT path remains for v0.1 P0 + private-feed support + v0.2 + v0.3 |
| 2026-08-22 | v0.4 shipped: dependency graph resolver + addon-declared dotnet union (P1-2, P1-5) — full SPEC.md §16 v0.4 scope, verified end-to-end against three real local git repos forming a real diamond, a real disjoint-constraint conflict, and real orphan pruning (261/261 tests green) |
| 2026-08-22 | v0.4 re-verified against a real NativeAOT publish — the full diamond/disjoint-conflict/orphan-pruning scenario reproduced exactly. No known unverified AOT path remains for v0.1 P0 + private-feed support + v0.2 + v0.3 + v0.4 |
| — | No fixed sprint end — v0.1 through v0.4 (the full original SPEC.md §16 phasing) are all shipped and AOT-verified; next up is whatever P2 work gets prioritized |
