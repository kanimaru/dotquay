# Godot plugin — public-release review (newbie lens)

Date: 2026-09-15. Scope: `src/Dotquay.Godot/` (the `addons/dotquay/` plugin, its tests, `run_tests.ps1`,
the plugin README). Reviewed as a first-time Godot user who wants to add, update and remove a couple
of addons and knows nothing about lockfiles, git refs, NuGet feeds, patches or overrides.

Every item names the file and line it was verified against. Items are grouped by priority; inside a
group the order is roughly "how often a newbie hits it". Tick them off as they land.

**Update 2026-09-15:** first implementation pass landed (9 commits, `10636ca`..`f169bbf`), verified
against the real Godot 4.7.2 binary and the real dotquay CLI (468/468 assertions passing, including
the live CLI contract suite). See the ticked items and inline progress notes below for exactly what
shipped versus what's still open. Nothing was marked done without being re-read from the committed
file first.

Things checked that turned out fine: no `dependi` leftovers anywhere in the plugin tree; `init --json`
does emit a proper envelope (`InitCommand.cs:96-112`), so the "Create dotquay.toml" button works — the
comment in `tests/test_cli_contract.gd:54-55` claiming otherwise is stale (see C-12); the
`DotquayRescan` / `DotquayRefreshTracker` state machines are correct; `plugin.gd` lifecycle ordering is
correct; `to_args()` passes values as separate argv elements, so spaces are safe on Windows.

---

## P0 — Must fix before a public release

These either show wrong information, lose data, or leave a beginner stuck with no way forward.

- [x] **P0-1 Fake summary on first run.** ~~`ui/dotquay_panel.tscn:71` ships
  `text = "  1 addon(s) · 1 linked  (from lockfile — press Re-resolve to check for conflicts)"` and
  `_update_summary()` only runs from `_apply_graph()` (`dotquay_panel.gd:544-549`). With no CLI, no
  `dotquay.toml`, or any failed `graph`, the toolbar claims one linked addon exists.~~ Fixed: the scene
  default is now `""`, cleared explicitly in `_ready()`, and only ever set from a real `_apply_graph()`.

- [x] **P0-2 First-run banner does not say where to get dotquay.** ~~`core/dotquay_cli.gd:67`:
  "No dotquay executable configured or found on PATH. Set Editor Settings -> Dotquay -> CLI Path."
  The only button, "Find dotquay on PATH" (`dotquay_panel.tscn:103`), re-runs the same `where`/`which`
  that already failed (`dotquay_settings.gd:57-64` ran it before producing this error).~~ Fixed: added
  a **Get dotquay…** button (`OS.shell_open` to the release page) and a **Browse…** button
  (`EditorFileDialog`, writing straight to `cli_path`), alongside the existing "Find on PATH" retry.
  The plain-English sentence explaining what dotquay even is now shows for the not-configured case
  specifically, replacing the raw setting-name error text there (the other cli-unusable kinds still
  show the CLI's own real message, per P0-4).
  _Which buttons a given failure kind offers moved into a new pure function,
  `DotquaySetupBanner.decide()` (not_configured gets all three; a wrong path/bad output gets Browse
  and Find on PATH but not Get dotquay, since something is already at that path; a schema mismatch
  gets none of the three, since both would just re-find the same differently-versioned binary),
  covered by `tests/test_dotquay_setup_banner.gd` with no Control/EditorSettings/CLI needed. One
  finding along the way: `EditorFileDialog` engine-refuses instantiation as part of a hand-authored
  `.tscn` outside a real editor session, which `tests/test_scenes.gd`'s headless scene-loading caught
  immediately — built in code in `_ready()` instead, which those tests never reach. All 511 assertions
  pass against the real Godot 4.7.2 binary and the real dotquay CLI (`75a80ba`)._

- [x] **P0-3 PATH discovery fails for the recommended install on macOS/Linux.**
  `core/dotquay_settings.gd:69-84` only asks `which dotquay` with the editor's own environment. Godot
  started from Finder/Dock/a `.desktop` launcher has the minimal login PATH, which does not include
  `~/.local/bin` — where `scripts/install.sh` and `self update` put the binary (repo `README.md:196`).
  Also `which` is absent on some minimal distros. Fix: walk `OS.get_environment("PATH")` yourself with
  `FileAccess.file_exists`, then probe `~/.local/bin/dotquay`, `~/.dotnet/tools/dotquay`,
  `/usr/local/bin`, `/opt/homebrew/bin`, `%LOCALAPPDATA%`. Make `EXECUTABLE_NAMES` (`:22`)
  platform-specific — `which dotquay.exe` on Unix is a guaranteed-miss extra process on every CLI call
  while unconfigured.
  Fixed: `discover_on_path()` now also probes `~/.local/bin`, `~/.dotnet/tools`, `/usr/local/bin`,
  `/opt/homebrew/bin` (macOS), and `%LOCALAPPDATA%\dotquay` / `%LOCALAPPDATA%\Programs\dotquay`
  (Windows) after a plain PATH lookup fails, and `EXECUTABLE_NAMES` is now platform-specific.

- [x] **P0-4 Wrong-path / launch failures never reach the banner, and the real reason is thrown away.**
  ~~`_update_setup_banner` (`dotquay_panel.gd:353-359`) only handles `KIND_NOT_CONFIGURED` and
  `KIND_NO_MANIFEST`. `KIND_BAD_OUTPUT`, `KIND_LAUNCH_FAILED`, `KIND_SCHEMA_MISMATCH`,
  `KIND_WRONG_TOOL` are one red line in the 140 px log while graph and sidebar stay blank.
  `DotquayResult.raw_output` (`dotquay_result.gd:40`) is assigned at `:103,:114,:123` and **never
  read** — its own doc comment (`:37-39`, "The panel shows it when parsing failed") is false.~~ Fixed
  in two passes: `raw_output` was folded into `error_message` earlier (so the log line already carried
  it); `_update_setup_banner` now also routes `KIND_LAUNCH_FAILED`, `KIND_BAD_OUTPUT` (this build has
  no separate `KIND_WRONG_TOOL` — "not dotquay" is one of the `KIND_BAD_OUTPUT` cases already), and
  `KIND_SCHEMA_MISMATCH` into the banner with the real message. "Find dotquay on PATH" is offered as a
  retry for the two that plausibly mean a wrong path; skipped for a schema mismatch, since re-running
  discovery would likely find the same differently-versioned binary. No **Open Editor Settings** /
  **Browse…** button was added — that's P0-2's ask, still open below.

- [x] **P0-5 Windows: crash exit codes are misreported as "could not start".** ~~`core/dotquay_cli.gd:112`:
  `if code < 0:` → `KIND_LAUNCH_FAILED` and the captured output is
  ignored. Godot stores the process's DWORD exit code into an `int`, so any code ≥ `0x80000000`
  (.NET fail-fast / `STATUS_*` codes) arrives negative and a CLI crash with a full trace on stderr is
  reported as "Could not start dotquay at C:\…\dotquay.exe".~~ Fixed: checks `code == -1` only, and
  always attaches the captured output via the 4-arg `failure()`.

- [x] **P0-6 Mutating commands are not serialised; most entry points ignore the busy state.**
  `DotquayCli.run()` starts a thread per call with no queue (`dotquay_cli.gd:87-89`); `is_busy()`
  (`:46`) has no callers. `_on_busy_changed` (`dotquay_panel.gd:369-374`) disables only the
  `%Toolbar` buttons. Still live while a command runs: every GraphNode **Actions** menu
  (`dependency_node.gd:129-154` → update / remove / override / patch), the conflict card's
  **Resolve…** and the conflict dialog's OK, **ConfirmRemove** OK, the explain dialog's **Force
  update**, the preview dialog's **Install**, the NuGet row **Remove**, **Locate**/**Init**, and
  **Update dotquay** (which replaces the binary another run may be executing). Press **Install**, then
  Actions → **Update** on a node: two processes rewrite `dotquay.lock` and `addons/` at once. Fix:
  queue runs inside `DotquayCli` (one worker at a time, still async), and disable graph/sidebar/dialog
  buttons on `busy_changed`.
  Fixed at the root: `DotquayCli.run()` now queues any call made while one is already in flight and
  runs them strictly in order, so two mutating commands can no longer touch `dotquay.lock`/`addons/`
  concurrently regardless of which UI surface fired them. Toolbar buttons are also gated on the new
  `busy_changed`-driven `_is_busy` state. Still open: node menus, dialog OK buttons and the NuGet row
  Remove button aren't individually greyed out while a run is queued — clicking them still works, it
  just waits its turn safely instead of racing.

- [x] **P0-7 No timeout; `_exit_tree` joins unconditionally, so a stalled child hangs the editor —
  including on quit.** ~~`dotquay_cli.gd:35-43` + the retired timeout setting
  (`dotquay_settings.gd:15-18`, "OS.execute has no timeout to give it to"). An ssh host-key prompt on a
  first `git@host:` clone, or a half-open connection to a feed, leaves the toolbar disabled forever and
  blocks Godot's shutdown until the user force-kills it — which is exactly the mid-write corruption the
  join was meant to prevent.~~ Fixed: added `DotquayProcessRunner`, which wraps `OS.execute_with_pipe()`
  so a worker thread polls wall-clock time against a new editor setting (`Command Timeout Seconds`,
  `dotquay_settings.gd`, default 300s) and kills the child once the budget runs out, instead of being
  stuck inside one blocking `OS.execute()` call with no way to interrupt it. `_exit_tree` now tracks
  every running child's pid (`Mutex` + `Dictionary`, written by the worker thread the moment a pid
  exists) and kills them directly before joining, so shutdown is bounded by one poll interval instead of
  the full configurable timeout.
  _Verified with a new suite, `tests/test_dotquay_process_runner.gd`, that spawns real OS processes: a
  missing executable, a fast process's real exit code and output, and a process genuinely killed after
  outliving a 500ms budget. All 479 assertions pass against the real Godot 4.7.2 binary and the real
  dotquay CLI (`d961a06`). Getting the implementation right took one throwaway headless probe first:
  reading the child's output pipes concurrently with the timeout-polling loop silently returns empty
  output on Windows, regardless of what the child actually wrote, confirmed against both `powershell.exe`
  and the real `dotquay.exe` binary — so `DotquayProcessRunner` deliberately reads the pipes only after
  `OS.is_process_running()` has already gone false. Still unverified: the Linux/macOS `/bin/sh` branch
  (cross-platform by construction, never actually run), and manually clicking through a real hang inside
  the GUI editor itself — headless `--script` mode proves the timeout/kill mechanism but not the editor's
  own quit sequence._

