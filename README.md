# dependi

[![pipeline status](https://gitlab.kemomi.de/wild-alliance/dependi/badges/main/pipeline.svg)](https://gitlab.kemomi.de/wild-alliance/dependi/-/pipelines)

A package manager for Godot projects: GDScript addons from git repositories, plus .NET
dependencies via NuGet, in one manifest with reproducible, lockfile-driven installs.

Full design: [`docs/SPEC.md`](docs/SPEC.md). Sprint log and what's actually been verified (real
repos, real NativeAOT publishes, not just unit tests): [`docs/SPRINT-01.md`](docs/SPRINT-01.md).

## Status

v0.1 through v0.4 are shipped — the full original roadmap. 261 unit tests pass, and every feature
below has been verified against real git repositories (and, for the git-addon and public/private
NuGet paths, a real NativeAOT publish), not just fakes:

- **v0.1 — reproducible core**: `init`, `add`, `remove`, `install`, `update`, `list`, all three
  git source kinds (`version`/`branch`/`rev`), NuGet delegation, a global content-addressed fetch
  cache, and private NuGet feed support.
- **v0.2 — safety and visibility**: `outdated`, `status`, `doctor`.
- **v0.3 — patches**: `dependi patch <name>` captures hand-edits, `install`/`update` re-apply them
  via a real 3-way merge, with conflicts halting cleanly instead of clobbering anything.
- **v0.4 — dependency graph**: dependi-aware addons can declare their own dependencies; dependi
  walks the graph, unifies shared dependencies to one version, and unions any addon-declared NuGet
  packages into the project's `.csproj`.

## Quick start

```
dependi init                                   # scaffold dependi.toml in a Godot project
dependi add https://github.com/nathanhoad/godot_dialogue_manager   # git addon
dependi add Newtonsoft.Json                    # NuGet package
dependi install                                # fresh clone -> addons/ + .csproj match the lockfile
```

## Commands

| Command | What it does |
|---|---|
| `init <directory>` | Scaffold a `dependi.toml` in the project root (or, with `--package`, inside an addon folder — see "Declaring dependencies as an addon author" below). |
| `add <source>` | Add a dependency (git URL or NuGet package ID), resolve it, lock it, and install it. |
| `remove <name>` | Remove a dependency from the manifest, lockfile, and disk. |
| `install` | Make `addons/` and `.csproj` match the lockfile exactly — the reproducible, no-network-resolution path for a fresh clone or CI. |
| `update [name]` | Re-resolve one dependency (or all of them, if omitted) and reinstall. |
| `list` | Show installed dependencies with their versions. |
| `outdated` | Check for newer versions without cloning anything (`git ls-remote` / NuGet metadata only). |
| `status` | Diff disk against the lockfile: clean / patched as expected / modified / missing. |
| `patch <name>` | Capture hand-edits to an installed addon as a patch file, replayed via 3-way merge on future `install`/`update`. |
| `doctor` | Run sanity checks: git on PATH, dangling lockfile entries, `plugin.cfg` version drift, orphaned `addons/` folders. |

Run `dependi --help` or `dependi <command> --help` for the full option list.

## The manifest (`dependi.toml`)

```toml
[project]
godot = "4.3"

[addons.dialogue_manager]
git = "https://github.com/nathanhoad/godot_dialogue_manager"
version = "~2.44"        # or: branch = "main"   or: rev = "<full-sha>"

[dotnet.Newtonsoft.Json]
version = "13.0.*"

# Force a specific resolution for a name every other addon's own dependencies agree to disagree
# on (see "Dependency graph" below) — wins unconditionally, skips unification entirely.
[overrides.core]
git = "https://example.com/core.git"
version = "~2.0"
```

`version` matches against git tags using Cargo-style `~` ranges (`~2.44` matches `2.44.x`, `~2`
matches any `2.x.x`). `dependi add` picks a sensible default automatically: the latest release
tag's `~major.minor` range if the repo has tags, otherwise a hard `rev` pin to the default
branch's tip.

## Dependency graph (v0.4)

Dependi-aware addons can declare their own dependencies, and dependi walks and unifies that graph
automatically — if two addons you depend on both need a third shared addon, exactly one copy of it
lands in `addons/`, at a version satisfying every requirer's constraint at once. Conflicting
requirements (disjoint version ranges, or the same name pinned to two different commits) are a
hard error naming every requirer, not a silent pick.

### Declaring dependencies as an addon author

Ship a `dependi.toml` inside your addon's own folder (next to `plugin.cfg`) with a `[package]`
header instead of `[project]`:

```toml
[package]
name = "my_addon"
version = "1.0.0"

[addons.core]
git = "https://example.com/core.git"
version = "~1.4"

[dotnet.Newtonsoft.Json]
version = "13.0.*"
```

Anything under `[addons.*]` gets pulled into the consuming project's dependency graph
automatically; anything under `[dotnet.*]` gets unioned into the consuming project's `.csproj`
(the project's own `[dotnet.*]` entries always win a name collision). Third-party addons without
this file are treated as leaf nodes, same as before — this is additive, not a requirement.

## Project layout

```
src/Dependi.Core/    pipeline stages behind interfaces — manifest/lockfile I/O, git resolution,
                      the dependency graph resolver, sync engine, patch workflow, NuGet delegation.
                      No console/CLI code lives here.
src/Dependi.Cli/     thin System.CommandLine command layer over Dependi.Core.
tests/Dependi.Tests/ xUnit tests. Everything goes through IFileSystem/IGitClient/INuGetClient/
                      IPatchTool/IDotnetCliRunner fakes, never real disk or network — see
                      Dependi.Tests/Support/.
```

The `Dependi.Core` / `Dependi.Cli` / `Dependi.Tests` split and the interface seams are a deliberate
architecture decision — see SPEC.md §12.

## Build & test

Requires the .NET 10 SDK.

```
dotnet build
dotnet test
```

Try it:

```
dotnet run --project src/Dependi.Cli -- init
```

## Publishing

The distribution target is a trimmed NativeAOT single-file binary (SPEC.md §12, decided OQ-6):

```
dotnet publish src/Dependi.Cli -c Release -r <win-x64|linux-x64|osx-arm64> -p:PublishAot=true
```

Spectre.Console.Cli was tried first but proved incompatible with NativeAOT — it built cleanly but
crashed on every command at runtime. The CLI now uses System.CommandLine, which publishes with
zero AOT/trim warnings of its own (see `docs/SPRINT-01.md`). Likewise, NuGet delegation uses a
direct HTTP client (`NuGetV3Client`) rather than `NuGet.Protocol`, whose reflection-based JSON
handling doesn't survive trimming.

NativeAOT compilation isn't cross-OS — it needs the target's own native toolchain. If you're
building somewhere that doesn't have one for a given target (this project's own CI, for instance,
only has a Linux Docker runner, so its Windows build can't natively compile), the documented
fallback is a trimmed self-contained publish instead:

```
dotnet publish src/Dependi.Cli -c Release -r win-x64 --self-contained -p:PublishAot=false -p:PublishTrimmed=true -p:PublishSingleFile=true
```

## CI/CD and releases

`.gitlab-ci.yml` runs the test suite, then publishes both `linux-x64` (real NativeAOT) and
`win-x64` (trimmed self-contained fallback, see above) binaries as job artifacts on every
pipeline. Pushing a tag additionally uploads both binaries to this project's Generic Package
Registry and creates a GitLab Release with download links:

```
git tag v0.5.0
git push origin v0.5.0
```

Released binaries then show up under **Deployments → Releases**.
