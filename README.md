# dotquay

[![pipeline status](https://gitlab.kemomi.de/wild-alliance/dotquay/badges/main/pipeline.svg)](https://gitlab.kemomi.de/wild-alliance/dotquay/-/pipelines)

A package manager for Godot projects: GDScript addons from git repositories, plus .NET
dependencies via NuGet, in one manifest with reproducible, lockfile-driven installs.

Full design: [`docs/SPEC.md`](docs/SPEC.md). Sprint log and what's actually been verified (real
repos, real NativeAOT publishes, not just unit tests): [`docs/SPRINT-01.md`](docs/SPRINT-01.md).

## Status

Latest release: **v0.21.0** (2026-10-04). Download it from **Deployments → Releases**, or run
`dotquay self update` on an existing install.

Every feature below is on `main` and in that release. The unit suite stands at 1409 tests. Features were also
verified against real git repositories and, for most, a real NativeAOT publish, not just fakes —
each design doc's own `Status:` line records exactly what was checked. The original v0.1–v0.4 roadmap is
in [`docs/SPEC.md` §16](docs/SPEC.md#16-phasing).

| Feature | What it adds | Design |
|---|---|---|
| v0.1–v0.4 | Reproducible core, `outdated`/`status`/`doctor`, patches with 3-way re-apply, dependency graph | [`SPEC.md`](docs/SPEC.md) |
| v0.5 | `--json` output and the Godot editor plugin | [`SPEC.md` §17–18](docs/SPEC.md#17-machine-readable-output-v05) |
| v0.7 | Multiple NuGet feeds and a shared credential store (`login`/`logout`) | [`DESIGN-v0.7`](docs/DESIGN-v0.7-AUTH.md) |
| v0.8 | Addon-declared NuGet feeds | [`DESIGN-v0.8`](docs/DESIGN-v0.8-FEED-PROPAGATION.md) |
| v0.9 | `add --preview`, feed approval in the editor, scene-based plugin UI | [`DESIGN-v0.9`](docs/DESIGN-v0.9-INSTALL-PREVIEW.md) |
| v0.10 | PackageReference asset metadata | [`DESIGN-v0.10`](docs/DESIGN-v0.10-PACKAGE-ASSETS.md) |
| v0.11 | `link` a `[dotnet]` package to a local `.csproj` | [`DESIGN-v0.11`](docs/DESIGN-v0.11-LOCAL-DOTNET-LINK.md) |
| v0.12 | `status --explain`: why an addon is modified | [`DESIGN-v0.12`](docs/DESIGN-v0.12-MODIFIED-REASON.md) |
| v0.13 | `doctor`'s install-folder drift check; linked addons survive an `install_as` rename | — |
| v0.14 | `source add/list/remove` | [`DESIGN-v0.14`](docs/DESIGN-v0.14-FEED-COMMANDS.md) |
| v0.15 | Git transport, URL identity, fetch diagnostics | [`DESIGN-v0.15`](docs/DESIGN-v0.15-GIT-TRANSPORT.md) |
| v0.16 | Junction checks before writing through a link | [`DESIGN-v0.16`](docs/DESIGN-v0.16-LINK-SAFETY.md) |
| v0.17 | `self check/update/rollback/notify` | [`DESIGN-v0.17`](docs/DESIGN-v0.17-SELF-UPDATE.md) |
| v0.18 | `dotquay mcp`: reads and guarded writes for coding agents | [`DESIGN-v0.18`](docs/DESIGN-v0.18-MCP.md), [`SPEC.md` §20](docs/SPEC.md#20-mcp-server-v018) |
| v0.19 | NuGet packages as nodes in the dependency graph | [`DESIGN-v0.19`](docs/DESIGN-v0.19-UNIFIED-GRAPH.md) |
| v0.20 | Guessing an addon's subtree, and asking before using the guess | [`DESIGN-v0.20`](docs/DESIGN-v0.20-SUBTREE-GUESS.md) |
| v0.21 | One version-constraint language for git addons and NuGet packages | [`DESIGN-v0.21`](docs/DESIGN-v0.21-VERSION-CONSTRAINTS.md) |
| v0.22 | Never install Git LFS placeholders as an addon | [`DESIGN-v0.22`](docs/DESIGN-v0.22-GIT-LFS.md) |
| v0.23 | The recorded content hash ignores line endings | [`DESIGN-v0.23`](docs/DESIGN-v0.23-LINE-ENDINGS.md) |

Feature numbers and release numbers are separate sequences: features are numbered by design doc,
releases by CI (see [CI/CD and releases](#cicd-and-releases)). That is why the v0.22 and v0.23
features shipped inside release v0.21.0.

## Quick start

```
dotquay init                                   # scaffold dotquay.toml in a Godot project
dotquay add https://github.com/nathanhoad/godot_dialogue_manager   # git addon
dotquay add Newtonsoft.Json                    # NuGet package
dotquay install                                # fresh clone -> addons/ + .csproj match the lockfile
```

Coding agents can drive the same operations through MCP instead of a shell:

```
dotquay mcp --project /path/to/a/godot/project
```

See [`docs/SPEC.md` §20](docs/SPEC.md#20-mcp-server-v018) for the tool catalogue and a connection
walkthrough.

## Commands

| Command | What it does |
|---|---|
| `init <directory>` | Scaffold a `dotquay.toml` in the project root (or, with `--package`, inside an addon folder — see "Declaring dependencies as an addon author" below). |
| `add <source>` | Add a dependency (git URL or NuGet package ID), resolve it, lock it, and install it. |
| `remove <name>` | Remove a dependency from the manifest, lockfile, and disk. |
| `install` | Make `addons/` and `.csproj` match the lockfile exactly — the reproducible, no-network-resolution path for a fresh clone or CI. |
| `update [name]` | Re-resolve one dependency (or all of them, if omitted) and reinstall. |
| `list` | Show installed dependencies with their versions. |
| `graph` | Show the dependency graph: who requires what, and where it conflicts. `--resolve` re-walks it against live refs. |
| `override <name>` | Force one addon to a specific version, branch, or commit — the conflict escape hatch. |
| `source` | Declare, list, or remove the NuGet feeds this project searches, in order. |
| `link <name> <path>` | Develop a dependency in place: point an addon at a local folder, or a `[dotnet]` package at a local `.csproj`. Per machine, never recorded in the lockfile. |
| `outdated` | Check for newer versions without cloning anything (`git ls-remote` / NuGet metadata only). |
| `status` | Diff disk against the lockfile: clean / patched as expected / modified / missing. |
| `patch <name>` | Capture hand-edits to an installed addon as a patch file, replayed via 3-way merge on future `install`/`update`. |
| `doctor` | Run sanity checks: git on PATH, dangling lockfile entries, `plugin.cfg` version drift, orphaned `addons/` folders. |
| `login <host>` / `logout <host>` | Store or forget credentials for a private feed or git host. |
| `mcp` | Start dotquay as a stdio MCP server, exposing reads and guarded writes as tools for coding agents — see [`docs/SPEC.md` §20](docs/SPEC.md#20-mcp-server-v018). |
| `self check` | Ask whether a newer dotquay exists. Exits 3 when one does. |
| `self update` | Download, verify and install the newest dotquay, replacing this executable. |
| `self rollback` | Put back the dotquay the last update replaced. |
| `self notify on\|off` | Turn the once-a-day "new release" notice on or off. |

Run `dotquay --help` or `dotquay <command> --help` for the full option list. Every command takes
`-C/--project <dir>`, and most take `--json` for a single machine-readable document (SPEC.md §17).

## The manifest (`dotquay.toml`)

```toml
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

`version` is one constraint language for git addons and NuGet packages alike (node-semver, the one
npm, Cargo, Helm and Composer share; spec: `docs/SPEC.md` §7.1.5):

| Written | Means |
| --- | --- |
| `1.2.3` | exactly 1.2.3 |
| `~2.44` | `2.44.x` (`~2` matches any `2.x.x`) |
| `^1.2.3` | `>=1.2.3 <2.0.0` (`^0.2.3` stays below `0.3.0`) |
| `13.0.*`, `1.x` | any patch / minor |
| `>=1.2 <2`, `1.2 - 1.4`, `^1 \|\| ^2` | comparators, hyphen range, either-or |

Prereleases are only chosen when the range names one (`^5.1.0-rc1` accepts `5.1.0-rc5`; `^5.0` never
selects `5.1.0-rc1`). `update` picks the highest version that satisfies every constraint on a name,
and `outdated` reports exactly that: a package pinned to `3.0.0` is current even though `3.1.1`
exists. NuGet's own interval syntax (`[1.0,2.0)`) is not accepted; dotquay resolves the range itself
and writes only the exact result into the `.csproj`.

`dotquay add` picks a sensible default automatically: for a git addon the latest release tag's
`~major.minor` range if the repo has tags, otherwise a hard `rev` pin to the default branch's tip; for
a NuGet package `^<newest stable>`.

## Dependency graph (v0.4)

Dotquay-aware addons can declare their own dependencies, and dotquay walks and unifies that graph
automatically — if two addons you depend on both need a third shared addon, exactly one copy of it
lands in `addons/`, at a version satisfying every requirer's constraint at once. Conflicting
requirements (disjoint version ranges, or the same name pinned to two different commits) are a
hard error naming every requirer, not a silent pick.

### Declaring dependencies as an addon author

Ship a `dotquay.toml` inside your addon's own folder (next to `plugin.cfg`) with a `[package]`
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
src/Dotquay.Core/    pipeline stages behind interfaces — manifest/lockfile I/O, git resolution,
                      the dependency graph resolver, sync engine, patch workflow, NuGet delegation.
                      No console/CLI code lives here.
src/Dotquay.Cli/     thin System.CommandLine command layer over Dotquay.Core.
src/Dotquay.Mcp/     `dotquay mcp`: a stdio MCP server exposing dotquay's read and guarded write
                      operations as tools for coding agents — see docs/SPEC.md §20. Depends only
                      on Dotquay.Core, never Dotquay.Cli.
src/Dotquay.Godot/   the editor plugin (addons/dotquay/). GDScript only, scene-based UI, no
                      resolution logic: every button is one `dotquay ... --json` call.
tests/Dotquay.Tests/ xUnit tests. Everything goes through IFileSystem/IGitClient/INuGetClient/
                      IPatchTool/IDotnetCliRunner fakes, never real disk or network — see
                      Dotquay.Tests/Support/. The exceptions are the real-process tests under
                      Git/ and Mcp/, which guard bugs that only exist in a real process tree.
```

The `Dotquay.Core` / `Dotquay.Cli` / `Dotquay.Tests` split and the interface seams are a deliberate
architecture decision — see SPEC.md §12.

## Build & test

Requires the .NET 10 SDK.

```
dotnet build
dotnet test
```

The editor plugin has its own GDScript suite. Run it through the script, which does Godot's import
pass first (without it every `class_name` fails to resolve):

```
pwsh src/Dotquay.Godot/run_tests.ps1
```

Try it:

```
dotnet run --project src/Dotquay.Cli -- init
```

## Publishing

The distribution target is a trimmed NativeAOT single-file binary (SPEC.md §12, decided OQ-6):

```
dotnet publish src/Dotquay.Cli -c Release -r <win-x64|linux-x64|osx-arm64> -p:PublishAot=true
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
dotnet publish src/Dotquay.Cli -c Release -r win-x64 --self-contained -p:PublishAot=false -p:PublishTrimmed=true -p:PublishSingleFile=true
```

## CI/CD and releases

Releases are continuous: every green push to `main` that changes something under `src/` becomes a
release, with no version to bump by hand. Pushes that only touch tests, docs, or the pipeline run the
suite and release nothing.

| Stage | Jobs | What happens |
|---|---|---|
| test | `test` | The full xUnit suite. |
| tag | `auto-tag` | Reserves the next patch version and pushes it as a `vX.Y.Z` tag (`main` + `src/` changes only). |
| build | `build:linux-x64`, `build:windows-x64`, `smoke:linux-x64` | Real NativeAOT for linux-x64, the trimmed self-contained fallback for win-x64 (see above), then the linux binary is run for real — `--version`, `init`, and the `--json` envelopes — before anything ships. |
| release | `upload:*`, `release:auto` / `release:tagged` | Binaries, checksums and `manifest.json` go to the Generic Package Registry, then a GitLab Release links to them. |

**Choosing a version yourself.** To cut a specific major or minor, push the tag by hand. That tag
gets its own pipeline through the same build and release jobs, and `auto-tag` bumps the patch from
it next time:

```
git tag v0.22.0
git push origin v0.22.0
```

**What `auto-tag` relies on:**

- **The version never goes backwards.** It takes the higher of the newest `v*` tag and
  `Dotquay.Cli.csproj`'s `<Version>`, and bumps the patch from that. CI stamps the version into the
  binary at publish time, so the csproj's `<Version>` is only a floor.
- **The tag is pushed with `$CI_JOB_TOKEN`.** That needs **Settings → CI/CD → Job token permissions
  → "Allow Git push requests to the repository"** turned on. Without it the push fails with "You are
  not allowed to push code to this project". The push uses `-o ci.skip` so the tag doesn't start a
  second, racing release pipeline.
- **The remote's tags win.** Each runner slot (`concurrent-0`, `concurrent-1`, …) keeps its own
  build volume, and a tag from a run whose push failed survives in that volume. So `auto-tag`
  fetches with `--tags --force --prune --prune-tags`: tags missing from the remote are dropped, and
  tags that exist on both sides are overwritten with the remote's. Without `--force`, a stale tag
  sharing its name with a shipped one made the fetch refuse with "would clobber existing tag" and
  fail the job with an empty log (job 6147).

**Release layout.** Files are stored under the version **without** its `v`:

```
packages/generic/dotquay/0.21.0/dotquay-linux-x64          (+ .sha256)
packages/generic/dotquay/0.21.0/dotquay-windows-x64.exe    (+ .sha256)
packages/generic/dotquay/0.21.0/manifest.json
```

`manifest.json` names every binary with its SHA-256 and size, and it is what `dotquay self update`
reads; a release without it is invisible to the updater. Both release jobs build their links from
`PACKAGE_VERSION`, which `upload:manifest` writes from the exact version it uploaded under. Links
built straight from the tag pointed at `.../v0.21.0/` while the files were in `.../0.21.0/`, which
left the first hand-tagged release with five dead links.

## Installing, and staying current

```sh
# Linux / macOS
DOTQUAY_TOKEN=<gitlab token> sh scripts/install.sh

# Windows
$env:DOTQUAY_TOKEN = "<gitlab token>"; .\scripts\install.ps1
```

Both verify the download against the checksum the release publishes, run it once before installing
it, and put it in `~/.local/bin` — the same place `dotquay self update` expects to be able to write,
so anything installed this way can keep itself current afterwards:

```
dotquay self check      # is there a newer one?
dotquay self update     # install it
dotquay self rollback   # put the previous one back
```

A one-line notice on stderr mentions a new release at most once a day; `dotquay self notify off`
turns it off without turning off `self check`. While the release project is private, run
`dotquay login gitlab.kemomi.de` once and updates work without a token on the command line.

Design and the reasoning behind every refusal: [`docs/DESIGN-v0.17-SELF-UPDATE.md`](docs/DESIGN-v0.17-SELF-UPDATE.md).