- [x] **P0-8 A `null` list anywhere in an envelope is a script error on the worker thread → panel
  stuck busy forever.** ~~`dotquay_result.gd:126` `for warning in envelope.get("warnings", [])` only
  defaults when the key is *absent*; SPEC §17 says optional fields are emitted as `null`. The same
  `for x in payload.get(key, [])` pattern is in `dotquay_graph.gd:48,51,128,173,177,256-274`,
  `dotquay_preview.gd:40,114,117,152-164`, `dotquay_explain.gd:149,217`, `dotquay_panel.gd:512,661`.~~
  Fixed: `DotquayWire.array_or_empty()` was added and applied throughout `dotquay_result.gd`,
  `dotquay_graph.gd`, `dotquay_preview.gd`, and `dotquay_explain.gd`, with fixture tests for every null
  site. On re-check, `dotquay_panel.gd`'s doctor/outdated sites (`_dictionaries_in`) turned out to
  already guard against null via an `is Array` check — only `_report_operation`'s
  `payload.get("messages", [])` was a genuine gap, now fixed the same way (no fixture test added for
  it; C-24 already tracks the lack of a typed parser/fixture suite for this raw-payload reporting
  path). `_run_blocking` was never actually at risk: `RESULT.parse()` has no path that returns null —
  every branch returns a `DotquayResult`, a failure one if parsing went wrong — so `_finish` was always
  unconditional; the review's stated risk here didn't hold up on a closer read.

- [x] **P0-9 Every toolbar button stays enabled with no `dotquay.toml`.** ~~With `no_manifest`, **Add…**
  opens the full form, the user fills it in, presses Add, the dialog closes and clears
  (`add_dependency_dialog.gd:46-58`), and the CLI's no-manifest error lands in the log — form
  contents gone. Same for Install / Update all / Check for updates / Re-resolve / Doctor.~~ Fixed: a
  `_project_ready` flag tracks the last `graph` result and `_update_toolbar_state()` disables every
  mutating button but Refresh until it's true (and while the setup banner is showing).

- [x] **P0-10 `plugin.cfg` version is `0.9.0`** ~~(`plugin.cfg:6`) while the code ships v0.12 (explain
  dialog) and v0.17 (self-update banner). `doctor` checks plugin.cfg drift, so this is user-visible.~~
  Fixed: bumped to `0.17.0`. Still worth automating from the CLI tag in CI at some point so it can't
  drift again.

- [x] **P0-11 SPEC references leak into user-facing text.** ~~`dependency_node.gd:84` "(SPEC.md P0-6)",
  `:93` "(SPEC.md §10)", `dotquay_panel.gd:302` "(SPEC.md §8)", `items/dotnet_row.gd:31`
  "(SPEC.md P1-5)"~~, plugin `README.md:8,31,32,33,65`. A plugin user has no SPEC.md. Fixed in all four
  GDScript sites. Still open: the plugin README's own SPEC.md references (a docs-only fix, not
  attempted this pass).

---

## P1 — Should fix: the newbie journey

### First run / setup

- [x] **P1-1** ~~Settings breadcrumb is wrong everywhere it is written. The key is
  `dotquay/general/cli_path` (`dotquay_settings.gd:12`), which Godot renders as **Dotquay › General ›
  Cli Path**; the strings say "Editor Settings -> Dotquay -> CLI Path" (`plugin.cfg:4`,
  `dotquay_cli.gd:67`, `dotquay_panel.gd:339`, `README.md:18`).~~ Fixed: added
  `DotquaySettings.breadcrumb()`, which derives the real breadcrumb from a setting key the same way
  Godot's own dialog does (title-case each `/`-delimited segment) — the two runtime strings
  (`dotquay_cli.gd`) now read from it instead of a hand-typed guess. `plugin.cfg` and `README.md` are
  plain text, not GDScript, so those two carry a hand-corrected copy instead. Not done: renaming the
  key itself to `executable_path` — left as explicitly optional ("consider") in the original wording,
  and not worth the migration risk for a cosmetic rename.
- [x] **P1-2** ~~Common path-setting mistakes get "does not exist" with no hint (`dotquay_cli.gd:71-76`,
  `dotquay_settings.gd:58-62` only `strip_edges()`): Windows "Copy as path" gives a quoted string,
  `~/.local/bin/dotquay` is not expanded, a folder or `dotquay.dll` is accepted.~~ Quote-stripping and
  `~` expansion were already handled by `_normalize_configured_path`/`_expand_home` from an earlier
  pass (not this one). Fixed the remaining gap: `DotquaySettings.path_problem()` now checks for a
  folder or a `.dll` specifically and returns a ready-to-show sentence naming which one it is, in
  addition to plain nonexistence.
