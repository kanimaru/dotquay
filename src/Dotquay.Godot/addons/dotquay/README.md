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

## What the badges mean

| Badge | Meaning |
|---|---|
| `conflict` | Two requirers disagree and no single version satisfies both (SPEC.md §8). Click **Resolve…**. |
| `modified` | Differs from its locked commit, with no captured patch. `install`/`update` refuse to overwrite it (P0-6). Start with **Why is this modified?** — an addon Godot merely rewrote a `uid://` in looks identical to one you edited, and only the first is safe to discard. Then **Capture edits as a patch**, or force the update from that dialog. |
| `patched` | A captured patch is re-applied on every update (SPEC.md §10). Expected, not a problem. |
| `missing` | Locked, but the folder isn't on disk. Run **Install**. |
| `override` | Forced by an `[overrides]` entry in `dotquay.toml`. |
| `orphan` | Locked but nothing requires it any more — the drift `doctor` flags. |

## Adding an addon

The **Add…** form covers everything `dotquay add` accepts:

| Field | What it sets |
|---|---|
| Source | Git repository or NuGet package — dotquay routes both through the same `add` |
| URL / ID | The repository or the package ID. A `#ref` suffix (`…/repo#main`) asks the remote whether that's a branch, a tag, or a commit |
| Pin to | **Latest release** (the default: newest tag, or the default branch's tip if there are none), **Version** (a range like `~2.5`, or one exact tag), **Branch**, or **Commit** |
| Subtree | Git only. The folder inside the repo to extract, when the addon isn't at `addons/<repo name>` |
| Install as | Git only. The folder under `addons/` to install into, when it shouldn't be the addon's own name — some addons are preloaded by an exact path |

Branch and Commit don't appear for a NuGet package, which has neither, and the subtree and install
folder are hidden too. If the URL already carries a `#ref`, **Pin to** locks and says why: naming the
source twice is a combination `add` refuses outright.

Tracking a branch still pins the resolved commit in `dotquay.lock`, so a teammate's `install` gets
exactly what you tested. **Update** is what moves it forward.

## …and shows you what you're agreeing to

**Add…** on a git URL doesn't install. It runs `dotquay add --preview`, which resolves the real graph
without writing anything, and shows you:

- **Every addon that would land**, marked `+` arriving, `~` moving to a new commit, `=` unchanged —
  each one saying who wants it, so "the four things it drags in" is visible before the folders are.
- **The NuGet packages** those addons declare, attributed to the addon that declared each.
- **Any NuGet feed an addon wants your project to trust** (SPEC.md §11.3), naming the addon asking,
  the package IDs the feed would be allowed to serve, and what breaks if you decline.

A feed on a host already in your `trusted_hosts` is shown as adopted automatically rather than as a
choice. Every other one needs a tick, and **Install** stays disabled until they all have one —
declining would make the add fail on the CLI side anyway, so offering the button would be a lie.

Ticking a box approves that feed **for this install only**. It never adds the host to
`trusted_hosts`, so the next addon from the same host still asks.

## Two graph modes

**Refresh** rebuilds the graph from `dotquay.lock` — instant, offline, and true to what is actually
installed. It is what the panel opens with, and it can never show a conflict, because a lockfile only
exists once conflicts were already settled.

**Re-resolve** re-walks live git refs. Slower and network-bound, but the only mode that can discover
that your manifest currently has no solution, and the only one that can offer candidate versions to
settle it. Nothing is installed and no lockfile is written — resolving here is a question, not a
commitment.

## Re-importing after a command

The CLI writes to `addons/` as a child process, and Godot's file watcher does not notice out-of-band
writes until the editor window regains OS focus — which is why an install used to look like it did
nothing until you clicked into another window and back.

So any command that reports an actual change asks `EditorFileSystem` to rescan, once, as soon as the
editor is free. A command that reports only `unchanged` doesn't: `update` lists every addon it looked
at, and a full project scan per no-op press is its own kind of annoying.

## Layout

Every control is a scene under `ui/`, with its script bound to it — the panel, each dialog, the graph
node, and one scene per repeated row (`ui/items/`). The scripts hold data binding; the scenes hold
layout, so a spacing tweak is an editor change rather than a code review.

Scripts still build what the *data* decides. A graph node's status row depends on whether the addon
is conflicted, patched, linked, or renamed, and no scene can hold a skeleton for every combination.

| Path | What it is |
|---|---|
| `ui/dotquay_panel.tscn` | The main-screen tab: toolbar, graph, log, sidebar, and the dialogs |
| `ui/dependency_graph_view.tscn` | The `GraphEdit` the graph is drawn into |
| `ui/dependency_node.tscn` | One addon as a `GraphNode` |
| `ui/add_dependency_dialog.tscn` | The add form |
| `ui/install_preview_dialog.tscn` | The confirmation step, with the feed approvals |
| `ui/conflict_dialog.tscn` | The version picker |
| `ui/items/*.tscn` | Rows and cards instanced by the above |
| `core/dotquay_palette.gd` | The five status colours, in one place |

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
