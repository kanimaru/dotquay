# ADR / Design — v0.25: A reliable editor plugin, and updating addons that ship native code

**Status:** **Proposed, not implemented.** Written 2026-10-21 from a read of the code (no change was run).
**Deciders:** Kani

Two problems, one document, because the second is the hardest case of the first.

1. The editor plugin is "often unreliable". The cause is not one bug; section *Plugin findings* lists what
   the code shows and what still needs a reproduction.
2. An addon that ships a GDExtension cannot be updated from the editor, because the editor holds the DLL.
   Today the attempt can leave the addon folder half-deleted.

Facts below were read from code unless marked *(inferred)* or *(unverified)*.

## What happens today when an update meets a loaded DLL

- `SyncEngine.SwapAndHash` runs the link guard, the dirty check, then `IFileSystem.MoveDirectory(staging, dest)`.
- `SystemFileSystem.MoveDirectory` is `DeleteDirectory(destination)` and then `Directory.Move` (`IO/SystemFileSystem.cs:265-281`).
  Its comment calls the move atomic; the delete in front of it is not.
- `DeleteTree` deletes entry by entry and has no handling for a locked file. On Windows a loaded DLL cannot be
  deleted, so it throws part-way: the files enumerated before it are already gone, and `Directory.Move` is never reached.
  Godot loads a copy `~X.dll` beside the original (`GodotArtifacts.cs`, commit `b4dedd7`), so it is the copy that is locked,
  but it sits inside the folder being deleted.
- `UpdateCommand` catches only `WorkflowException` (`UpdateCommand.cs:80`) and `Program.cs` has no global catch, so an
  `IOException` escapes as a stack trace on stderr with empty stdout. The plugin then reports "no output" or `cli_bad_output`;
  the user never learns the file was locked. *(inferred from the catch sites, not run)*
- `AddonInstaller.InstallAsync` deletes the staging folder and rethrows, so the new version is lost as well, and the lockfile
  is written only after the whole graph sync, so addons swapped earlier in the same run are on disk but unrecorded.
- The next run finds a folder that no longer matches `content_hash` and refuses without `--force`.
- `SPEC.md:691-693` promises "The project folder is never left half-updated: all failures happen in staging." That does not
  hold for a failure in the delete step. This is a bug against the spec, independent of the editor.

Observed in the wild: an update with the BotV2 editor open left `GDMP` half-deleted (the reason for the standing rule "no dotquay while an editor runs").

## Decision

Three layers, each shippable on its own, in this order.

### A. Make the swap safe (core, no plugin involved)

The guarantee to restore: **after any failure, the addon folder is the old version or the new one, never a mixture.**

- **A1. Lock probe before touching anything.** Before the swap, try to open each file of the installed addon that can be held
  (at minimum `*.dll`, `*.so`, `*.dylib`, and every library a `.gdextension` file lists) for exclusive write. Any failure is a
  `LockedAddonException` naming the files. Nothing has been deleted at that point. Cheap, and it turns a corrupt tree into a refusal.
- **A2. Rename-aside swap.** Replace delete-then-move with: move `dest` to `.dotquay/trash/<name>-<timestamp>`, move staging to
  `dest`, and on failure move the old tree back. Delete the trash only after the lockfile is written. On Windows a directory that
  contains a mapped DLL usually cannot be renamed *(unverified)*, which makes this a second lock probe for free; the failure falls
  into A1's refusal.
- **A3. Surface it.** Catch `IOException`/`UnauthorizedAccessException` at the workflow boundary, map to `WorkflowException`
  with advice ("close the editor, or use the restart flow"), and add a global catch in `Program.cs` that emits a JSON envelope
  (`internal_error`) so no failure reaches the plugin as an empty stdout. Correct the SPEC §9 wording to what is now true.
- **A4. Record progress.** Either write the lockfile after each addon swap, or journal the run, so a failure in addon 3 of 5 does not
  leave 1 and 2 on disk but unrecorded. (Smallest correct change to be decided during implementation.)