- [x] **P1-3** ~~Linux/macOS: `FileAccess.file_exists` does not check the execute bit; a binary without
  `chmod +x` yields "Could not start dotquay" (`dotquay_cli.gd:115`) with no hint.~~ Fixed:
  `DotquayProcessRunner.launch_failed_message()` adds a `chmod +x` hint whenever `OS.get_name() !=
  "Windows"`.
- [x] **P1-4** ~~"Find dotquay on PATH" gives no feedback in the banner itself (`dotquay_panel.gd:336-344`,
  log only), and silently overwrites a deliberately configured-but-wrong path (`:342`).~~ Fixed:
  `_setup_message.text` now updates directly on both outcomes, and a new `ConfirmOverwriteCliPath`
  dialog asks first whenever a different, non-blank path was already configured (a blank one is never
  worth confirming — there's nothing to lose).
  _P1-1..P1-4 covered by `tests/test_dotquay_settings.gd` (new) and two added cases in
  `tests/test_dotquay_process_runner.gd`. 522 assertions pass against the real Godot 4.7.2 binary and
  the real dotquay CLI (`0ab2391`)._
- [x] **P1-5** ~~The no-manifest banner and button are jargon: "Scaffold a manifest in this Godot
  project, the same as running `dotquay init`." (`dotquay_panel.tscn:109`, backticks render
  literally). Suggest banner "dotquay keeps the list of your addons in a file called dotquay.toml.
  This project doesn't have one yet." / button "Set up this project". Also skip the duplicate red
  CLI error line for the two banner-handled kinds (`dotquay_panel.gd:393-399`).~~ Fixed:
  `DotquaySetupBanner.NO_MANIFEST_MESSAGE` now names dotquay.toml in plain language instead of
  "manifest"; the Create-dotquay.toml button's tooltip dropped its backtick-rendered `dotquay init`
  jargon too. The button's own label was already "Create dotquay.toml" (clearer than the suggested
  "Set up this project", which names nothing concrete), so it was left as-is. `_update_setup_banner`
  now returns its decision so `_on_cli_finished` can skip `_log_error` whenever the banner already
  shows the same message — closing the duplicate-red-line half of this item for every banner-handled
  kind, not just no-manifest.
- [ ] **P1-6** Sidebar tabs are blank before the first graph — `_render_conflicts`/`_render_dotnet`
  run only from `_apply_graph` (`dotquay_panel.gd:593-631`). Render the empty-state labels in `_ready`.
- [ ] **P1-7** `_self_checked` is set to `true` before the run and even when the CLI is not configured
  (`dotquay_panel.gd:151-156`); the deferred `KIND_NOT_CONFIGURED` failure hides the banner
  (`:495-497`). A user who opens the tab before configuring the path never gets an update notice
  that session. Reset the flag on `KIND_NOT_CONFIGURED`/`KIND_LAUNCH_FAILED`. Related: a failed
  self-update leaves `_update_message.text = "updating dotquay..."` (`:530`) while re-enabling the
  button (`:510-519`) — restore the message.

### Add dialog

- [x] **P1-8** ~~No input-shape validation; the common pastes fail *after* the dialog closed and wiped
  itself (`dotquay_add_request.gd:49-61,67-74`, `add_dependency_dialog.gd:143,194-202` — `is_complete`
  only checks non-empty).~~ Fixed: new pure `DotquayAddRequest.diagnose_source_shape()` parses a
  GitHub/GitLab "tree" browser URL into repo URL + branch + subtree and the dialog writes all three
  fields back automatically; a bare `owner/repo` shorthand is rejected with a reason (guessing a host
  would be worse than asking) rather than expanded; a `.git` suffix is acknowledged as already correct.
  The `%Hint` label shows the diagnosis live, and the OK button's tooltip names whatever's still missing
  while it's disabled.
- [x] **P1-9** ~~On a failed `add` / `add --preview`, re-open the dialog with the previous values and
  show the error inside it (a Label above the buttons) instead of only in the log.~~ Fixed: a new
  `%Error` label sits above the buttons, and `DotquayPanel` now tracks the request behind every
  add-family ticket (not just the preview path) so `reopen_after_failure()` can restore it, along with
  the CLI's own error message, the instant an `add` or `add --preview` fails.
- [ ] **P1-10** Field labels/placeholders: "Subtree" (`add_dependency_dialog.tscn:91`) is git jargon and
  its placeholder (`:97`, ~85 chars) is cut mid-sentence in the ~510 px field; same for "Install as"
  (`:113`, ~75 chars). Suggest "Folder in repo" / "Install to", short placeholders, and tooltips on the
  five row labels (none have any). "Pin to" (`:71`) whose first option is "Latest release" isn't a pin
  — "Which version?" with "Newest release (recommended)", "A specific version…", "Follow a branch…",
  "An exact commit…".
- [ ] **P1-11** Source kind labels mislead C# users: "Git repository (GDScript addon)" /
  "NuGet package (C#)" (`:40,42`). A C# addon also comes from git. Suggest "Addon from a git
  repository (GitHub, GitLab…)" / "C# library from NuGet".
- [ ] **P1-12** Placeholders look like pre-filled values (`add_dependency_dialog.gd:137,149,151`,
  `conflict_dialog.tscn:104`); with OK disabled this reads as "broken". Prefix "e.g. ".
- [ ] **P1-13** Enter in the URL field does nothing (`add_dependency_dialog.gd:194-198` guards
  Enter-confirm but never `register_text_enter(_source)`). Kind auto-switches to Git on a git-looking
  URL but never back (`:102-108`). NuGet hint is always "latest stable" even with Pin to = Version
  (`:159-161`).
- [x] **P1-14** ~~Naming: the flow is toolbar **Add…** → dialog **"Install 'x'?"** / OK **Install**
  (`install_preview_dialog.gd:58`, `.tscn:12`) → summary "Installing 'x' adds …" — next to an unrelated
  toolbar **Install** button.~~ Fixed: the preview dialog is now "Add 'x'?" / **Add** / "Adding 'x'
  installs …", so nothing left in that flow says "Install" except the unrelated toolbar button it was
  colliding with.
  _Deferred: "Add a dependency" (`add_dependency_dialog.tscn:7`) and "Remove dependency"
  (`dotquay_panel.tscn:213`) vs "addon" everywhere else — left alone this pass, since both dialogs are
  shared with NuGet packages, which are not "addons"; renaming either word risks trading one
  inaccuracy for another rather than fixing the collision this item was actually about. Worth a look
  together with P1-10/P1-11's wording pass (step 6)._
- [ ] **P1-15** Preview dialog: the `+ ~ =` markers have no legend (only per-row tooltips,
  `preview_addon_row.gd:37,49-55`); a no-op preview ("nothing on disk would change",
  `dotquay_preview.gd:195-197`) still offers an enabled **Install**; cancelling leaves the log on
  "Resolving what adding x would install..." with no follow-up.
- [ ] **P1-16** NuGet feed approval is unintelligible without knowing what a feed is
  (`install_preview_dialog.tscn:97`, `preview_source_card.gd:37-60`, mentions `trusted_hosts` and
  `[nuget]`). Add one plain sentence: "A NuGet feed is a server that supplies C# packages. Ticking this
  lets <host> supply the packages listed below to your project — only if you trust whoever runs it."
  Also: on Unix `OS.execute` with an output array goes through `/bin/sh` with naive `"…"` quoting, so a
  user-typed value containing `"`, `$` or a backtick is shell-interpreted (`dotquay_cli.gd:105`,
  `dotquay_add_request.gd:101-124`). Fixed by P0-7, or reject those characters in the form.

### Graph and node menus

