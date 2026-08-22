# dependi

A package manager for Godot projects: GDScript addons from git repositories, plus .NET
dependencies via NuGet, in one manifest with reproducible, lockfile-driven installs.

Full design: [`docs/SPEC.md`](docs/SPEC.md). Current sprint: [`docs/SPRINT-01.md`](docs/SPRINT-01.md).

## Status

v0.1 in progress. `dependi init` works end to end. `add`/`remove`/`install`/`update`/`list` are
wired into the CLI but not yet implemented — running them prints which SPEC.md requirement they're
tracked under and exits non-zero. See `docs/SPRINT-01.md` for the current backlog.

## Project layout

```
src/Dependi.Core/   pipeline stages behind interfaces — manifest, lockfile, init, and (soon)
                     resolver + sync engine. No console/CLI code lives here.
src/Dependi.Cli/    thin System.CommandLine command layer over Dependi.Core.
tests/Dependi.Tests/ xUnit tests. Everything goes through IFileSystem/IGitClient/INuGetClient,
                     never real disk or network (see Dependi.Tests/Support/InMemoryFileSystem.cs).
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
dotnet publish src/Dependi.Cli -c Release -r <win-x64|linux-x64|osx-arm64>
```

Spectre.Console.Cli was tried first but proved incompatible with NativeAOT — it built cleanly but
crashed on every command at runtime. The CLI now uses System.CommandLine, which publishes with
zero AOT/trim warnings of its own (see `docs/SPRINT-01.md`). If a future dependency ever blocks a
clean AOT publish, the documented fallback is a trimmed self-contained publish
(`-p:PublishAot=false -p:PublishTrimmed=true`).
