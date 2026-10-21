# Handover — linked-analyzer staging (`feature/link-analyzer-staging`)

Written 2026-10-21; **finished 2026-10-21, see "Final state" below.** The sections between are the original brief, kept for the reasoning.
Read `docs/DESIGN-v0.11-LOCAL-DOTNET-LINK.md` (the section *The analyzer lock, and why the overlay
carries a build target*) first; it is the spec for this work.

## Where it lives

| | |
|---|---|
| Worktree | `F:\projects\godot\dotquay\.claude\worktrees\friendly-pike-34f711` |
| Branch | `feature/link-analyzer-staging` (created 2026-10-21 from the commit below; it used to be a detached HEAD, i.e. reachable only through the reflog) |
| Commit | `d57fad8` `feat(link): stage linked analyzers so a locked generator can still be rebuilt` (2026-09-16, one commit, 467 insertions, 3 files) |
| Merge base with `main` | `6b6370e`, now **79 commits behind** `main` |
| Pushed? | No. Nothing from this branch exists on `origin` |

Files touched: `src/Dotquay.Core/Local/LocalTargetsWriter.cs` (+193),
`tests/Dotquay.Tests/Local/LocalTargetsTests.cs` (+204), `docs/DESIGN-v0.11-LOCAL-DOTNET-LINK.md` (+70).
No JSON/wire change, no CLI surface change, no `dotquay.toml`/lockfile change.

## What it does (one paragraph)