- [x] **P1-17** ~~No legend for badges anywhere in the panel. `conflict / modified / missing / linked /
  patched / override / orphan / → folder` (`dependency_node.gd:79-106`) are explained only by hover
  tooltips, two of which tell a GUI user to open a terminal (`:91` "Run 'dotquay link --remove'").
  README's table (`README.md:29-36`) is unreachable from the editor and omits `linked` and the rename
  badge. Add a **?** / Legend (toolbar button or sidebar tab): one sentence + recommended action per
  badge.~~ Fixed: a new hand-authored `legend_dialog.tscn` (script-less — per the plugin's own
  scenes-not-code convention, since these eight rows are static content, not runtime state) lists every
  badge — conflict/modified/missing/linked/patched/override/orphan/renamed — each with one sentence and
  its recommended action, opened from a new **?** toolbar button. On a closer re-read only one tooltip
  actually told a GUI user to open a terminal (the linked badge's, `:91`), not two; it now defers to
  the legend instead of naming a command.
- [x] **P1-18** ~~"Pin to a version..." on a healthy addon opens a dialog that claims a conflict:
  synthetic `Conflict` with `kind = ""` (`dotquay_panel.gd:291-310`) → headline "'x' could not be
  resolved." (`dotquay_graph.gd:213-214`), title "Resolve dependency conflict", heading "Pick a version
  to force", OK "Apply override" (`conflict_dialog.tscn:7,64,12`), candidates "dotquay found no
  published version to offer." (`conflict_dialog.gd:84`). Give the dialog a pin mode: "Pin 'x' to a
  version" / "Choose the version dotquay should always use" / OK "Pin"; hide empty sections (the
  100 px "Who needs it" block, `conflict_dialog.tscn:56`).~~ Fixed: `DotquayGraph.Conflict` gained an
  explicit `is_manual_pin` flag, set by the panel itself rather than inferred from a blank `kind` — an
  inference would have misclassified a genuine but unrecognised conflict kind as a pin.
  `headline()`/`dialog_title()`/`candidates_heading()`/`ok_button_label()` all branch on it, so a pin
  now reads "Pin 'x' to a version" / "Choose the version dotquay should always use" / OK "Pin", while a
  real conflict keeps its existing vocabulary untouched. The "Who needs it" block now hides whenever
  `requirements` is empty, pin or not, rather than only for pins.
- [x] **P1-19** ~~Disabled menu items give no reason — **Update** for transitive addons, **Remove** for
  transitive/orphan, **Open repository** without a URL (`dependency_node.gd:138,153,154`). Use
  `popup.set_item_tooltip()`: "Brought in by <requirer>; update/remove that one instead."~~ Fixed:
  `_add_menu_item()` now takes a `disabled_reason` and sets it via `popup.set_item_tooltip()`, backed by
  pure static functions (`requirer_summary()`, `brought_in_by_reason()`, `remove_disabled_reason()`) so
  the wording is unit-tested without a live `GraphNode`. Update and Remove on a transitive addon both
  name the requirer to act on instead; Open repository explains there's no URL because it's likely a
  NuGet package; an orphan's Remove explains it isn't a root manifest entry — deliberately without
  claiming anything would auto-prune it, since nothing in SPEC.md or P1-20 (still open) confirms that.
- [ ] **P1-20** Orphans are a dead end: badge says "Locked but no longer required by anything." (`:99`)
  and **Remove** is disabled for them (`is_removable()` requires root origin). State the remedy the CLI
  actually offers, or enable it.
- [x] **P1-21** ~~Menu wording is jargon: "Pin to a version...", "Clear override", "Capture edits as a
  patch" (`:139,142,149`). Suggest "Always use a specific version…", "Stop forcing a version", "Keep
  my edits (save as patch)". Consider an **Advanced** submenu for pin/override. Node detail prints wire
  literals: `"%s %s" % [source_kind, constraint]` → "rev 3fa9c…" (`:113`); map `rev` → "commit" (also
  `dotquay_graph.gd:107,145`, `dotquay_panel.gd:324`, `conflict_dialog.tscn:93-97`).~~ Fixed: the three
  menu labels now read exactly as suggested. A new `DotquayWire.display_source_kind()` maps the wire
  literal `rev` to "commit" everywhere it reaches the user — node detail, `Requirement.describe()`,
  `Candidate.display_label()`, the override log line, and the conflict dialog's custom-kind picker
  caption — while the wire value the CLI actually receives is untouched. Not done: the optional
  **Advanced** submenu for pin/override — a "consider", not required, and left alone this pass.
- [ ] **P1-22** Every refresh discards manual node positions: `render()` frees all nodes and sets
  `position_offset` unconditionally (`dependency_graph_view.gd:41-46,64`), and `refresh()` runs on every
  return to the tab. Keep a `name → position_offset` map across renders; add an "Auto-arrange" action.
- [ ] **P1-23** Empty graph canvas has no hint (only the muted toolbar text
  "No dependencies yet — use Add.", `dotquay_panel.gd:558`). Draw a centred placeholder inside the
  GraphEdit. Long addon names/folders overflow the fixed 240 px node (`dependency_node.tscn`, title
  at `dependency_node.gd:53`) into the next column — `TRIM_ELLIPSIS` on the title.
- [ ] **P1-24** The lockfile view nags permanently: "(from lockfile — press Re-resolve to check for
  conflicts)" in the summary (`:585`) and "Conflicts are only detected by a live re-resolve…" in the
  default sidebar tab (`:601`). Reword to "No conflicts known (Re-resolve checks online)" and drop the
  summary suffix.

### Toolbar, log, feedback

- [x] **P1-25** ~~"Re-resolve" vs "Refresh" is opaque (`dotquay_panel.tscn:51-59`), and "Check for
  updates" (addons) sits above the "Update dotquay" banner (the tool). Suggest "Reload" (or drop it —
  `refresh_on_focus` exists), "Check for conflicts (online)", "Check addons for updates"; move
  Re-resolve/Doctor into a **Tools** overflow.~~ Fixed: Refresh is now "Reload" (kept rather than
  dropped, since `refresh_on_focus` covers the common case but not an explicit manual reload), "Check
  for updates" is now "Check addons for updates", and Re-resolve/Doctor moved into a new **Tools**
  `MenuButton` overflow. "Check for conflicts (online)" for Re-resolve's own label was folded into the
  Tools menu item text rather than kept as a separate always-visible button. Reload keeps its existing
  exemption from the busy/readiness gate (P0-9/P0-6), extended to the new Legend button (P1-17) since
  neither needs a project to already be loaded.
- [ ] **P1-26** "Update all" has no confirmation or preview (`dotquay_panel.gd:219-221`) while Add has
  both and Remove has one. A beginner cannot tell it may move every addon to a new commit.
- [ ] **P1-27** Commands that change nothing end silently: `_report_operation` (`:660-672`) skips every
  `unchanged` change and prints no closing line, so the log stops at "Installing from the lockfile...".
  Add "Done." / "Done — nothing needed changing." Also "Check for updates" results are log-only and
  not actionable (`:634-648`) — badge the node and/or offer "Update these".
- [x] **P1-28** ~~Warnings and errors differ only by colour (`:683-688`). Prefix "Warning: " / "Error:
  ". Log text is not BBCode-escaped: addon names, NuGet ranges like `[1.0,2.0)`, CLI messages quoting
  `[addons.x]` and TOML tables go straight into `[color]…[/color]` (`:679-701`,
  `dotquay_palette.gd:24`) — a known tag in user/CLI text corrupts the rest of the log. `replace("[",
  "[lb]")` before wrapping.~~ Fixed the escaping half — the actual data-corrupting bug, and the reason
  this was a P1 rather than pure polish: added `DotquayPalette.escape_bbcode()`, applied inside
  `_log_line`/`_log_warning`/`_log_error`, the one choke point every log entry already passes through,
  so no call site elsewhere had to change. Covered by new `tests/test_dotquay_palette.gd` — the
  `test_log_text_with_bbcode_tags_is_escaped` this review's own T section asked for, under a more
  specific name. Not done: the "Warning: "/"Error: " text prefixes — colour is already how the rest of
  the panel (badges, the setup banner) distinguishes severity, and stacking a redundant text prefix
  onto the log alone reads as a separate cosmetic call worth its own look, not a correctness fix; left
  open for the remaining P1 polish pass.