### B. Deferred apply: stage now, swap when nothing holds the files

When A1 finds the addon locked, `update`/`install` does not fail. It finishes everything that does not touch `addons/`
(resolve, fetch, extract, re-apply patch, hash) and parks the result:

- `.dotquay/pending/<name>/` holds the staged tree, and `.dotquay/pending/pending.json` records name, from/to commit, expected
  `content_hash`, and patch info. The lockfile is **not** changed yet, so disk plus lockfile stay one consistent description.
- The JSON result carries `pending_restart: true` and the addon names. `status` reports a new kind (`pending_apply`).
  That is a wire change: `AddonStatusKind`/`dotquay_wire.gd` must change together.
- **`dotquay apply [--wait-for-pid <pid>] [--then <exe> <args...>]`** performs the parked swaps through the same `SwapAndHash`
  (dirty check intact), writes the lockfile, removes `.dotquay/pending/`, and with `--then` starts the given command detached.
  With `--wait-for-pid` it waits (bounded) for that process to exit first. It is idempotent and a no-op when nothing is pending.
- Any later `dotquay install|update|status` that finds pending work and nothing locked applies it first, so the CLI-only user
  who closes the editor and runs any command ends up consistent.
- On plain CLI use (no plugin), the default when locked is: stage, print "staged; close the editor and run `dotquay apply`",
  exit non-zero so scripts notice.

