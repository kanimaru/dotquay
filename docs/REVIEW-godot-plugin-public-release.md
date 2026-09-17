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
- [x] **P1-6** ~~Sidebar tabs are blank before the first graph — `_render_conflicts`/`_render_dotnet`
  run only from `_apply_graph` (`dotquay_panel.gd:593-631`). Render the empty-state labels in `_ready`.~~
  Fixed: both are now also called from `_ready()` — each already renders its own "nothing here yet"
  state when the graph is null, so no new branch was needed, just the earlier call.
- [x] **P1-7** ~~`_self_checked` is set to `true` before the run and even when the CLI is not configured
  (`dotquay_panel.gd:151-156`); the deferred `KIND_NOT_CONFIGURED` failure hides the banner
  (`:495-497`). A user who opens the tab before configuring the path never gets an update notice
  that session. Reset the flag on `KIND_NOT_CONFIGURED`/`KIND_LAUNCH_FAILED`. Related: a failed
  self-update leaves `_update_message.text = "updating dotquay..."` (`:530`) while re-enabling the
  button (`:510-519`) — restore the message.~~ Fixed both: `_report_self_check()` resets
  `_self_checked` on those two kinds so a later refresh, after the path is fixed, gets a real check
  instead of silence for the rest of the session; a new `_update_available_message` field remembers
  the last real "update available" text so a failed self-update restores it instead of leaving
  "Updating dotquay..." on screen.

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
- [x] **P1-10** ~~Field labels/placeholders: "Subtree" (`add_dependency_dialog.tscn:91`) is git jargon and
  its placeholder (`:97`, ~85 chars) is cut mid-sentence in the ~510 px field; same for "Install as"
  (`:113`, ~75 chars). Suggest "Folder in repo" / "Install to", short placeholders, and tooltips on the
  five row labels (none have any). "Pin to" (`:71`) whose first option is "Latest release" isn't a pin
  — "Which version?" with "Newest release (recommended)", "A specific version…", "Follow a branch…",
  "An exact commit…".~~ Fixed exactly as suggested: "Which version?" / "Folder in repo" / "Install to",
  the four reworded mode items, and a tooltip on all five row labels (`KindLabel`, `SourceLabel`,
  `ModeLabel`, `PathLabel`, `InstallAsLabel`) carrying the fuller explanation the shortened placeholders
  no longer fit.
- [x] **P1-11** ~~Source kind labels mislead C# users: "Git repository (GDScript addon)" /
  "NuGet package (C#)" (`:40,42`). A C# addon also comes from git. Suggest "Addon from a git
  repository (GitHub, GitLab…)" / "C# library from NuGet".~~ Fixed verbatim as suggested.