- [ ] **P1-29** The log has no structure: no separator per command, concurrent runs interleave
  (first show runs `graph` and `self check` together, `:141-143`), no Clear button, 140 px tall. Add a
  rule/heading per command and a Clear button. Busy indicator is a lowercase "working..." at the far
  right (`dotquay_panel.tscn:78`) that never says *what* is running.
- [ ] **P1-30** Overlapping `graph` reads can land out of order (refresh from tab focus, tracker,
  Locate, self-update, explain fallback: `:141,344,452,485,525`); results apply in arrival order.
  Remember the newest `graph` ticket and ignore older results (or serialise per P0-6).
- [ ] **P1-31** Fixed dialog sizes with `wrap_controls = false` ignore editor scale
  (`add_dependency_dialog.tscn:9,11` 620×340, `install_preview_dialog.tscn` 680×460,
  `conflict_dialog.tscn` 620×520, `explain_dialog.tscn` 640×420). At 150–200 % the last form row / the
  blocker text is clipped behind the buttons. Multiply by `EditorInterface.get_editor_scale()` in
  `_ready`. Long labels without `text_overrun_behavior` push buttons out of view under scroll containers
  with `horizontal_scroll_mode = 0` (`items/dotnet_row.tscn:8-13`, `items/preview_addon_row.tscn:31-36`).
- [ ] **P1-32** Explain dialog: the blocker tells a GUI user to run
  `'dotquay update x --force' at a terminal` (`explain_dialog.gd:129-131`); add a "Keep my edits (save
  as patch)" button via `add_button()`. Its safe-verdict sentence is maintainer-speak: "The recorded
  hash predates the current hashing rules; a forced update rerecords it and discards nothing."
  (`dotquay_explain.gd:179-180`). The unavailable case (`:126-127`) disables the only button with no
  next step.
