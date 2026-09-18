# DESIGN v0.19 — NuGet packages in the dependency graph

Status: proposed, not implemented. Direction agreed with kani on 2026-09-18: packages move into the
graph view, and the plugin's separate NuGet sidebar tab goes away. Phase 1 (§8) is the next thing
to build.

## The problem

The Godot plugin shows addons and NuGet packages in two different places. Addons are nodes in the
main-screen graph. Packages are rows in a `NuGet` sidebar tab, which is a separate list with its own
row scene (`dotnet_row.tscn`) and one action (Remove).

That split hides the one thing a package list most needs to answer. Since v0.4 (P1-5), an addon's
package manifest can declare `[dotnet.*]` entries that the consuming project inherits. In the
sidebar, a package brought in that way is greyed out and says *"Needed by an addon"*, but it never
says **which** addon. The data to answer that exists in Core (the resolver's walk records
`DotnetPackagesByAddon`, and doctor re-reads installed package manifests), but it never reaches the
wire. `graph --json` reduces it to one boolean, `declared_by_project`.

A graph edge is the natural answer to "which addon", and edges are what the graph view is for. So
the fix is to put packages in the graph as their own kind of node, with actions that make sense for
NuGet, and delete the second display.

## 1. Decisions

| Question | Decision | Why |
|---|---|---|
| One view or two | One. Packages are graph nodes; the NuGet sidebar tab is removed. | Two displays of one dependency set drift apart, and only the graph can show *why* a package is there. |
| What gets drawn | The project's own `[dotnet.*]` entries, addon-declared ones, and lockfile-only leftovers. **Never** NuGet's own transitive package tree. | dotquay delegates that tree to `dotnet restore` (SPEC.md §11). Drawing it would suggest dotquay manages it, and it would swamp the canvas. |
| Where the edges come from | A new `required_by` field on each `dotnet[]` entry in `graph --json`. The plugin draws addon→package edges from it. | The plugin holds no resolution logic (SPEC.md §18). "Who declared this package" is a Core fact and belongs on the wire. |
| Addon `edges[]` | Unchanged. Package edges are **not** mixed into it. | An addon and a package can share a name (addon `gut`, package `Gut`), and existing consumers (MCP, scripts) read `edges` as addon→addon. Keeping the two namespaces apart avoids both problems. |
| Layout | Packages go in their own column to the right of the deepest addon column, sorted by name. | Packages are always sinks. A fixed column keeps them from interleaving with addons of the same depth, and the layout stays stable across refreshes. |
| Crowding | A toolbar filter: **All / Addons / NuGet**. | A project with many root-declared packages and few addons would otherwise bury the addons under a column of unconnected nodes. The filter covers what the old tab's flat list was good for. |
| Actions | Per kind, from a pure-data action table (§5). Git-only actions never appear on a package. | Greyed-out "Open repository" on a package (today's tooltip: *"it was likely declared as a NuGet package"*) is the symptom of one menu serving two kinds. |

## 2. Non-goals

- **No NuGet transitive graph.** See §1.
- **No package overrides.** `[overrides]` is git-only (`OverrideWorkflow` needs a version, branch,
  or rev on an addon). NuGet's equivalent already exists: a project-level `[dotnet."X"]` always
  wins over an addon-declared one (P1-5). The action that exposes it is Phase 2 (§8).
- **No `.csproj` health state on package nodes.** "Declared but missing from the `.csproj`" is a
  doctor question; it's not a reason to add a status evaluator for packages here.
- **No schema bump.** Every wire change is additive (SPEC.md §17: an older plugin ignores unknown
  keys, and a newer plugin falls back when a key is absent).

## 3. Wire contract (`graph --json`, `data.dotnet[]`)

Existing fields are kept as they are. The new ones are `origin`, `required_by` and `source`:

```json
{
  "name": "VBeami.Core",
  "constraint": null,
  "resolved": "1.4.2",
  "declared_by_project": false,
  "origin": "transitive",
  "required_by": ["vbeami"],
  "source": "kemomi"
}
```

- **`origin`** — `root` | `transitive` | `orphan`, the same strings and meaning as for addons:
  - `root`: the project's own `[dotnet.*]` declares it. An addon may *also* declare it, and then
    both show up in `required_by`.
  - `transitive`: only reachable addons declare it.
  - `orphan`: it's in the lockfile, but neither the project nor any reachable addon declares it.
- **`required_by`** — who declared it. `"<project>"` (`GraphConflict.ProjectRequirerName`, the same
  sentinel addon requirements use) comes first when the project declares it, followed by addon
  names in ordinal order. It's empty for an orphan.
- **`source`** — the `[nuget.sources.*]` name recorded in the lockfile, or `null` for nuget.org.
  The plugin needs it to decide whether "Open on nuget.org" is honest (§5).
- `declared_by_project` stays as-is (`origin == root`), for older plugins.

`required_by` is filled in **both** graph modes:

| Mode | Source of addon declarations |
|---|---|
| lockfile (default, offline) | Each *reachable* locked addon's installed package manifest, read from `InstallPaths.For(...)`, using the same rule `doctor`'s dangling-entry check uses. |
| `--resolve` | `GraphAnalysis.DotnetPackagesByAddon` from the live walk. This includes every declarer, even ones whose version range lost the first-discovered-wins tie. |

Today the lockfile mode passes `addonDeclared: []`, so an addon-declared package in the default view
is indistinguishable from a leftover. That is the actual bug behind the unhelpful "Needed by an
addon" text.

### 3.1 One reader, shared

The "read an installed addon's package manifest" code is currently a private method on
`DependencyWorkflow` (`TryReadInstalledPackageManifest`). It moves into its own Core type,
`InstalledPackageManifests` (`Dotquay.Core.Sync`), with a single method:

```csharp
IReadOnlyDictionary<string, IReadOnlyList<string>> DotnetDeclarationsByAddon(
    string projectDirectory, IEnumerable<LockedAddon> addons);
```

`GraphInspector`, doctor's dangling-entry check, and `RemoveAsync` (§4) all call it. Three copies
of "where does an addon's manifest live" is exactly how `InstallPaths` came to exist. A malformed
package manifest reads as "declares nothing", as it does today; reporting that is
`CheckPackageManifestReadable`'s job.

## 4. A Core bug this surfaces: removing an orphan package

`doctor` tells the user to run `dotquay remove X` for a dangling NuGet lockfile entry. But
`RemoveAsync` only accepts names in the manifest, so the command it suggests fails with
*"'X' is not declared in dotquay.toml"*. The graph would inherit the same dead end on an orphan
package node.

Fix: `RemoveAsync` also accepts a name that is **only** in the lockfile's `[[dotnet]]` and that no
reachable addon declares. In that case it drops the lock entry and removes the `PackageReference`
through `IDotnetInstaller.Remove`, which is the same second half the manifest branch already runs.
If a reachable addon *does* declare it, the call is refused with a message that names the addon,
because removing the reference would only bring it back on the next update.

## 5. Actions

For each node kind, the action menu comes from `DotquayNodeActions` (plugin `core/`). It is a
static function of plain data that returns a list of entries, each with a label, an action id, a
disabled flag, and a reason. The addon menu moves into the same table. That way every "why is this
greyed out" rule for both kinds can be tested without creating a `GraphNode`, following the pattern
of the existing `update_disabled_reason` statics (P1-19).

Package menu:

| Action | `root` | `transitive` | `orphan` |
|---|---|---|---|
| Update (`dotquay update <id>`) | enabled | disabled: *"Brought in by X; update that instead."* (Updating an addon re-resolves the packages it declares, see `UpdateAsync`.) | disabled: *"Nothing declares this any more. Remove it, or run Update all."* |
| Open on nuget.org | enabled when `source` is null | same | same |
| Remove (`dotquay remove <id>`) | enabled | disabled: *"Declared by X, not by your dotquay.toml."* | enabled (§4) |

When "Open on nuget.org" is disabled, the reason names the private feed: *"Resolved from the
'kemomi' feed, which has no public package page."* The URL is
`https://www.nuget.org/packages/<id>/<resolved>` (or without the version when nothing is resolved).

Badges on a package node are `nuget` (the kind, always shown), `orphan`, and a `needed by N: …`
detail line when `required_by` has more than one entry. Addon-only badges (modified, patched,
linked, `→ folder`) never apply.

## 6. Plugin structure

```
core/dotquay_graph.gd          PackageNode (replaces DotnetEntry): origin, required_by, source,
                               is_removable/is_orphan/is_transitive, nuget_url().
                               AddonNode gains `packages` — the reverse of required_by, filled
                               in by DotquayGraph.from_payload — so an addon node knows it has a
                               right-hand slot even when it has no addon children.
                               package_column().
core/dotquay_graph_filter.gd   ALL / ADDONS / PACKAGES, and which node kinds each one shows.
core/dotquay_node_actions.gd   The action table (§5) for both kinds. Pure data.
core/dotquay_palette.gd        + PACKAGE colour.
ui/package_node.tscn/.gd       A sibling of dependency_node, not a mode of it. It has its own
                               status row, details and menu, plus a package icon in the scene.
ui/dependency_graph_view.gd    Lays out packages in their column, draws addon→package
                               connections, and adds the filter to the toolbar (next to
                               Auto-arrange, for the same reason that button is built in code).
ui/dotquay_panel.*             The NuGet tab, DotnetList and _render_dotnet are removed.
ui/items/dotnet_row.*          Deleted.
icons/package.svg              A generic package glyph (not the NuGet logo).
```

**Node identity.** `GraphEdit` node names and the view's `_nodes_by_name` are keyed by kind:
`addon:<name>` and `package:<id>`. Otherwise an addon and a package with the same name would
silently overwrite each other's connection endpoints. Remembered drag positions (P1-22) use the same
keys.

**Action routing.** Both node kinds emit `action_requested(action, name)`. The panel's
`_on_node_action` gains `MENU_OPEN_NUGET`. Update and Remove go through the existing handlers,
because `update`/`remove` already take either kind of name.

## 7. Tests

C# (`tests/Dotquay.Tests`, fakes only):

- `GraphInspector` lockfile mode: a project-declared package is `root` with `["<project>"]`; one
  declared by an installed, reachable addon is `transitive` with that addon's name; one declared by
  both is `root` with both names; a lockfile-only one is `orphan` with `[]`; an addon that is locked
  but unreachable does not count as a declarer; a malformed package manifest counts as declaring
  nothing; `source` comes through from the lockfile.
- `GraphInspector` resolved mode: `required_by` comes from `DotnetPackagesByAddon`, including a
  declarer whose range lost the tie.
- `InstalledPackageManifests` on its own: follows `install_as` (`EffectiveFolder`), missing
  manifest, malformed manifest.
- `RemoveAsync`: an orphan package is removed from the lockfile and the `.csproj`; a lockfile-only
  package that a reachable addon still declares is refused, and the message names that addon.
- JSON: the new keys are emitted, with `source` as `null` rather than omitted.

GDScript (`run_tests.ps1`):

- `DotquayGraph`: parses the new fields, falls back correctly on a pre-v0.19 payload (no `origin` →
  derived from `declared_by_project`), builds the reverse `packages` index, and puts
  `package_column()` right of the deepest addon column.
- `DotquayNodeActions`: every cell of §5's table, plus the addon menu cases already covered for
  `dependency_node`.
- `DotquayGraphFilter`: which kinds each mode shows.
- View: an addon and a package with the same name both render and connect; the filter hides and
  shows packages; addon→package connections exist.
- `test_scenes.gd` picks up `package_node.tscn` automatically.
- The live contract suite checks that `graph --json` really emits `origin`/`required_by`/`source`.

Real-binary check (per the verification discipline): a NativeAOT publish, `graph --json` against a
project with a root package, an addon-declared one and an orphan, with the document inspected. Also
`remove <orphan-id>` for real, followed by a look at the `.csproj`.

## 8. Phasing

| Phase | Scope | Status |
|---|---|---|
| 1 | §3 wire fields in both modes, §3.1 shared reader, §4 orphan removal, §5 actions (Update / Open on nuget.org / Remove), §6 package nodes + filter, NuGet tab removed. | proposed |
| 2 | "Pin a version in my project…" on a `transitive` package: a version dialog that runs `dotquay add <id> --version <v>`, which makes it `root` and lets the project's entry win (P1-5). On a `root` package, a "Change version…" action needs `add` to accept an already-declared ID (today it refuses), so that is a Core change of its own. | proposed |
| 3 | Optional: a per-node "outdated" badge from `outdated --json` for both kinds. | idea |
