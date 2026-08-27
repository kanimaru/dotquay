# Dotquay — Godot editor plugin

A dependency graph, conflict resolver, and add/update UI for [dotquay](../../../../README.md), the
package manager for Godot projects. GDScript only, Godot 4.7+.

The plugin is a **wrapper**, not a second implementation. It parses `dotquay --json` output and
renders it; every button is one CLI invocation, and the CLI stays the only thing that writes
`dotquay.toml`, `dotquay.lock`, or `addons/`. See SPEC.md §18 for why that boundary is drawn where
it is.

## Install

1. Copy `addons/dotquay/` into your Godot project.
2. Enable **Dotquay** in *Project → Project Settings → Plugins*.
3. Open the **Dotquay** tab, next to 2D / 3D / Script / Game / AssetLib.

If the dotquay executable isn't on your `PATH`, the panel says so and offers to find it. You can also
set it by hand at *Editor → Editor Settings → Dotquay → CLI Path*.

## Editor settings

| Setting | Default | What it does |
|---|---|---|
| `dotquay/general/cli_path` | *(auto-discovered)* | Path to the dotquay executable. Empty means "look on PATH". An editor setting rather than a project setting on purpose: the CLI is installed on *this machine*, and committing an absolute path into `project.godot` would break every teammate on a different OS. |
| `dotquay/general/refresh_on_focus` | `true` | Reload the graph when you switch back to the tab. Cheap — it reads the lockfile, not the network. |
| `dotquay/general/resolve_timeout_seconds` | `120` | Budget for the network-bound `Re-resolve`. |

## What the badges mean

| Badge | Meaning |
|---|---|
| `conflict` | Two requirers disagree and no single version satisfies both (SPEC.md §8). Click **Resolve…**. |
| `modified` | Hand-edited without a captured patch. `install`/`update` will refuse to overwrite it (P0-6). Use **Capture edits as a patch**. |
| `patched` | A captured patch is re-applied on every update (SPEC.md §10). Expected, not a problem. |
| `missing` | Locked, but the folder isn't on disk. Run **Install**. |
| `override` | Forced by an `[overrides]` entry in `dotquay.toml`. |
| `orphan` | Locked but nothing requires it any more — the drift `doctor` flags. |

## Two graph modes

**Refresh** rebuilds the graph from `dotquay.lock` — instant, offline, and true to what is actually
installed. It is what the panel opens with, and it can never show a conflict, because a lockfile only
exists once conflicts were already settled.

**Re-resolve** re-walks live git refs. Slower and network-bound, but the only mode that can discover
that your manifest currently has no solution, and the only one that can offer candidate versions to
settle it. Nothing is installed and no lockfile is written — resolving here is a question, not a
commitment.

## Development

The plugin lives in a minimal Godot project (`src/Dotquay.Godot/`) that exists so `res://` resolves
and so the tests can run against the real engine.

```powershell
./run_tests.ps1 -Godot "path/to/godot.exe"

# Also exercise the live CLI contract, which the fixture tests cannot cover:
$env:DOTQUAY_TEST_CLI = "path/to/dotquay.exe"
./run_tests.ps1 -Godot "path/to/godot.exe"
```

The import pass the script runs first is not optional: `class_name` globals live in
`.godot/global_script_class_cache.cfg`, which only exists after Godot has scanned the project.
Without it every typed reference in the tests fails to compile, which reads like a code error but is
really a missing cache.