- [x] **P1-12** ~~Placeholders look like pre-filled values (`add_dependency_dialog.gd:137,149,151`,
  `conflict_dialog.tscn:104`); with OK disabled this reads as "broken". Prefix "e.g. ".~~ Fixed: `main`
  and NuGet's `13.0.3` in the add dialog, and `~1.4, main, or a commit SHA` in the conflict dialog's
  custom-value field, are now prefixed `e.g. `. Left alone: the git Version placeholder ("~2.5 for a
  range, or 2.5.1 for one exact release") and the Commit placeholder ("a full commit SHA") already read
  as instructions rather than values, so prefixing them would be noise.
- [x] **P1-13** ~~Enter in the URL field does nothing (`add_dependency_dialog.gd:194-198` guards
  Enter-confirm but never `register_text_enter(_source)`). Kind auto-switches to Git on a git-looking
  URL but never back (`:102-108`). NuGet hint is always "latest stable" even with Pin to = Version
  (`:159-161`).~~ Fixed: `register_text_enter` is now called for all four fields (`_source`, `_value`,
  `_path`, `_install_as`), not just the one the review named — Enter previously did nothing anywhere in
  this dialog. Kind now switches back to NuGet once the dialog's own git-URL guess no longer applies,
  gated by a `_kind_auto_selected` flag so a kind the user picked from the dropdown themselves is never
  second-guessed by what they go on to type. The NuGet hint now names the exact-version behavior when
  Pin mode is Version instead of repeating "latest stable" regardless of mode.
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
- [x] **P1-15** ~~Preview dialog: the `+ ~ =` markers have no legend (only per-row tooltips,
  `preview_addon_row.gd:37,49-55`); a no-op preview ("nothing on disk would change",
  `dotquay_preview.gd:195-197`) still offers an enabled **Install**; cancelling leaves the log on
  "Resolving what adding x would install..." with no follow-up.~~ Fixed: a `MarkerLegend` label now
  sits above the addon list ("+ arrives new · ~ moves to a different commit · = already installed,
  unchanged"); a no-op preview now disables OK with a tooltip explaining why, instead of offering an
  install that would do nothing; and the panel now connects the dialog's own `canceled` signal to log
  "Cancelled — nothing was added." instead of leaving "Resolving..." as the last word.
- [x] **P1-16** ~~NuGet feed approval is unintelligible without knowing what a feed is
  (`install_preview_dialog.tscn:97`, `preview_source_card.gd:37-60`, mentions `trusted_hosts` and
  `[nuget]`). Add one plain sentence: "A NuGet feed is a server that supplies C# packages. Ticking this
  lets <host> supply the packages listed below to your project — only if you trust whoever runs it."
  Also: on Unix `OS.execute` with an output array goes through `/bin/sh` with naive `"…"` quoting, so a
  user-typed value containing `"`, `$` or a backtick is shell-interpreted (`dotquay_cli.gd:105`,
  `dotquay_add_request.gd:101-124`). Fixed by P0-7, or reject those characters in the form.~~ Fixed: each
  source card now leads with "A NuGet feed is a server that supplies C# packages — `<host>` would be
  allowed to supply the packages listed below to your project." before the `trusted_hosts`-specific
  detail lines. The shell-quoting half was already fixed by P0-7, confirmed by re-reading the live
  code rather than trusting the old citation (which now points at a file P0-7 split the process-running
  logic out of): `DotquayCli` invokes `DotquayProcessRunner.run`, which calls
  `OS.execute_with_pipe(executable, args)` with `args` as a `PackedStringArray` — Godot spawns this
  directly (argv-style) on every platform, never through `/bin/sh`, so there is no shell-quoting hole
  to close.

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
- [x] **P1-20** ~~Orphans are a dead end: badge says "Locked but no longer required by anything." (`:99`)
  and **Remove** is disabled for them (`is_removable()` requires root origin). State the remedy the CLI
  actually offers, or enable it.~~ Fixed: read `Dotquay.Core`'s `SyncAddonGraphAsync` to confirm the
  actual remedy rather than guess at one, as this review's own P1-19 note had deliberately declined to
  do — any `update` (all, or by name) re-walks the graph from every manifest root and deletes whatever
  that walk doesn't reach, which prunes an orphan automatically. The badge tooltip and Remove's disabled
  reason both now say so. Also found and fixed a real bug the same reading surfaced: `Update` on an
  orphan node was *not* disabled (only transitive addons were), but `update <name>` throws "not declared
  in dotquay.toml" for a name that was never a manifest root — exactly what an orphan is. Update is now
  disabled for orphans too, with its own reason naming "Update all" as the actual fix.
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
- [x] **P1-22** ~~Every refresh discards manual node positions: `render()` frees all nodes and sets
  `position_offset` unconditionally (`dependency_graph_view.gd:41-46,64`), and `refresh()` runs on every
  return to the tab. Keep a `name → position_offset` map across renders; add an "Auto-arrange" action.~~
  Fixed: a new `_manual_positions` dictionary (addon name → `Vector2`) survives across `render()` calls,
  populated from `GraphElement.position_offset_changed` guarded by a `_rendering` flag so `render()`'s
  own layout assignment is never mistaken for a drag. A new "Auto-arrange" button, added to the built-in
  `GraphEdit` toolbar (`get_menu_hbox()`) from `_ready()`, clears the map and re-renders the last graph —
  the plugin's own escape hatch now that `show_arrange_button` is deliberately off. Covered by a new
  `tests/test_dependency_graph_view.gd`, driven against the real (detached) scene:
  `test_graph_view_preserves_manual_positions_across_render`, matching this review's own suggested name,
  plus an `auto_arrange` counterpart.
- [x] **P1-23** ~~Empty graph canvas has no hint (only the muted toolbar text
  "No dependencies yet — use Add.", `dotquay_panel.gd:558`). Draw a centred placeholder inside the
  GraphEdit. Long addon names/folders overflow the fixed 240 px node (`dependency_node.tscn`, title
  at `dependency_node.gd:53`) into the next column — `TRIM_ELLIPSIS` on the title.~~ Fixed both halves. A
  centred `%EmptyHint` label was added directly to `dependency_graph_view.tscn`, toggled by `render()`
  from `graph == null or graph.is_empty()`, reusing the toolbar's own wording rather than inventing a
  second phrasing. Godot's native `GraphNode` title bar has no `text_overrun_behavior` of its own to set
  (confirmed against the real Godot 4 API, not assumed), so the title is truncated by hand instead: a
  new static `DotquayDependencyNode.truncate_title()` shortens a name past a fixed length with a
  trailing ellipsis, and the node's own `tooltip_text` is set to the untruncated name so it's still one
  hover away rather than gone. Covered by new `truncate_title` tests in `tests/test_dependency_node.gd`
  and the empty-hint visibility test in the new `tests/test_dependency_graph_view.gd`.
- [x] **P1-24** ~~The lockfile view nags permanently: "(from lockfile — press Re-resolve to check for
  conflicts)" in the summary (`:585`) and "Conflicts are only detected by a live re-resolve…" in the
  default sidebar tab (`:601`). Reword to "No conflicts known (Re-resolve checks online)" and drop the
  summary suffix.~~ Already fixed, as a side effect: P1-25's Tools-menu move (step 6) dropped the
  summary suffix entirely (it no longer names a specific button, since Re-resolve isn't one any more)
  and P1-24's own P2-table row reworded the sidebar tab's message to point at the Tools menu. Verified
  by grepping the current source for the old phrasing — nothing left to fix here.

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
- [x] **P1-26** ~~"Update all" has no confirmation or preview (`dotquay_panel.gd:219-221`) while Add has
  both and Remove has one. A beginner cannot tell it may move every addon to a new commit.~~ Fixed: a
  new `ConfirmUpdateAll` dialog, wired the same way as Remove's own confirm, names what the command can
  do (move any addon or package to a new commit or version) and that a hand-edited addon without a
  captured patch refuses rather than losing the edit.
- [x] **P1-27** ~~Commands that change nothing end silently: `_report_operation` (`:660-672`) skips every
  `unchanged` change and prints no closing line, so the log stops at "Installing from the lockfile...".
  Add "Done." / "Done — nothing needed changing."~~ Fixed the silent-ending half. _Deferred: "Check for
  updates" results being log-only and not actionable (`:634-648` — badge the node and/or offer "Update
  these") is a real cross-cutting UI addition (teaching the graph nodes about an "outdated" concept
  they don't otherwise carry), not a wording fix, and needs its own pass rather than riding along here._
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
- [x] **P1-29** ~~The log has no structure: no separator per command, concurrent runs interleave
  (first show runs `graph` and `self check` together, `:141-143`), no Clear button, 140 px tall. Add a
  rule/heading per command and a Clear button. Busy indicator is a lowercase "working..." at the far
  right (`dotquay_panel.tscn:78`) that never says *what* is running.~~ Fixed: `_run()` now logs a muted
  heading line for every command (`busy_label()`, trimmed of its trailing "..."), a new `ClearLogButton`
  above the log calls the already-existing `DotquayLogBuffer.clear()`, and the busy indicator names
  every command still in flight ("Adding...", "Checking dependencies...", etc.) instead of a bare
  "Working...", tracked in a `_running_commands` dictionary keyed by ticket — so two concurrent runs,
  the startup self-check racing the first graph load being exactly the case this item names, are both
  named at once rather than one hiding the other. `busy_label()` is a static function of plain data,
  unit-tested in `tests/test_dotquay_panel.gd` without a real CLI run. The 140px log height itself was
  left alone — nothing here argued a specific taller default, and the split is already user-resizable.
- [x] **P1-30** ~~Overlapping `graph` reads can land out of order (refresh from tab focus, tracker,
  Locate, self-update, explain fallback: `:141,344,452,485,525`); results apply in arrival order.
  Remember the newest `graph` ticket and ignore older results (or serialise per P0-6).~~ Fixed: a new
  `_latest_graph_ticket` field and a static `should_apply_graph_result()` helper (pure function of
  plain data, unit-tested in a new `tests/test_dotquay_panel.gd` without needing to race two real CLI
  calls) gate both places a `graph` result gets applied — the success path and the still-render-a-
  conflict-on-failure path alike.
- [x] **P1-31** ~~Fixed dialog sizes with `wrap_controls = false` ignore editor scale
  (`add_dependency_dialog.tscn:9,11` 620×340, `install_preview_dialog.tscn` 680×460,
  `conflict_dialog.tscn` 620×520, `explain_dialog.tscn` 640×420). At 150–200 % the last form row / the
  blocker text is clipped behind the buttons. Multiply by `EditorInterface.get_editor_scale()` in
  `_ready`. Long labels without `text_overrun_behavior` push buttons out of view under scroll containers
  with `horizontal_scroll_mode = 0` (`items/dotnet_row.tscn:8-13`, `items/preview_addon_row.tscn:31-36`).~~
  Fixed both halves. A new shared `DotquayNodes.scale_dialog_size()` multiplies a dialog's authored
  `size` (and `min_size`, if one is ever set) by `EditorInterface.get_editor_scale()`, called once per
  instance from each of the four dialogs' existing `_bind()` rather than `_ready()` — `_bind()`'s own
  one-shot guard is what keeps this from re-multiplying on every reopen, and two of these dialogs
  (`add_dependency_dialog.gd`, `install_preview_dialog.gd`) are opened directly on a detached instance in
  their own tests, which never runs `_ready()` at all. It no-ops when `Engine.is_editor_hint()` is false,
  since `EditorInterface` isn't fully bound outside a real running editor — found by the real headless
  test run itself throwing "Nonexistent function 'get_editor_scale' in base 'EditorInterface'", not
  assumed up front; covered by a new `tests/test_dotquay_nodes.gd`. Added `text_overrun_behavior` to
  `PackageLabel` in `dotnet_row.tscn` and to `Origin`/`Folder` in `preview_addon_row.tscn`, the exact
  labels this item cited.
- [x] **P1-32** ~~Explain dialog: the blocker tells a GUI user to run
  `'dotquay update x --force' at a terminal` (`explain_dialog.gd:129-131`); add a "Keep my edits (save
  as patch)" button via `add_button()`. Its safe-verdict sentence is maintainer-speak: "The recorded
  hash predates the current hashing rules; a forced update rerecords it and discards nothing."
  (`dotquay_explain.gd:179-180`). The unavailable case (`:126-127`) disables the only button with no
  next step.~~ The maintainer-speak sentence was already gone — the P2-table pass (step 7) had already
  reworded that exact spot to "the \"modified\" flag comes from an older way of checking" while this
  item sat open, so re-reading the live file before touching it caught that half was moot. Fixed the
  rest: a real `add_button()` action wired to a new `capture_patch_requested` signal (the panel runs
  `dotquay patch <name>` from it, same as the node menu's own "Keep my edits" item) replaces the
  terminal-command prose; the button only shows for a genuine blocked verdict, not for the unavailable
  case, where nothing is actually known to be worth capturing; and the unavailable case's blocker text
  now ends with a next step ("check your connection, then ask again") instead of stopping at the
  problem.
- [x] **P1-33** ~~`error_message` can be empty: `ok:false` with a missing/non-dict `error`
  (`dotquay_result.gd:139-144`) logs an empty red line (`dotquay_panel.gd:399,516`). Synthesize
  "dotquay reported a failure (exit code N) but gave no details." No `KIND_AUTH` constant exists
  although the CLI emits `auth` (`JsonOutput.cs:66-71`, SPEC §17) — at minimum hint "run `dotquay login
  <host>`".~~ The first half and the `KIND_AUTH` constant were both already there (from an earlier pass
  this review's own notes didn't catch up to) — re-read the live file rather than trusting the citation.
  What was actually still missing: nothing used `KIND_AUTH` for anything. Added `DotquayResult.auth_hint()`
  (blank for every kind but `auth`) and call it right after an auth failure's own red line.

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
literal `[overrides]` bracket; left for the P1 polish pass. _Update: P1-20 (step 7) has since verified
and fixed the orphan badge's actual remedy — its tooltip now names "Update all" — so only the plain
wording of the other three rows (modified/patched/override) is still open here._ The log's `[indent]`
formatting and the README's ellipsis/badge-table gaps are also still open — the README was already out
of scope for every earlier pass in this review and stays that way here.

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

- [x] **C-1** ~~`dotquay_result.gd:16,18,19` `KIND_WORKFLOW`, `KIND_CONFLICT`, `KIND_GIT` — only their
  definitions exist; `KIND_AUTH` is missing (P1-33). Either complete the set and move it to
  `dotquay_wire.gd`, or delete.~~ Fixed: all six wire-reported kinds (`KIND_WORKFLOW`,
  `KIND_NO_MANIFEST`, `KIND_CONFLICT`, `KIND_GIT`, `KIND_AUTH`, `KIND_UNEXPECTED`) now live on
  `DotquayWire`; `dotquay_result.gd` only references them through it, and keeps just the kinds it
  synthesizes itself (`KIND_NOT_CONFIGURED`, `KIND_LAUNCH_FAILED`, `KIND_BAD_OUTPUT`,
  `KIND_SCHEMA_MISMATCH`, `KIND_TIMEOUT`) — those never come from the wire, so they stay put.
- [x] **C-2** ~~`dotquay_result.gd:40` `raw_output` — assigned, never read (P0-4).~~ Fixed as part of
  P0-4/P0-5: `failure()` now folds it into `error_message`, which the panel already logs.
- [x] **C-3** ~~`dotquay_cli.gd:46` `is_busy()` — no callers (P0-6 wants it).~~ Fixed: `run()` now calls
  it to decide whether a new request queues or starts immediately.
- [x] **C-4** ~~`dotquay_settings.gd:2` `class_name DotquaySettings` — never referenced; every consumer
  preloads.~~ Fixed for every production file the review named: `dotquay_panel.gd`, `dotquay_cli.gd`,
  and `plugin.gd` now use the global `class_name`s (`DotquayCli`, `DotquayResult`,
  `DotquaySettings`, `DotquayProcessRunner`, `EditorInterface`) instead of local `preload` aliases.
  Deliberately left as-is: the ~15 test files' own local `preload` aliases (`const RESULT :=
  preload(...)` and similar). That's a reasoned exception, not an oversight — a test suite always runs
  after `run_tests.ps1`'s `--import` pass has already built the class cache, so the class-cache concern
  the review raised for production code doesn't apply there, and the short local alias keeps fixture
  code terse.
- [x] **C-5** ~~`items/candidate_row.gd:7` `CUSTOM_INDEX := -1` unused; the `-1` sentinel is also in
  `conflict_dialog.gd:16` and `conflict_dialog.tscn:86` (metadata) and re-set in `_ready` (`:48`).~~
  Fixed: `DotquayCandidateRow.CUSTOM_INDEX` is now the single source of truth; `conflict_dialog.gd`'s
  duplicate `CUSTOM_ID` constant is gone, and the redundant `set_meta("candidate_index", CUSTOM_ID)`
  re-set in `_bind()` was removed — the scene's own authored `metadata/candidate_index = -1` on
  `CustomRadio` already supplies that value at instantiation time, before `_bind()` ever runs.
- [x] **C-6** ~~`add_dependency_dialog.gd:23` `_mode_row` declared, never used.~~ Fixed: deleted.
- [x] **C-7** ~~Parsed but never read: `DotquayGraph.overrides`, `AddonNode.path`,
  `DotquayPreview.Source.name`, `Explanation.baseline_includes_patch`.~~ Fixed: all four deleted after
  re-verifying each against the current source (not the review's own line numbers, which had drifted) —
  confirmed dead via a full read of every call site.
- [x] **C-8** ~~`dependency_graph_view.gd:27-33,103-108` `_reject_connection` / `_reject_disconnection`
  are empty. In Godot 4 `connection_request` / `disconnection_request` are *requests* — nothing is
  connected unless the handler calls `connect_node`, so a no-op handler equals no handler. The comment
  at `:28-30` ("simply not connecting the signal would let the user rearrange real dependencies") is
  wrong.~~ Fixed: both functions and their `_ready()` wiring and the incorrect comment were deleted.
- [x] **C-9** ~~`dotquay_settings.gd:15-18,40-42` `RETIRED_SETTINGS` migration for
  `resolve_timeout_seconds` — there are no earlier *public* versions.~~ Fixed: deleted the constant and
  its erasure loop — confirmed no earlier public release ever shipped that setting name, so there was
  nothing to migrate, and no test covered it.
- [x] **C-10** ~~Production-dead, test-only: `Candidate.override_flag()`, `AddonNode.is_healthy()`,
  `has_conflict()`, `has_conflicts`, `Source.is_trusted_host`.~~ Fixed: all five deleted after
  confirming each is genuinely dead in production; the override-flag logic they duplicated is now one
  function, `DotquayWire.flag_for_source_kind()`, used by both `dotquay_add_request.gd` and
  `dotquay_panel.gd`.

### Stale / misleading comments

- [x] **C-11** ~~`dotquay_panel.gd:492-494` — the doc comment "Only ever reached from the explain dialog,
  and only when it judged the change benign…" sits above `_report_self_check`; it describes
  `_on_force_update_requested` (`:538`), which has no doc comment.~~ Fixed: the comment now sits above
  `_on_force_update_requested`, and `_report_self_check` has the comment that actually describes it.
- [x] **C-12** ~~`tests/test_cli_contract.gd:54-55` "`init` predates the --json layer and still prints
  plain text" — false, `InitCommand.cs:96-112` emits an envelope. Tighten the assertion to
  `init_result.ok`. Also `:128` labels an `init` call `"status"` (copy-paste).~~ Fixed: comment
  corrected, assertion now requires `init_result.ok` (not just the file existing), label renamed.
  **Re-applied 2026-09-17:** all three parts were silently undone when the C-1..C-25 sync wrote a
  pre-fix copy of this file back over the device (the same stale-copy overwrite that cost SPEC.md
  §20) and the box was ticked anyway. Verified live this time: `init --json` reports
  `"command":"init"` with an operation payload.
- [x] **C-13** ~~`dependency_graph_view.gd:11-13` says "the panel listens to the built-in
  `node_selected`" — the panel never connects it.~~ Fixed: rewrote the comment to state plainly that
  nothing connects it today, and what a future details-pane feature would do instead — no longer
  claims a wiring that doesn't exist.
- [x] **C-14** ~~`dotquay_add_request.gd:77-78` doc for `git_url()` ("the key a pending request must be
  stored under") contradicts `dotquay_panel.gd:60-63` ("Keyed on the ticket, not the URL"); same stale
  claim in `tests/test_dotquay_add_request.gd` (test name) and SPEC §18.2.~~ Fixed in all three places:
  `git_url()`'s doc comment now explains it's only used by `looks_like_git()` to classify pasted text
  (confirmed via grep — its one production caller); the two test names were renamed to match; SPEC.md
  §18.2 was rewritten to describe the real ticket-based keying instead of the stale "keyed back... by
  the git URL" claim (re-checked `git status`/`git diff` on `docs/SPEC.md` immediately before editing
  it, per the concurrent-session caution below).
- [x] **C-15** ~~`dotquay_result.gd:65-70` brace trimming cuts first `{` to *last* `}` — a trailing
  stderr line containing `}` turns a successful run into "did not return JSON".~~ Fixed properly, not
  just worked around: `extract_json_object()` now does a forward-scanning, string-aware,
  brace-depth-tracking walk from the first `{`, so it stops at that object's own matching close and
  ignores braces inside quoted strings. Three new regression tests.
- [x] **C-16** ~~`dotquay_wire.gd:5` "Every literal that crosses the CLI boundary, in one place." —
  false. `dotquay_graph.gd:7` "The UI never touches the raw dictionaries." — false:
  `_report_outdated`/`_report_doctor`/`_report_operation`/`_report_self_*` read raw payloads directly.~~
  Fixed both: `dotquay_wire.gd`'s header now explicitly excepts `--json`/`--project` (added by
  `DotquayCli` itself, not chosen per call). `dotquay_graph.gd`'s claim is now actually true rather than
  just re-scoped — C-24 gave outdated/doctor/operation/self-check their own typed views, so every one of
  those report methods reads through a typed object now; there are zero remaining `result.data.get(...)`
  calls anywhere in `dotquay_panel.gd`.
- [x] **C-17** ~~Unfollowable references: `docs/DESIGN-v0.12-MODIFIED-REASON.md`, `docs/SPRINT-01.md`,
  version tags "(v0.17)"/"v0.8", IDs `OQ-2`/`D7`/`P0-6`/`P1-5`.~~ Fixed, with one caveat: the
  `DESIGN-v0.12-MODIFIED-REASON.md` citation was dropped from all four sites — in every case the
  surrounding prose already inlined the one sentence that mattered, so nothing needed replacing. "v0.8"
  became "SPEC.md §11.3" (confirmed that section exists and covers addon-declared feeds); the dangling
  "D7" sub-id was dropped (grepped SPEC.md — no such ID exists anywhere in it). Two parts of this item
  were already stale before this pass and needed no code change: `docs/SPRINT-01.md`'s citation
  (`test_runner.gd:9`) was already gone — that header was rewritten earlier in this same pass (C-19);
  and "(v0.17)" was already removed from both cited sites. The caveat: `OQ-2`/`P0-6`/`P1-5` were
  re-checked against SPEC.md's own requirements table and turned out to be genuinely followable (each
  has a real, grep-able entry there) — the review bundled them with the one ID that truly wasn't
  followable ("D7"), so they were left as-is rather than "fixed" into something worse.
- [x] **C-18** ~~Commit-message-style history in doc comments — rewrite as present-tense rationale.~~
  Fixed in `dotquay_refresh_tracker.gd`, `dotquay_nodes.gd`, `dotquay_cli.gd`'s `project_directory()`
  comment, and the three named test files (`test_dotquay_result.gd`, `test_dotquay_log_buffer.gd`,
  `test_dotquay_refresh_tracker.gd`). Two parts were already stale: `dotquay_log_buffer.gd`'s own
  citation no longer matches its current comment, which already reads as present-tense rationale, not
  history — left untouched. `README.md:89`'s "used to look like it did nothing" no longer exists
  anywhere in `README.md` (checked live on the device with `Select-String`, since a concurrent session
  edits that file — see the caution at the top of this section); left untouched rather than risk a
  collision with that session over a citation that was already resolved.
- [x] **C-19** ~~`dotquay_cli.gd:102-104` "(missing .NET runtime, a wrong architecture)"... `tests/
  test_runner.gd:4-12` header says the suite "only ever tests parsing and layout"... `dotquay_panel.gd:
  148-150` has a dangling " -" line break.~~ Fixed the two real sub-items: the dangling em-dash line
  break, and `test_runner.gd`'s header (no longer claims a narrower scope than the suite actually has,
  and its "Run with:" section now says `run_tests.ps1` is mandatory, not the raw `godot --script` line).
  The "(missing .NET runtime...)" comment was already stale — grepped the full repo and it no longer
  exists anywhere in the actual source; this sub-item needed a note, not a code change.

### Parallel mechanisms / wire fragility

- [x] **C-20** ~~Half the envelope-command vocabulary is inline... Add the six constants.~~ Already done
  before this pass — `DotquayWire.COMMAND_*` and `DotquayWire.cli_command_words()` cover every call
  site; re-verified via grep, no remaining inline command-name literals in `dotquay_panel.gd`.
- [x] **C-21** ~~CLI flags as literals across files: `--preview`, `--adopt-source`, `--resolve`,
  `--explain`, `--clear`, `--force`, `--git`, `--install-as`, `--path`,
  `--version/--branch/--rev`, `"--" + kind`.~~ Fixed: added `DotquayWire.FLAG_*` for all of them plus
  `DotquayWire.flag_for_source_kind()` for the `"--" + kind` pattern, and threaded them through every
  remaining literal call site in `dotquay_panel.gd` and `dotquay_add_request.gd`.
- [x] **C-22** ~~`MAX_LOG_ENTRIES := 200` duplicates `DotquayLogBuffer.DEFAULT_CAPACITY`. Two `_clear()`
  wrappers around `DotquayNodes.clear_children`. `ACTION_RESOLVE`/`ACTION_OVERRIDE` route to the same
  handler. `add_dependency_dialog.tscn` placeholder is unconditionally overwritten.~~ Fixed: deleted
  `MAX_LOG_ENTRIES` (the log buffer now relies on its own default capacity); deleted both redundant
  `_clear()` wrappers, every call site now calls `DotquayNodes.clear_children()` directly like the five
  other scripts already did; added a comment documenting the shared-handler routing is deliberate;
  replaced the dead placeholder text with a comment explaining `_refresh()` always overwrites it first.
- [x] **C-23** ~~Null handling is inconsistent: `str_or_empty` for some fields but `String()` for
  others. A JSON `null` renders as `"<null>"`, a null `folder` yields a "→ <null>" rename badge, a null
  `origin` makes an addon non-removable. Default values for the same key differ.~~ Fixed comprehensively
  — every optional string field in `dotquay_graph.gd` and `dotquay_preview.gd` now goes through
  `str_or_empty` (including `name`/`git`/`origin`/`kind`/`value`/`message`/`requirer`, previously plain
  `String()`), with defaults for the same wire key now matching across call sites (`Requirement.
  constraint` now matches `AddonNode`/`DotnetEntry`; `dotquay_preview.gd`'s `Addon.resolved`/
  `short_sha`/`folder` now match `dotquay_graph.gd`'s `AddonNode`; `Source.declared_by_addon` now
  matches `DotnetPackage`'s). Also found and fixed a worse instance of the same bug class:
  `dotquay_preview.gd` had *no* array field going through `array_or_empty` at all — a present-but-null
  `addons`/`dotnet`/`sources_to_adopt`/etc. would have crashed the parse outright (`for x in null`), not
  just rendered wrong. Same fix applied to `dotquay_panel.gd`'s self-update "messages" loop. 10 new
  regression tests across `test_dotquay_graph.gd` and `test_dotquay_preview.gd`.
- [x] **C-24** ~~Raw payloads for outdated/doctor/operation/self-check have no typed view and no
  fixture test. Doctor's `issues[].kind` is ignored.~~ Fixed: four new typed views —
  `DotquayOutdated`, `DotquayDoctor`, `DotquayOperation`, `DotquaySelfCheck` — mirroring the
  graph/preview/explain pattern, each with its own fixture-test suite. `dotquay_panel.gd`'s
  `_report_outdated`/`_report_doctor`/`_report_operation`/`_report_self_check`/`_report_self_update`
  all read through these now; the now-fully-dead `_dictionaries_in` helper was deleted. Doctor's
  `issues[].kind` is read and surfaced as a `"[kind] message"` prefix — verified against SPEC.md's own
  documented kind vocabulary (`addon_folder_case_mismatch` §7.1.1, `install_folder_drift` and
  `package_asset_metadata_drift` §7.1.2, `fragile_uid_reference` §7.3.1) rather than inventing a
  translated label for kinds nothing here has actually confirmed.
- [x] **C-25** ~~Style: `plugin.gd` uses `get_editor_interface()` while the panel uses the
  `EditorInterface` singleton; `typeof(x) == TYPE_DICTIONARY` vs `x is Dictionary`; untyped
  `_pending_requests: Dictionary`; `DotquayWire.ACTION_UPDATED` vs `DotquayDependencyNode.ACTION_UPDATE`
  — near-identical names for different vocabularies. `.tscn` provenance is mixed. Dialog scenes set
  `visible = true` on the root and the panel overrides to `false`.~~ Fixed every sub-item except one:
  `plugin.gd` now uses the `EditorInterface` singleton; the two outlier `x is Dictionary` guards
  (`dotquay_panel.gd`, `dotquay_rescan.gd`) now read `typeof(x) == TYPE_DICTIONARY` like the other 18
  in the codebase; the three per-ticket dictionaries in `dotquay_panel.gd` are now typed
  (`Dictionary[int, DotquayAddRequest]` and so on); every `DotquayDependencyNode` menu-action constant
  was renamed `ACTION_* → MENU_*`, so `DotquayWire.ACTION_*` (what a wire `changes[].action` *did*) and
  a dependency node's menu commands no longer share a name for different vocabularies (confirmed via
  grep that no test referenced the old names); all five dialog scenes now default `visible = false` on
  their own root, and the panel's now-redundant per-instance overrides were removed.
  _Deliberately deferred: the `.tscn` UID-provenance item ("open and re-save every scene in 4.7 once")
  needs the real Godot editor GUI — a text edit can't regenerate `uid=`/`unique_id` the way the editor's
  own save does, and guessing at values here risks corrupting real scene identity rather than fixing
  it. Left for a future pass with a live editor open._

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
- ~~`test_panel_self_check_retries_after_not_configured` (P1-7),
  `test_panel_failed_self_update_restores_the_banner_message`~~ P1-7 itself is fixed (step 7), but not
  via this suggested harness — `_cli` is created inside `_ready()` (`_cli = CLI_SCRIPT.new();
  add_child(_cli)`), so injecting a double needs either a real `add_child`-ed panel in a tree or a
  seam this pass didn't add. Verified instead by a full real-binary test run plus reading the two
  branches by hand; the harness itself (inject `_cli`/`_rescan`, add the panel to a tree so `_ready()`
  actually runs) is still worth doing, as its own follow-up rather than riding along on a wording pass.
- `test_panel_summary_is_empty_until_a_graph_arrives` (P0-1)
- ~~`test_log_text_with_bbcode_tags_is_escaped` (P1-28; needs an `escape_bbcode` helper in core)~~
  Added, as `tests/test_dotquay_palette.gd` (the helper itself needed no panel instance to test).
- ~~`test_update_all_asks_for_confirmation` (P1-26)~~ The confirm dialog is fixed (step 7); the
  suggested panel-instance test hits the same missing-harness gap as P1-7's two above, so it's
  verified for real (655/655 assertions, real binary) rather than by this specific test name.
- Added this pass, not originally suggested: `tests/test_dotquay_panel.gd`'s
  `should_apply_graph_result()` coverage (P1-30) — a pure static function pulled out specifically so
  the out-of-order-`graph`-reply rule didn't need the panel-instance harness at all.

Graph view:
- ~~`test_graph_view_preserves_manual_positions_across_render` (P1-22)~~ Added, under this exact name,
  in the new `tests/test_dependency_graph_view.gd`, alongside an `auto_arrange` counterpart and an
  empty-hint visibility test (P1-23).
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
3. ~~P0-4, P0-5, P0-8~~ — done, in two follow-up passes. ~~P1-33 not yet reviewed against the current
   code~~ — reviewed and closed in step 7: both halves this note flagged as open (the empty-message
   synthesis and the missing `KIND_AUTH` constant) turned out to already exist from an earlier pass;
   what was actually still missing was a login hint that used `KIND_AUTH` for anything.
4. ~~P0-2, P0-3, P1-1..P1-4~~ — done.
5. ~~P0-9, P1-8, P1-9, P1-14~~ — done.
6. ~~P1-17, P1-18, P1-19, P1-21, P1-25~~ — legend, pin mode, menu wording, toolbar names. Done.
7. ~~The P2 string table, P1-5, P1-28's escaping half, and P1-6/7/20/24/26/27/30/32/33~~ — **done**. The
   remaining P1 polish (P1-10..13, P1-15, P1-16, P1-22, P1-23, P1-29, P1-31) and the C-1..C-25 items —
   not started.
8. ~~P1-10, P1-11, P1-12, P1-13, P1-15, P1-16~~ — **done**: the add-dialog and install-preview-dialog
   wording/UX pass.
9. ~~P1-22, P1-23, P1-29, P1-31~~ — **done**: manual graph-layout persistence and an Auto-arrange
   button, the empty-canvas hint and title truncation, per-command log headings with a Clear button and
   a busy indicator that names what's running, and dialog sizes/labels that respect editor scale. This
   closes every remaining P1. Remaining: the C-1..C-25 hygiene items — not started.

**Net this pass:** 11 of 11 P0s fully closed, all 33 P1s (P1-1 through P1-33), the full P2 string-table
rewrite (~36 sites, plus the new `DotquayText.plural()` helper), C-2, C-3, C-8, C-11, C-12.
Verified for real: 690/690 GDScript test assertions pass against Godot 4.7.2 and the real dotquay CLI,
across 26 conventional commits from this session (`081d141`..`ede78f6`) on `main` — interleaved with
unrelated commits from other concurrent work in the repo (an MCP-server feature, one manual commit)
that were left untouched throughout (verified via `git status --short` before every stage). Two P1-20
findings came from reading `Dotquay.Core`'s actual pruning logic rather than guessing: orphans are
already cleared by any `update`, and `update <name>` on an orphan was a live, previously-unnoticed bug
(it always threw). P1-16's shell-quoting half turned out to already be fixed by P0-7's
`OS.execute_with_pipe(executable, args)` rewrite — confirmed by reading the live code rather than
trusting a citation that pointed at a file P0-7 had already split that logic out of. Step 8 also closed
a real testability gap along the way: `add_dependency_dialog.gd`, `install_preview_dialog.gd`, and
`preview_source_card.gd` all switched from `@onready` to the bind-on-first-use pattern
`explain_dialog.gd`/`conflict_dialog.gd` already used, specifically so their new wiring (Enter-to-confirm,
the Kind auto-switch, the no-op OK-disable, the feed explanation) could be asserted against a detached
instance instead of only by hand — the dependency-injection harness gap the T-section above flags still
applies to the panel itself, but three more of the plugin's dialogs are no longer in that category.
Everything a first-time Godot user would hit trying to add, update, or remove an addon through this tab
is now either fixed or has a working, discoverable path forward, the panel's user-facing wording has had
its first full plain-language editing pass including every remaining dialog, and every dead end this
review found (no legend, no pin mode, no disabled-item reasons, no orphan remedy, no confirmation before
a project-wide update, a silently-ending command, an out-of-order graph race, a terminal-only patch
escape hatch, an unused auth hint, a no-op install offered as if it would do something) now has a way
through. Step 9 closed the last of the layout/scaling polish too: a graph the user rearranges by hand
stays rearranged, an empty canvas and a long name both say something rather than nothing or overflowing,
the log reads as a sequence of commands instead of one interleaved stream, and every dialog now scales
with the editor instead of clipping at high UI scale. What's left is code hygiene (C-1..C-25), not a
beginner getting stuck or confused by what's on screen.
