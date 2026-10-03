# dotquay

[![pipeline status](https://gitlab.kemomi.de/wild-alliance/dotquay/badges/main/pipeline.svg)](https://gitlab.kemomi.de/wild-alliance/dotquay/-/pipelines)

A package manager for Godot projects: GDScript addons from git repositories, plus .NET
dependencies via NuGet, in one manifest with reproducible, lockfile-driven installs.

Full design: [`docs/SPEC.md`](docs/SPEC.md). Sprint log and what's actually been verified (real
repos, real NativeAOT publishes, not just unit tests): [`docs/SPRINT-01.md`](docs/SPRINT-01.md).

## Status

v0.1 through v0.18 are shipped. The unit test suite has grown well past the original 261 (1090 as
of v0.18), and every feature has been verified against real git repositories (and, for the
git-addon, private-feed, and MCP-server paths, a real NativeAOT publish), not just fakes. The list
below is the original v0.1-v0.4 roadmap; everything shipped since — private NuGet feeds and auth,
machine-readable `--json` output, the Godot editor plugin, local links, patch-conflict handling,
self-update, and the `dotquay mcp` server for coding agents (§20) — is covered in
[`docs/SPEC.md`](docs/SPEC.md).

- **v0.1 — reproducible core**: `init`, `add`, `remove`, `install`, `update`, `list`, all three
  git source kinds (`version`/`branch`/`rev`), NuGet delegation, a global content-addressed fetch
  cache, and private NuGet feed support.
- **v0.2 — safety and visibility**: `outdated`, `status`, `doctor`.
- **v0.3 — patches**: `dotquay patch <name>` captures hand-edits, `install`/`update` re-apply them
  via a real 3-way merge, with conflicts halting cleanly instead of clobbering anything.
- **v0.4 — dependency graph**: dotquay-aware addons can declare their own dependencies; dotquay
  walks the graph, unifies shared dependencies to one version, and unions any addon-declared NuGet
  packages into the project's `.csproj`.

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
| `outdated` | Check for newer versions without cloning anything (`git ls-remote` / NuGet metadata only). |
| `status` | Diff disk against the lockfile: clean / patched as expected / modified / missing. |
| `patch <name>` | Capture hand-edits to an installed addon as a patch file, replayed via 3-way merge on future `install`/`update`. |
| `doctor` | Run sanity checks: git on PATH, dangling lockfile entries, `plugin.cfg` version drift, orphaned `addons/` folders. |
| `mcp` | Start dotquay as a stdio MCP server, exposing reads and guarded writes as tools for coding agents — see [`docs/SPEC.md` §20](docs/SPEC.md#20-mcp-server-v018). |
| `self check` | Ask whether a newer dotquay exists. Exits 3 when one does. |
| `self update` | Download, verify and install the newest dotquay, replacing this executable. |
| `self rollback` | Put back the dotquay the last update replaced. |

Run `dotquay --help` or `dotquay <command> --help` for the full option list.

## The manifest (`dotquay.toml`)

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
tests/Dotquay.Tests/ xUnit tests. Everything goes through IFileSystem/IGitClient/INuGetClient/
                      IPatchTool/IDotnetCliRunner fakes, never real disk or network — see
                      Dotquay.Tests/Support/.
```

The `Dotquay.Core` / `Dotquay.Cli` / `Dotquay.Tests` split and the interface seams are a deliberate
architecture decision — see SPEC.md §12.

## Build & test

Requires the .NET 10 SDK.

```
dotnet build
dotnet test
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

`.gitlab-ci.yml` runs the test suite, then publishes both `linux-x64` (real NativeAOT) and
`win-x64` (trimmed self-contained fallback, see above) binaries as job artifacts on every
pipeline. Pushing a tag additionally uploads both binaries to this project's Generic Package
Registry and creates a GitLab Release with download links:

```
git tag v0.5.0
git push origin v0.5.0
```

Released binaries then show up under **Deployments → Releases**.

Tagging also triggers `bump-version`, which pushes a follow-up commit to `main` bumping
`Dotquay.Cli.csproj`'s patch version (e.g. `0.5.0` → `0.5.1`) so subsequent work is already
developed under the next version. This job runs independently of test/build/release — it moves
the version forward even if the build breaks — and needs a `VERSION_BUMP_TOKEN` CI/CD variable
(a Maintainer-scoped access token with `write_repository`) to push to the protected branch.

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