Why not an in-place hot swap. Windows lets you rename a loaded DLL even though you cannot delete it, so a per-file rename-aside
could replace files under a running editor *(unverified; ExecutableSwapper already does this for dotquay's own exe)*. But the
editor would still be running the old library, GDExtension hot reload is not dependable, and half-replaced native code is the
failure to avoid. A restart is needed regardless; deferring the swap to just after exit is the simpler correct sequence.

### C. The editor restart flow (plugin)

GDExtensions load before plugins run, so a plugin cannot intercept the next startup; an external helper has to do the swap
between exit and relaunch. `EditorInterface.restart_editor()` relaunches immediately and races the swap, so it is not used.

On a result with `pending_restart`:

1. The panel shows "Restart the editor to finish updating `<addon>` (it contains native code)", with *Restart now* and *Later*.
   "Later" leaves the addon in the `pending_apply` state in the graph.
2. *Restart now*: `EditorInterface.save_all_scenes()`, then
   `OS.create_process(cli, ["apply", "--wait-for-pid", OS.get_process_id(), "--then", OS.get_executable_path(), "--editor", "--path", <project>])`,
   then `get_tree().quit()`.
3. The helper waits for exit, applies, relaunches the editor on the same project.

To verify before building: that a process started with `OS.create_process` outlives the editor on Windows (job-object behaviour),
and the exact editor relaunch arguments for `--editor --path` from a project launched through the project manager.
Failure of `apply` after the editor is gone must not be silent: it writes `.dotquay/apply.log` and the next editor start's
panel shows it.

## Plugin findings (reliability)

Read from code; ordered by how likely they are to cause what the user sees. The review `REVIEW-godot-plugin-public-release.md`
has every box ticked, so these are all *new* relative to it, and its closing prose says the C-items were "not started" while
their boxes are ticked, so treat those as unverified.

| # | Finding | Evidence | Proposed fix |
|---|---|---|---|
| 1 | The plugin kills running children when it exits the tree, including a mutating `update`. A kill between delete and move produces the half-deleted addon. | `dotquay_cli.gd:50-65` (`OS.kill` in `_exit_tree`) | Do not kill a mutating command; let it finish or refuse to unload. Kill only read-only commands. A2 also limits the damage. |
| 2 | Any non-`WorkflowException` becomes an empty-stdout failure; the plugin cannot show the reason. | `UpdateCommand.cs:80`, no catch in `Program.cs` | A3: global catch emitting an envelope. |
| 3 | A failed mutation never rescans the disk, so the FileSystem dock and extension state go stale after a partial write. | `dotquay_panel.gd` success-only `_request_rescan` *(line numbers per survey, not re-read)* | Rescan after every mutation, success or not. |
| 4 | Blocking `OS.execute("where"/"which")` on the main thread whenever no CLI path is configured, on every refresh. | `dotquay_settings.gd:155-165` | Resolve once in a thread, cache, invalidate on setting change. |
| 5 | No cancel, and `OS.kill` ends only dotquay; child `git`/`dotnet` can outlive it and keep locks *(inferred)*. | `dotquay_process_runner.gd` | Cancel button; kill the process tree (`taskkill /T`, `kill -- -pgid`). |
| 6 | stdout and stderr are joined into one string before parsing, so a banner or warning with a `{` before the JSON can mis-extract *(unverified, runner not re-read)*. | `dotquay_process_runner.gd:~107`, `dotquay_result.gd` | Parse stdout only; keep stderr for diagnostics. |
| 7 | Only `schema == 1` is checked. A CLI that lacks a feature the plugin calls (the new `apply`) fails opaquely. | `dotquay_result.gd` | `capabilities` list in the envelope; the plugin gates features on it and says "update dotquay". |
| 8 | Pipe handles are never closed and the child is never waited on *(inferred)*; pending tickets can leak if a run never returns. | `dotquay_process_runner.gd`, `dotquay_panel.gd` | Close pipes in `finally`; time out tickets. |
| 9 | No automated coverage of `DotquayCli`, the panel, or `plugin.gd` (no injection seam). | review doc, section T | Make the CLI injectable; add a fake-CLI script that replays recorded envelopes. |

**What this cannot explain.** "Often unreliable" has no reproduction in hand. Before the reliability work is scoped further, the
plugin should write `.dotquay/plugin.log` (argv, duration, exit code, first 2 KB of output, per invocation) so the next failure
leaves evidence. That log is cheap and goes in the first plugin release of this design.

## Alternatives considered

- **Refuse to update native addons from the editor.** Honest, ten lines, and what the A-layer gives anyway. Rejected as the end
  state: the update flow is the reason to have a panel.
- **Hot swap by renaming locked files.** See B. Rejected for the reason stated there; kept as a possible optimisation for non-native files.
- **Exit and let the user rerun the CLI by hand.** What the CLI-only path of B does. Rejected for the plugin: asking the user to
  remember a command after closing the editor is the failure this design exists to remove.
- **Ship GDExtension updates only with an editor-side updater inside the extension.** Out of scope; the extension is a third party's.

## Phasing

| Phase | Content | Verifiable by |
|---|---|---|
| 1 | A1-A4: lock probe, rename-aside swap, global catch, SPEC §9 fix | Unit tests with a file system fake that can hold files; then the real binary against GDMP with the editor open, expecting a refusal and an untouched tree |
| 2 | B: pending state, `apply`, `status` kind, JSON fields, `capabilities` | Unit tests; real binary: stage while locked, close editor, `apply`; kill `apply` mid-way and rerun |
| 3 | C: plugin restart flow, wire update, `plugin.log` | GDScript suite plus a live run: update GDMP from the panel, restart, confirm new DLL loaded |
| 4 | Findings 1, 3-9 | Per finding; items 5 and 9 are the larger ones |

Every phase needs the real-binary run: this repo's standing rule is that a green suite does not prove a NativeAOT binary or an
editor interaction works. Each wire change ships CLI and `dotquay_wire.gd` in the same change.

## Open questions for Kani

1. What does "unreliable" look like when it happens: the panel stuck busy, stale graph, wrong badges, errors, a crash? That
   decides whether phase 4 starts with findings 3/6/8 or 1/5.
2. On plain CLI use, should a locked update exit non-zero after staging (proposed) or succeed with a notice?
3. Is Linux/macOS editor use in scope for phase 3? Their loaders let an in-use library be replaced, so A/B degrade to no-ops there,
   but the restart prompt is still wanted.