A Roslyn host (Rider, an IDE, a Roslyn MCP server) loads an analyzer DLL in place and holds a file lock
for its whole lifetime. For a `dotquay link --analyzer` generator that lock lands on the generator's own
build output, so its next build fails (MSB3061/MSB3026) and takes the consumer down with it. The
generated, gitignored `dotquay.local.targets` now also carries `DotquayStageLinkedAnalyzers`: after
`ResolveReferences` and before `CoreCompile` it copies every `@(Analyzer)` outside `$(NuGetPackageRoot)`
/ `$(NetCoreRoot)` into `$(IntermediateOutputPath)linked-analyzers\<name>-<sha256>\` and swaps the item
for the copy. Four details are load-bearing and each fails *silently* if wrong: unchanged file name
(a renamed copy gives CS8034, a warning, and less generated code), hash in the **directory** name,
design-time builds stage too, and scope is every locally built analyzer (`DisableDotquayAnalyzerStaging=true`
opts out). Stale directories are swept with `rmdir /s /q` / `rm -rf` under `IgnoreExitCode` rather than
`RemoveDir`, which would warn MSB3231 on every build.

## State of the implementation

**Done and passing (on its own base):** `dotnet test` in the worktree: 1037 passed, 0 failed (run
2026-10-21; the suite finished in 211 ms, which is fast enough that a stale `Dotquay.Core.dll` is worth
ruling out with a clean `obj/`+`bin/` before trusting it, per the repo's standing note).

**Tests the commit adds** (`LocalTargetsTests.cs`, all assert on the generated overlay XML):
- target present only for an analyzer link, exactly once with several links (`CarriesTheStagingTarget`,
  `HasNoStagingTarget`, `StillEmitsExactlyOneTarget`)
- file name preserved; neighbours copied with it (`KeepsTheAnalyzersOwnFileName`, `TakesTheAnalyzersNeighboursWithIt`)
- package/SDK analyzers left alone (`AreLeftWhereTheyAre`)
- item swap, stage root resolved inside the target (`SwapsTheAnalyzerItemForTheCopy`, `TheStageRoot_IsResolvedInsideTheTarget`)
- sweep keeps current dirs, silent about a held one (`StaleStagedDirectories_AreSweptButNotTheCurrentOnes`,
  `TheSweep_SaysNothingAboutADirectoryAHostStillHolds`)
- stays out of the committed csproj; unlinking removes it (`StaysOutOfTheCommittedCsproj`, `UnlinkingTheAnalyzer_TakesTheStagingWithIt`)

**Verified by hand, not by a test** (from the commit message): the published NativeAOT binary against a
real source generator. A process holding the staged assembly open no longer blocks the generator's
rebuild, the consumer picks up the new generator output, 0 warnings, held directory swept on a later
build. Reproduced originally in `F:\projects\godot\BotV2` with `Dev.Kani.Godot.Generator` linked to
`F:\projects\godot\BlackHaul\dev.kani.godot.generator`.

## Final state (2026-10-21)

Branch `feature/link-analyzer-staging-2` (worktree `F:\projects\godot\dotquay_analyzers`), cherry-picked
onto `main` at `33644ea`. Not pushed, not merged. The old branch `feature/link-analyzer-staging` and its
worktree `friendly-pike-34f711` are untouched and can be deleted after the merge.

| # | Item | Result |
|---|---|---|
| 1 | Onto current `main` | Cherry-pick of `d57fad8`, one conflict (a `using` line in `LocalTargetsTests.cs`); both sides kept, `System` first. |
| 2 | Full suite, clean `obj/`+`bin/` | 1585 passed, 0 failed before the new tests; 1589 after (+3 real-process, +1 overlay-upgrade). |
| 3 | Real NativeAOT binary | Needed the `vcvars64.bat` recipe (plain publish failed: vswhere not found). With the published exe: `link Gen.Pkg ../Gen --analyzer` wrote the overlay; `dotnet build` staged `Gen-<sha256>\Gen.dll`; with that DLL held `FileShare.None`, rebuilding the generator, then the consumer, succeeded with 0 warnings and the consumer printed the new generated value; a second hash directory appeared while the first was held; after release the next build swept it. Control: same lock with `-p:DisableDotquayAnalyzerStaging=true` on the *original* DLL fails the generator rebuild (MSB3061/MSB3026/MSB3027). |
| 4 | Real MSBuild test | `tests/Dotquay.Tests/Local/LinkedAnalyzerStagingRealProcessTests.cs`: lock-not-blocking, sweep, and the disabled-staging control (Windows only). Passes on Windows and in `mcr.microsoft.com/dotnet/sdk:10.0` on Linux. |
| 5 | SPEC | New "Local .NET links, and the overlay" paragraph at the end of §7.4. |
| 6 | Open questions | Below. |

**Not verified:** (a) the CS8034 trap (a renamed copy that Roslyn refuses to load) is not exercised by the
real-process test, because its "analyzer" is a plain class library to stay offline. The fixture used for
item 3 was likewise a trivial generator, not `Dev.Kani.Godot.Generator` in BotV2, and no real Roslyn host
(Rider, an MCP server) held the file: the lock was a `FileShare.None` handle. Re-run in BotV2 before relying on it.
(b) macOS was not run; Linux was, in a container.

### The three open questions, settled

- **Existing overlays.** `SyncLocalTargets` is called from `InstallAsync` and from `SyncDotnetAsync` (which
  `AddAsync` and the update path, `UpdateCoreAsync`, use, hence also `override`). `link` and `link --remove`
  end in `InstallAsync` unless `--no-install`. The overlay is rewritten whole every time
  (`Sync_OverAnOverlayFromBeforeTheStagingTarget_ReplacesItWhole`). So an existing user gets the target on
  their next `install`, `add`, `update`, `override` or `link`, without relinking. `status`, `doctor`
  and `build` do not regenerate it. Say that in the release notes.
- **Non-Windows sweep.** Run on Linux in a container: the `rm -rf` branch works; since Unix does not
  enforce the lock, the "held" directory is swept at once there. macOS untested but uses the same `rm -rf`.
- **Disk growth.** One directory per generator build, swept by the next consumer build; only a directory a
  host still holds survives, until it exits. So `obj\` holds the current copy plus at most the held ones.
  Documented in SPEC §7.4. After unlinking, the last copies stay until `obj\` is cleaned.

### Left for the user

Review, merge (fast-forward) and push `main` yourself; remove the old worktree and branch; delete this file or keep it as history.
The exe was installed to `~/.local/bin/dotquay.exe` (previous kept as `dotquay.previous.exe`; the older
`dotquay.previous.exe` from 12:18 was kept as `dotquay.previous-1218.exe`).