- [ ] **P1-33** `error_message` can be empty: `ok:false` with a missing/non-dict `error`
  (`dotquay_result.gd:139-144`) logs an empty red line (`dotquay_panel.gd:399,516`). Synthesize
  "dotquay reported a failure (exit code N) but gave no details." No `KIND_AUTH` constant exists
  although the CLI emits `auth` (`JsonOutput.cs:66-71`, SPEC §17) — at minimum hint "run `dotquay login
  <host>`".

---

## P2 — Message rewrites (concrete strings)

Rules that came out of the inventory: never cite SPEC; introduce "dotquay.lock" by name before saying
"lockfile"; say "commit" not "rev"; say "what needs it" not "requirer"; one arrow style (`→`) and one
dash style (`—`); "Warning:"/"Error:" prefixes; real plurals instead of "(s)".

| Where | Now | Suggested |
|---|---|---|
| `dotquay_cli.gd:67` | No dotquay executable configured or found on PATH. Set Editor Settings -> Dotquay -> CLI Path. | dotquay isn't installed, or Godot can't find it. Install it from <release URL>, then press "Find dotquay on PATH" or set Editor › Editor Settings › Dotquay › General › Cli Path. |
| `dotquay_cli.gd:74` | The configured dotquay path does not exist: %s | The dotquay path in Editor Settings doesn't exist: %s. Fix it there, or press "Find dotquay on PATH". |
| `dotquay_cli.gd:115` | Could not start dotquay at %s. | Could not start dotquay at %s. On Linux/macOS check it is executable (chmod +x). Output: … |
| `dotquay_result.gd:101` | dotquay did not return JSON (exit code %d). Check that the CLI path points at the dotquay executable. | dotquay didn't answer as expected (exit code %d). Its output was: … |
| `dotquay_result.gd:135` | dotquay %s speaks payload schema %d, but this plugin understands %d. Update whichever is older. | This plugin and dotquay %s are incompatible versions. Update dotquay (Update dotquay button, if shown) or update this plugin. |
| `dotquay_panel.gd:302` | No conflict was reported for '%s'. Pinning it here writes an [overrides] entry that wins over every other constraint (SPEC.md §8). | '%s' has no conflict right now. Choosing a version here forces it to that version, overriding whatever other addons ask for. |
| `dotquay_panel.gd:504` | dotquay %s is available - this editor is running %s. | dotquay %s is available — you have %s. |
| `dotquay_panel.gd:640` | %d dependency/dependencies have newer versions: | Newer versions are available for %d addons/packages: (proper singular/plural) |
| `dotquay_panel.gd:585` | (from lockfile — press Re-resolve to check for conflicts) | *(drop)* |
| `dotquay_panel.gd:601` | Conflicts are only detected by a live re-resolve. Press Re-resolve to check. | No conflicts known. Press Re-resolve to check online. |
| `dotquay_panel.gd:162` | Re-resolving against live git refs — this can take a moment. | Checking repositories online for the newest allowed versions... (this can take a moment) |
| `dotquay_panel.tscn:35` | Make addons/ and .csproj match the lockfile exactly. | Re-download the addons listed in dotquay.lock — use after cloning a project, or when an addon shows "missing". |
| `dotquay_panel.tscn:40` | Re-resolve every dependency and reinstall. | Move every addon and package to its newest allowed version and reinstall. |
| `dotquay_panel.tscn:45` | Ask each upstream what's newer, without cloning. | Check whether newer versions of your addons and packages exist. Nothing is changed. |
| `dotquay_panel.tscn:53` | Re-walk the live dependency graph and report conflicts. | Contact each repository to work out the newest allowed versions and report conflicts. Needs internet; nothing is installed. |
| `dotquay_panel.tscn:63` | Run dotquay's sanity checks. | Check this project for problems such as missing folders or addons nothing needs any more. |
| `dotquay_panel.tscn:109` | Scaffold a manifest in this Godot project, the same as running `dotquay init`. | Create a dotquay.toml file in this project (the same as running dotquay init). |
| `dependency_node.gd:82-85` | Hand-edited without a captured patch. Install and update will refuse to overwrite it (SPEC.md P0-6). … | You (or the editor) changed files in this addon and no patch has been captured, so Install and Update will not overwrite it. Choose "Why is this modified?" to see what changed. |
| `dependency_node.gd:89-91` | Linked to a folder on this machine … Run 'dotquay link --remove' to put it back. | This addon is a link to a folder on this computer, so edits there show up immediately. To go back to the recorded version run `dotquay link --remove %s` in a terminal. *(or add an Unlink menu item)* |
| `dependency_node.gd:93` | A captured patch is re-applied on every update (SPEC.md §10). | You captured your edits as a patch. dotquay re-applies it after every update — this is expected. |
| `dependency_node.gd:96` | Forced by an [overrides] entry in dotquay.toml. | Forced to this version by an [overrides] entry in dotquay.toml. Choose "Clear override" to undo. |
| `dependency_node.gd:99` | Locked but no longer required by anything. | Nothing needs this addon any more. *(+ the actual remedy)* |
| `dependency_node.tscn:25` | Things you can do to this dependency. | Actions for this addon. |
| `dotnet_row.gd:31` | Required by an addon, not declared in this project's manifest (SPEC.md P1-5). | Needed by an addon, not listed in your dotquay.toml, so it cannot be removed from here. |
| `dotquay_graph.gd:149` / `:208` | satisfies every requirer / No released version of '%s' satisfies every requirer. | works for everything that needs it / No released version of '%s' works for everything that needs it. |
| `dotquay_graph.gd:210` | '%s' has a malformed version constraint. | '%s' has a version requirement dotquay can't read. Check its entry in dotquay.toml. |
| `dotquay_explain.gd:175` | …Being offline looks exactly like this. | dotquay couldn't download the recorded version of '%s' to compare against. This usually means you're offline. |
| `dotquay_explain.gd:179` | …The recorded hash predates the current hashing rules; a forced update rerecords it and discards nothing. | No files in addons/%s actually differ. The "modified" flag comes from an older way of checking; Force update will clear it without changing any files. |
| `dotquay_explain.gd:183` | %s in addons/%s — nothing here survives a reinstall anyway. | %s in addons/%s. These are all generated by the editor, so Force update won't lose anything. |
| `dotquay_explain.gd:186` | %d real content change(s) in addons/%s. A forced update would discard them. | %d files in addons/%s have real edits. Force update would delete those edits. |
| `add_dependency_dialog.gd:170-171` | A range like ~2.5 keeps up with patches; an exact tag pins to it. | A range like ~2.5 follows bug-fix releases (2.5.1, 2.5.2…); an exact tag like 2.5.1 never moves. *("patches" collides with captured patches)* |
| `preview_addon_row.gd:51,53` | this add would leave it alone / this add would move it to | adding won't change it / adding this would move it to |
| `preview_source_card.gd:47` | Adopted automatically: %s is already in [nuget] trusted_hosts. | Trusted automatically: %s is already in your project's trusted hosts (dotquay.toml). |
| `conflict_dialog.tscn:64` / `:12` | Pick a version to force / Apply override | Choose the version to use / Use this version |
| `conflict_card.tscn:28` | …writing an [overrides] entry that wins over every other constraint. | Choose a version for this addon; it will be used even if other addons ask for something else. |
| `dotquay_panel.gd:530`, `.tscn:78` | updating dotquay... / working... | Updating dotquay... / Working... |

~~Pluralisation sites to route through one helper (`plural(n, one, many)`): `dotquay_preview.gd:200,208,212`,
`dotquay_panel.gd:564,567 ("%d NuGet" → "%d NuGet packages"),579,640`, `install_preview_dialog.gd:134`,
`dotquay_explain.gd:114,116,118,186`.~~ Done: added `DotquayText.plural(count, singular, plural)` and
routed every one of these sites through it. Leading double-space indents in the log
(`dotquay_panel.gd:209,645,657,669`) and the README's ellipsis style — not done this pass.

**Update (step 7):** every row above is applied, most verbatim. Adapted rather than copied exactly:
the schema-mismatch and "does not exist" messages keep their numbers/exact substring (a real bug
report benefits from the schema numbers; `tests/test_dotquay_settings.gd` asserts the exact "does not
exist" phrase); the `dotquay_panel.tscn:53`/`:63` rows moved *into* the Tools menu's own two item
tooltips rather than staying on a toolbar button, since P1-25 (an earlier pass) already folded
Re-resolve/Doctor into that menu; `dotquay_explain.gd:179`'s row kept its "Nothing in addons/%s
differs" opening (the same test constraint) and only replaced the confusing "recorded hash predates
the hashing rules" half; `dotquay_explain.gd:186`'s row is phrased as "%s real edits in addons/%s"
with the count/word pair pre-built by `plural()`, rather than a single three-placeholder format
string. The `conflict_dialog.tscn:64`/`:12` row no longer applies — P1-18 (an earlier pass) replaced
that static scene text with `Conflict.candidates_heading()`/`ok_button_label()`, computed in code.
Not done: `dependency_node.gd`'s remaining status-row texts for modified/patched/override/orphan
(`:82-85,93,96,99` in the table's own line numbers) — the linked-badge row was already fixed in the
P1-17 pass, but these four still read close to their "Now" column, including the override badge's
literal `[overrides]` bracket; left for the P1 polish pass alongside P1-20 (the orphan badge's actual
remedy is still unverified). The log's `[indent]` formatting and the README's ellipsis/badge-table
gaps are also still open — the README was already out of scope for every earlier pass in this review
and stays that way here.

README (plugin): `README.md:3` links `../../../../README.md` (dead once copied into a project) → repo
URL. `:57` heading "## …and shows you what you're agreeing to" is a fragment → "## Previewing before
you install". `:70` "offering the button would be a lie", `:94` "its own kind of annoying" are
developer voice. Lines 96-132 (Layout, Development) are contributor content → a "## For contributors"
heading or CONTRIBUTING. The badge table omits `linked` and `→ folder`; the Layout table omits
`ui/explain_dialog.tscn`; Check for updates / Doctor / Install / Update all / the self-update banner /
Open repository / Clear override are never mentioned.

---

## C — Code hygiene: dead code, stale comments, parallel mechanisms

Everything here was verified by grepping `addons/` + `tests/`.

### Dead / unreferenced

- [ ] **C-1** `dotquay_result.gd:16,18,19` `KIND_WORKFLOW`, `KIND_CONFLICT`, `KIND_GIT` — only their
  definitions exist; `KIND_AUTH` is missing (P1-33). Either complete the set and move it to
  `dotquay_wire.gd`, or delete.
  _Progress: `KIND_AUTH` was added (closing P1-33's specific ask), but the constants still live in
  `dotquay_result.gd` rather than moving to `dotquay_wire.gd` — leaving open._
- [x] **C-2** ~~`dotquay_result.gd:40` `raw_output` — assigned, never read (P0-4).~~ Fixed as part of
  P0-4/P0-5: `failure()` now folds it into `error_message`, which the panel already logs.
- [x] **C-3** ~~`dotquay_cli.gd:46` `is_busy()` — no callers (P0-6 wants it).~~ Fixed: `run()` now calls
  it to decide whether a new request queues or starts immediately.
- [ ] **C-4** `dotquay_settings.gd:2` `class_name DotquaySettings` — never referenced; every consumer
  preloads (`plugin.gd:12`, `dotquay_cli.gd:23`, `dotquay_panel.gd:17`). Pick preload *or*
  `class_name` project-wide: `dotquay_panel.gd:16-18` preloads `CLI_SCRIPT/SETTINGS/RESULT` while the
  same file uses `DotquayCli`, `DotquayResult`, `DotquayRescan`, `DotquayRefreshTracker`,
  `DotquayLogBuffer` by class name. The class-cache concern applies to typed references too, so the
  preloads buy nothing.
- [ ] **C-5** `items/candidate_row.gd:7` `CUSTOM_INDEX := -1` unused; the `-1` sentinel is also in
  `conflict_dialog.gd:16` and `conflict_dialog.tscn:86` (metadata) and re-set in `_ready` (`:48`).
  Keep one.
- [ ] **C-6** `add_dependency_dialog.gd:23` `_mode_row` declared, never used.
- [ ] **C-7** Parsed but never read: `DotquayGraph.overrides` (`dotquay_graph.gd:246,259-260`),
  `AddonNode.path` (`:19,38`), `DotquayPreview.Source.name` (`dotquay_preview.gd:96,109`),
  `Explanation.baseline_includes_patch` (`dotquay_explain.gd:131,141`).
- [x] **C-8** ~~`dependency_graph_view.gd:27-33,103-108` `_reject_connection` / `_reject_disconnection`
  are empty. In Godot 4 `connection_request` / `disconnection_request` are *requests* — nothing is
  connected unless the handler calls `connect_node`, so a no-op handler equals no handler. The comment
  at `:28-30` ("simply not connecting the signal would let the user rearrange real dependencies") is
  wrong.~~ Fixed: both functions and their `_ready()` wiring and the incorrect comment were deleted.
- [ ] **C-9** `dotquay_settings.gd:15-18,40-42` `RETIRED_SETTINGS` migration for
  `resolve_timeout_seconds` — there are no earlier *public* versions. Delete (or keep only if P1-1
  renames the path key and needs the mechanism).
- [ ] **C-10** Production-dead, test-only: `Candidate.override_flag()` (`dotquay_graph.gd:136-137` — the
  panel re-derives `"--" + kind` by hand at `dotquay_panel.gd:314`; use one), `AddonNode.is_healthy()`
  (`:66-70`, sole user of `DotquayWire.STATUS_CLEAN`), `has_conflict()` (`:299-300`), `has_conflicts`
  (`:247,254` — UI uses `conflicts.is_empty()`), `Source.is_trusted_host` (`dotquay_preview.gd:101,112` —
  the dialog decides by which *list* a source is in, `install_preview_dialog.gd:100-110`). Use or drop.

### Stale / misleading comments

- [x] **C-11** ~~`dotquay_panel.gd:492-494` — the doc comment "Only ever reached from the explain dialog,
  and only when it judged the change benign…" sits above `_report_self_check`; it describes
  `_on_force_update_requested` (`:538`), which has no doc comment.~~ Fixed: the comment now sits above
  `_on_force_update_requested`, and `_report_self_check` has the comment that actually describes it.
- [x] **C-12** ~~`tests/test_cli_contract.gd:54-55` "`init` predates the --json layer and still prints
  plain text" — false, `InitCommand.cs:96-112` emits an envelope. Tighten the assertion to
  `init_result.ok`. Also `:128` labels an `init` call `"status"` (copy-paste).~~ Fixed: comment
  corrected, assertion now requires `init_result.ok` (not just the file existing), label renamed.
- [ ] **C-13** `dependency_graph_view.gd:11-13` says "the panel listens to the built-in
  `node_selected`" — the panel never connects it (only hit is this comment). Delete or implement
  (a details pane on select would help newbies).
  _Still open — re-verified after this pass: `dotquay_panel.gd` still has no `node_selected`
  connection anywhere, so the comment (kept, word-for-word, through the C-8 edit of the same file) is
  still making a false claim._
- [ ] **C-14** `dotquay_add_request.gd:77-78` doc for `git_url()` ("the key a pending request must be
  stored under") contradicts `dotquay_panel.gd:60-63` ("Keyed on the ticket, not the URL");
  `git_url()` is now only used by `looks_like_git()`. Same stale claim in
  `tests/test_dotquay_add_request.gd:117-119` (test name) and SPEC §18.2.
- [ ] **C-15** `dotquay_result.gd:65-70` brace trimming cuts first `{` to *last* `}` — a trailing stderr
  line containing `}` (NuGet HTTP error bodies are JSON) turns a successful run into "did not return
  JSON". Fixed properly by P0-7 (separate stderr); until then parse line-by-line.
- [ ] **C-16** `dotquay_wire.gd:5` "Every literal that crosses the CLI boundary, in one place." — false:
  error kinds and envelope keys live in `dotquay_result.gd:13-26,109-146`; command names and flags in
  `dotquay_panel.gd`; flags in `dotquay_add_request.gd:117,121,146,150,152`. `dotquay_graph.gd:7` "The
  UI never touches the raw dictionaries." — false: `_report_outdated/_report_doctor/_report_operation/
  _report_self_*` (`dotquay_panel.gd:494-513,634-672`) read raw payloads, which is why
  `_dictionaries_in` (`:708-716`) exists. Either make the claims true or scope them.
- [ ] **C-17** Unfollowable references: `docs/DESIGN-v0.12-MODIFIED-REASON.md` (`dotquay_wire.gd:47`,
  `dotquay_explain.gd:6`, `explain_dialog.gd:6`, `tests/test_dotquay_explain.gd:3`), `docs/SPRINT-01.md`
  (`tests/test_runner.gd:9`), version tags "(v0.17)" (`dotquay_panel.gd:78`, `dotquay_wire.gd:86`),
  "v0.8" (`tests/test_dotquay_preview.gd:6`), IDs `OQ-2`/`D7`/`P0-6`/`P1-5` in code comments. Replace
  with SPEC section numbers or inline the one sentence that matters.
- [ ] **C-18** Commit-message-style history in doc comments — rewrite as present-tense rationale:
  `dotquay_log_buffer.gd:7-12` "used to be the RichTextLabel itself", `dotquay_refresh_tracker.gd:7-10`
  "used to keep one boolean", `dotquay_nodes.gd:12-14` "Four scripts had grown their own copies… this is
  the one that is right", `dotquay_cli.gd:94-95` "this is why the CLI grew --project",
  `README.md:89` "used to look like it did nothing", `tests/test_dotquay_result.gd:70`,
  `tests/test_dotquay_log_buffer.gd:5`, `tests/test_dotquay_refresh_tracker.gd:5`.
- [ ] **C-19** `dotquay_cli.gd:102-104` "(missing .NET runtime, a wrong architecture)" — the shipped
  CLI is NativeAOT; a missing runtime is not a user failure mode. `tests/test_runner.gd:4-12` header
  says the suite "only ever tests parsing and layout" yet includes `test_scenes.gd` and
  `test_cli_contract.gd`, and its "Run with:" line omits the mandatory `--import` pass.
  `dotquay_panel.gd:148-150` has a dangling " -" line break.

### Parallel mechanisms / wire fragility

- [ ] **C-20** Half the envelope-command vocabulary is inline: `_run("install"|"update"|"override"|
  "patch"|"remove"|"init", …)` at `dotquay_panel.gd:216,221,236,252,256,284,328,333,541` while the rest
  use `DotquayWire.COMMAND_*`. Add the six constants. Every call also repeats the command as label
  *and* `args[0]` — derive one from the other (the two exceptions `self-check`/`add-preview` can live in
  the wire file).
- [ ] **C-21** CLI flags as literals across files: `--preview`, `--adopt-source`, `--resolve`,
  `--explain`, `--clear`, `--force`, `--git`, `--install-as`, `--path`, `--version/--branch/--rev`,
  `"--" + kind` (twice). `DotquayWire.FLAG_*`.
- [ ] **C-22** `MAX_LOG_ENTRIES := 200` (`dotquay_panel.gd:24`) duplicates
  `DotquayLogBuffer.DEFAULT_CAPACITY := 200` (`dotquay_log_buffer.gd:17`). Two `_clear()` wrappers
  (`dotquay_panel.gd:723`, `install_preview_dialog.gd:150`) around `DotquayNodes.clear_children`, which
  five other scripts call directly. `ACTION_RESOLVE` and `ACTION_OVERRIDE` route to the same handler.
  `add_dependency_dialog.tscn:58` placeholder is unconditionally overwritten by `_refresh()`.
- [ ] **C-23** Null handling is inconsistent: `dotquay_graph.gd:35-46` uses `str_or_empty` for some
  fields but `String()` for `name/git/path/folder/origin`; `dotquay_preview.gd:34-36` uses `String()`
  for `resolved/short_sha/folder`. A JSON `null` renders as `"<null>"`, a null `folder` yields a
  "→ <null>" rename badge, a null `origin` makes an addon non-removable. `str_or_empty` everywhere.
  Default values for the same key differ (`entry.get("name", "?")` vs `payload.get("name", "")`).
- [ ] **C-24** Raw payloads for outdated/doctor/operation/self-check have no typed view and no fixture
  test, unlike graph/preview/explain. Doctor's `issues[].kind` is ignored. Small parsers + fixtures.
- [ ] **C-25** Style: `plugin.gd` uses `get_editor_interface()` (pre-4.2) while the panel uses the
  `EditorInterface` singleton; `typeof(x) == TYPE_DICTIONARY` vs `x is Dictionary` guards; untyped
  `_pending_requests: Dictionary` (typed dictionaries exist since 4.4); `DotquayWire.ACTION_UPDATED`
  (change action) vs `DotquayDependencyNode.ACTION_UPDATE` (menu action) — near-identical names for
  different vocabularies, prefix menu actions `MENU_*`. `.tscn` provenance is mixed (some have
  `uid=`/`unique_id`, `explain_dialog.tscn`, `dependency_*.tscn`, `items/*.tscn` have neither;
  `dotquay_panel.tscn:206` ExplainDialog lacks `unique_id`) — open and re-save every scene in 4.7 once.
  Dialog scenes set `visible = true` on the root and the panel overrides to `false`; set `false` in the
  dialog scenes.

---

## T — Tests to add (names)

No test covers `dotquay_cli.gd`, `dotquay_settings.gd`, `dotquay_nodes.gd`, `dotquay_palette.gd`,
`dotquay_wire.gd`, `plugin.gd`, or any UI script except `explain_dialog.gd` (via `test_scenes.gd`).
`test_scenes.gd` checks `%UniqueName` existence, not type, and hand-lists the toolbar buttons
(`:36-39`) — a second copy of `_wire_toolbar`'s keys that the file's own header says it avoids.
`run_tests.ps1` runs under `pwsh` on Linux/macOS, but a contributor without it has no entry point and a
parse error in a suite exits 0 (`run_tests.ps1:70-73`) — ship `run_tests.sh` with the same three steps.

Contract / parsing:
- `test_result_null_warnings_do_not_raise`, `test_graph_null_arrays_parse_as_empty`,
  `test_preview_null_arrays_parse_as_empty`, `test_explain_null_changes_parse_as_empty` (P0-8)
- `test_result_missing_ok_is_bad_output_with_a_message`, `test_result_non_dict_error_still_has_a_message`
  (P1-33), `test_result_unknown_error_kind_is_preserved_with_its_message` (`"kind": "auth"`)
- `test_bad_output_message_includes_the_raw_bytes` (P0-4),
  `test_extract_json_object_survives_braces_in_trailing_noise` (C-15)
- `test_graph_null_folder_is_not_a_rename`, `test_preview_null_resolved_is_empty` (C-23)
- `test_cli_contract_init_emits_a_json_envelope` (C-12)

_Progress: the null/missing-field coverage above landed (under slightly different names —
`test_null_warnings_do_not_crash_and_yield_none`, `test_missing_error_object_produces_an_informative_message`,
`test_non_dictionary_error_produces_an_informative_message`, `test_failure_helper_records_and_surfaces_raw_output`
in `test_dotquay_result.gd`; `test_null_arrays_parse_as_empty_not_a_crash` +
`test_null_nested_arrays_parse_as_empty_not_a_crash` + `test_null_folder_also_defaults_to_the_name` in
`test_dotquay_graph.gd`; the equivalent set in `test_dotquay_preview.gd` and `test_dotquay_explain.gd`),
and the C-12 fix above tightened `test_cli_contract.gd` in place rather than adding a new test. All 468
assertions pass against the real Godot 4.7.2 binary and the real CLI. Not yet added: the CLI
runner/settings, panel, graph-view, and add-form suites below — none of P0-6's queueing, P0-7's
timeout, or the settings path-normalization got dedicated new tests this pass._

CLI runner / settings (make the executor injectable so no real binary is needed):
- `test_cli_serialises_mutating_runs`, `test_panel_refuses_mutations_while_busy` (P0-6)
- `test_cli_launch_failure_is_only_exit_code_minus_one` (P0-5)
- `test_settings_normalises_quoted_and_tilde_paths`, `test_discovery_probes_known_install_dirs`
  (inject `file_exists`/env) (P0-3, P1-2)

Panel (instantiate detached as `test_scenes` does; inject `_cli`/`_rescan` doubles via `set()`):
- `test_panel_dispatches_every_command_to_its_handler`, `test_panel_error_path_applies_a_conflicted_graph`
- `test_panel_ticket_keyed_pending_requests_survive_a_failed_preview`
- `test_panel_self_check_retries_after_not_configured` (P1-7),
  `test_panel_failed_self_update_restores_the_banner_message`
- `test_panel_summary_is_empty_until_a_graph_arrives` (P0-1)
- ~~`test_log_text_with_bbcode_tags_is_escaped` (P1-28; needs an `escape_bbcode` helper in core)~~
  Added, as `tests/test_dotquay_palette.gd` (the helper itself needed no panel instance to test).
- `test_update_all_asks_for_confirmation` (P1-26)

Graph view:
- `test_graph_view_preserves_manual_positions_across_render` (P1-22)
- `test_depths_terminate_on_a_cycle`, `test_layers_with_fifty_nodes_are_stable_and_sorted`

Add form:
- ~~`test_add_form_parses_github_tree_urls_into_branch_and_subtree`,
  `test_add_form_rejects_bare_owner_repo_with_a_hint` (P1-8)~~ Added, alongside six more
  `diagnose_source_shape()` cases (GitLab's `-/tree/`, no-subtree links, plain URLs, `.git` suffixes,
  NuGet IDs, blank input) — all in `tests/test_dotquay_add_request.gd`, no scene needed.
- `test_to_args_keeps_values_with_spaces_as_single_elements`,
  `test_to_args_rejects_leading_dash_values`

---

## Suggested order of work

1. ~~P0-1, P0-10, P0-11, C-11, C-12, C-13~~ — done except C-13 (see note above).
2. P0-6 + P0-7 together (queue + `execute_with_pipe` + timeout + kill-on-exit) — one change, fixes
   five findings. **Done: both the queue (P0-6) and the timeout/kill-on-exit rewrite (P0-7)** — the
   latter verified against real hung and real fast processes on the actual Godot 4.7.2 binary.
3. ~~P0-4, P0-5, P0-8~~ — done, in two follow-up passes; P1-33 not yet reviewed against the current code.
4. ~~P0-2, P0-3, P1-1..P1-4~~ — done.
5. ~~P0-9, P1-8, P1-9, P1-14~~ — done.
6. ~~P1-17, P1-18, P1-19, P1-21, P1-25~~ — legend, pin mode, menu wording, toolbar names. Done.
7. The P2 string table — **done**, folding in P1-5 (no-manifest banner wording, duplicate red log line)
   and P1-28's escaping half (BBCode injection in the log). The remaining P1 polish and C items — not
   started.

**Net this pass:** 11 of 11 P0s fully closed, plus P1-1, P1-2, P1-3, P1-4, P1-5, P1-8, P1-9, P1-14,
P1-17, P1-18, P1-19, P1-21, P1-25, P1-28 (escaping half), the full P2 string-table rewrite (~36 sites,
plus the new `DotquayText.plural()` helper), C-2, C-3, C-8, C-11, C-12.
Verified for real: 637/637 GDScript test assertions pass against Godot 4.7.2 and the real dotquay CLI,
across 22 conventional commits from this session (`081d141`..`863207f`) on `main` — three unrelated
MCP-feature commits and one manual commit from other concurrent work in the repo sit interleaved in
that range and were left untouched (verified via `git status --short` before every stage). Everything
a first-time Godot user would hit trying to add, update, or remove an addon through this tab is now
either fixed or has a working, discoverable path forward, and the panel's user-facing wording has had
its first full plain-language editing pass — the remaining P1/P2/C backlog above is ergonomics and
hygiene, not a beginner getting stuck or confused by what's on screen.
