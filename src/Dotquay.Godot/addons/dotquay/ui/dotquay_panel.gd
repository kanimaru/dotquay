@tool
class_name DotquayPanel
extends Control

## The main-screen panel: toolbar, dependency graph, sidebar, and log (SPEC.md §18).
##
## Every button here maps to exactly one CLI invocation. The panel holds no model of its own beyond
## the last graph it was handed — after any mutating command it re-runs `graph` rather than patching
## its state, so what's on screen is always something dotquay actually said, never something the UI
## inferred.
##
## The layout is [code]dotquay_panel.tscn[/code]; this script binds data to it. Nothing here builds a
## container or sets an anchor, which is what keeps a change to the panel's shape out of the code that
## talks to the CLI.

const CLI_SCRIPT := preload("res://addons/dotquay/core/dotquay_cli.gd")
const SETTINGS := preload("res://addons/dotquay/core/dotquay_settings.gd")
const RESULT := preload("res://addons/dotquay/core/dotquay_result.gd")

const CONFLICT_CARD := preload("res://addons/dotquay/ui/items/conflict_card.tscn")
const DOTNET_ROW := preload("res://addons/dotquay/ui/items/dotnet_row.tscn")
const STATUS_LABEL := preload("res://addons/dotquay/ui/items/status_label.tscn")

const MAX_LOG_ENTRIES := 200

@onready var _graph_view: DotquayDependencyGraphView = %GraphView
@onready var _summary: Label = %Summary
@onready var _log: RichTextLabel = %Log
@onready var _conflict_list: VBoxContainer = %ConflictList
@onready var _dotnet_list: VBoxContainer = %DotnetList
@onready var _busy: Label = %Busy
@onready var _toolbar: HBoxContainer = %Toolbar

@onready var _setup_banner: PanelContainer = %SetupBanner
@onready var _setup_message: Label = %SetupMessage
@onready var _get_dotquay_button: Button = %GetDotquayButton
@onready var _browse_button: Button = %BrowseButton
@onready var _locate_button: Button = %LocateButton
@onready var _init_button: Button = %InitButton

## Built in code, not the scene, unlike every other dialog this panel owns: `EditorFileDialog`
## engine-refuses to be instantiated as part of a hand-authored `.tscn` outside a real running
## editor ("Class 'EditorFileDialog' can only be instantiated by editor") — which is exactly the
## context `tests/test_scenes.gd` loads every scene in, headless and script-driven. Built here in
## `_ready()` instead, which the scene-loading tests never call (they `.instantiate()` and `.free()`
## without adding the panel to a tree), so it never collides with that restriction.
var _cli_path_dialog: EditorFileDialog

@onready var _update_banner: PanelContainer = %UpdateBanner
@onready var _update_message: Label = %UpdateMessage
@onready var _update_button: Button = %UpdateButton
@onready var _update_dismiss_button: Button = %UpdateDismissButton

@onready var _conflict_dialog: DotquayConflictDialog = %ConflictDialog
@onready var _add_dialog: DotquayAddDependencyDialog = %AddDialog
@onready var _preview_dialog: DotquayInstallPreviewDialog = %PreviewDialog
@onready var _explain_dialog: DotquayExplainDialog = %ExplainDialog
@onready var _legend_dialog: AcceptDialog = %LegendDialog
@onready var _confirm_remove: ConfirmationDialog = %ConfirmRemove
@onready var _confirm_overwrite_cli_path: ConfirmationDialog = %ConfirmOverwriteCliPath

var _cli: DotquayCli
var _graph: DotquayGraph

## Mirrors [signal DotquayCli.busy_changed]; kept separately from [member _busy]'s visibility because
## [method _update_toolbar_state] needs busy state whenever readiness changes too, not only when
## busy itself changes.
var _is_busy := false

## Re-imports what the CLI wrote. Owned here rather than by [DotquayCli] because it reacts to what a
## command *reported*, not to a process exiting — an `install` that changed nothing should not make
## the editor walk the project.
var _rescan: DotquayRescan

var _pending_removal := ""

## The path a "Find dotquay on PATH" just discovered, held here only while
## [member _confirm_overwrite_cli_path] is asking whether to replace an already-configured, different
## path (P1-4). Empty means nothing is pending confirmation.
var _pending_locate_path := ""

## CLI ticket to the [DotquayAddRequest] whose preview that run is producing. Keyed on the ticket, not
## the URL: several previews can be in flight, a failed one must not leave its request behind to be
## picked up by the next preview of the same URL (with the old `--path`/`--install-as`), and the
## ticket is the one thing a result carries that names exactly the call it answers.
var _pending_requests: Dictionary = {}

## CLI ticket to the addon name that run's `status --explain` was opened about. Keyed on the ticket
## for the same reason as [member _pending_requests]: `status` reports every addon, so the reply alone
## does not say which one the user asked about.
var _pending_explains: Dictionary = {}

## Which in-flight runs must be followed by a graph refresh. A tracker rather than a flag, because a
## flag is consumed by whichever command finishes first — see [DotquayRefreshTracker].
var _refresh_tracker := DotquayRefreshTracker.new()

## The log's contents, in message units. The [RichTextLabel] is a view of this, rebuilt on change.
var _log_buffer := DotquayLogBuffer.new(MAX_LOG_ENTRIES)

## Whether this editor session has already asked the release channel about the CLI (v0.17). Once,
## not once per panel focus: [method refresh] runs every time the tab is shown, and reaching the
## network on each of those would be a worse habit than the one this feature is fixing.
var _self_checked := false

## Whether the last graph result implied a working CLI and an existing manifest. Buttons that mutate
## the project are gated on this rather than just on busy state, so a fresh, unconfigured project
## can't be told to `install` or `update` before there is anything for those commands to act on.
var _project_ready := false


func _ready() -> void:
	_cli = CLI_SCRIPT.new()
	_cli.name = "DotquayCli"
	_cli.finished.connect(_on_cli_finished)
	_cli.busy_changed.connect(_on_busy_changed)
	add_child(_cli)

	_log_buffer.changed.connect(_render_log)

	_wire_toolbar()
	# Nothing is known to be ready yet — gate the mutating buttons immediately rather than leaving
	# them clickable until the first busy_changed or graph result arrives.
	_update_toolbar_state()

	_graph_view.node_action_requested.connect(_on_node_action)
	_add_dialog.add_requested.connect(_on_add_requested)
	_preview_dialog.install_confirmed.connect(_on_install_confirmed)
	_explain_dialog.force_update_requested.connect(_on_force_update_requested)
	_conflict_dialog.override_chosen.connect(_on_override_chosen)
	_confirm_remove.confirmed.connect(_on_remove_confirmed)
	_confirm_overwrite_cli_path.confirmed.connect(_on_overwrite_cli_path_confirmed)
	_get_dotquay_button.pressed.connect(_on_get_dotquay_pressed)
	_browse_button.pressed.connect(_on_browse_pressed)

	_cli_path_dialog = EditorFileDialog.new()
	_cli_path_dialog.title = "Select the dotquay executable"
	_cli_path_dialog.access = EditorFileDialog.ACCESS_FILESYSTEM
	_cli_path_dialog.file_mode = EditorFileDialog.FILE_MODE_OPEN_FILE
	_cli_path_dialog.file_selected.connect(_on_cli_path_selected)
	add_child(_cli_path_dialog)

	_locate_button.pressed.connect(_on_locate_pressed)
	_init_button.pressed.connect(_on_init_pressed)
	_update_button.pressed.connect(_on_self_update_pressed)
	_update_dismiss_button.pressed.connect(_on_self_update_dismissed)

	_busy.add_theme_color_override("font_color", DotquayPalette.WARN)
	_setup_message.add_theme_color_override("font_color", DotquayPalette.WARN)

	_rescan = DotquayRescan.new(EditorInterface.get_resource_filesystem())
	# Polling only while a scan is owed. Defining `_process` at all turns processing on, and this
	# panel is idle almost all of the time it is open.
	set_process(false)

	# No refresh here: the plugin adds this panel hidden and refreshes it on first show, so a refresh
	# in _ready would only ever run the CLI twice for one tab click.

	# Belt-and-braces alongside the scene's own default: the scene should never carry placeholder
	# text, but the script owning the truth means a bad scene default can't put a fake summary on
	# screen before a real graph ever comes back.
	_summary.text = ""


## Only ever running while [member _rescan] is waiting for the editor to finish a scan of its own.
func _process(_delta: float) -> void:
	set_process(_rescan.pump())


## Ids for the Tools overflow menu (P1-25) — Re-resolve and Doctor moved here off the main toolbar,
## since neither is a first-click action. Positional like [DotquayDependencyNode]'s own menu, and for
## the same reason: the action a click means must survive the menu being reordered or relabelled.
const TOOLS_ITEM_RESOLVE := 0
const TOOLS_ITEM_DOCTOR := 1


## The toolbar's buttons live in the scene, so the binding is by node name rather than by a builder
## that would have to duplicate the scene's ordering to stay readable.
func _wire_toolbar() -> void:
	var handlers := {
		"AddButton": _on_add_pressed,
		"InstallButton": _on_install_pressed,
		"UpdateAllButton": _on_update_all_pressed,
		"OutdatedButton": _on_outdated_pressed,
		"RefreshButton": refresh,
		"LegendButton": _on_legend_pressed,
	}

	for node_name in handlers:
		(_toolbar.get_node(node_name) as Button).pressed.connect(handlers[node_name])

	var tools_popup := (_toolbar.get_node("ToolsButton") as MenuButton).get_popup()
	tools_popup.add_item("Check for conflicts (online)", TOOLS_ITEM_RESOLVE)
	tools_popup.add_item("Run doctor", TOOLS_ITEM_DOCTOR)
	tools_popup.id_pressed.connect(_on_tools_menu_id_pressed)


func _on_tools_menu_id_pressed(id: int) -> void:
	match id:
		TOOLS_ITEM_RESOLVE:
			resolve()
		TOOLS_ITEM_DOCTOR:
			_on_doctor_pressed()


func _on_legend_pressed() -> void:
	_legend_dialog.popup_centered()


## Reloads the graph from the lockfile — fast, offline, and safe to call on every panel focus.
func refresh() -> void:
	_run(DotquayWire.COMMAND_GRAPH, PackedStringArray(["graph"]))
	_check_for_cli_update()


## Asks the CLI whether a newer one exists, at most once per editor session.
##
## The plugin is asking on behalf of a binary it does not own: the thing being replaced is the
## `dotquay` executable this panel shells out to, not the plugin, so nothing here needs a restart -
## only a re-read of the version, which the next envelope carries anyway.
func _check_for_cli_update() -> void:
	if _self_checked:
		return

	_self_checked = true
	_run(DotquayWire.COMMAND_SELF_CHECK, PackedStringArray(["self", "check"]))


## Re-walks live git refs. The only mode that can discover a conflict (SPEC.md §8), and the only one
## that costs network time — so it is always an explicit action, never automatic.
func resolve() -> void:
	_log_line("Re-resolving against live git refs — this can take a moment.")
	_run(DotquayWire.COMMAND_GRAPH, PackedStringArray(["graph", "--resolve"]))


# ---------------------------------------------------------------------------------------------
# Commands
# ---------------------------------------------------------------------------------------------

## Returns the run's ticket so a caller can key state on it (see [member _pending_requests]).
func _run(command_name: String, args: PackedStringArray, refresh_after: bool = false) -> int:
	var ticket := _cli.run(command_name, args)
	if refresh_after:
		_refresh_tracker.arm(ticket)
	return ticket


func _on_add_pressed() -> void:
	_add_dialog.open()


## A git source is previewed first; a NuGet package has no dependency graph and declares no feeds, so
## there would be nothing in the dialog to read.
func _on_add_requested(request: DotquayAddRequest) -> void:
	if not request.is_git:
		_log_line("Adding %s..." % request.describe())
		var ticket := _run(DotquayWire.COMMAND_ADD, request.to_args(), true)
		_pending_requests[ticket] = request
		return

	_log_line("Resolving what adding %s would install..." % request.describe())

	var args := request.to_args()
	args.append("--preview")
	var ticket := _run(DotquayWire.COMMAND_ADD_PREVIEW, args)
	_pending_requests[ticket] = request


## Runs the add the preview described, carrying the user's feed approvals as `--adopt-source`.
func _on_install_confirmed(request: DotquayAddRequest, adopt_source_urls: PackedStringArray) -> void:
	var args := request.to_args()

	for url in adopt_source_urls:
		args.append("--adopt-source")
		args.append(url)

	_log_line("Adding %s..." % request.describe())

	for url in adopt_source_urls:
		_log_line("  adopting NuGet feed %s" % url)

	var ticket := _run(DotquayWire.COMMAND_ADD, args, true)
	# Kept under the plain request, not the one carrying --adopt-source: a failure here re-opens the
	# add dialog (P1-9), which only ever collects what this request already holds — the adopted feeds
	# are the preview dialog's business, and re-approving them is what re-previewing is for.
	_pending_requests[ticket] = request


func _on_install_pressed() -> void:
	_log_line("Installing from the lockfile...")
	_run(DotquayWire.COMMAND_INSTALL, PackedStringArray(["install"]), true)


func _on_update_all_pressed() -> void:
	_log_line("Updating everything...")
	_run(DotquayWire.COMMAND_UPDATE, PackedStringArray(["update"]), true)


func _on_outdated_pressed() -> void:
	_run(DotquayWire.COMMAND_OUTDATED, PackedStringArray(["outdated"]))


func _on_doctor_pressed() -> void:
	_run(DotquayWire.COMMAND_DOCTOR, PackedStringArray(["doctor"]))


func _on_node_action(action: String, addon_name: String) -> void:
	match action:
		DotquayDependencyNode.ACTION_UPDATE:
			_log_line("Updating %s..." % addon_name)
			_run(DotquayWire.COMMAND_UPDATE, PackedStringArray(["update", addon_name]), true)

		DotquayDependencyNode.ACTION_REMOVE:
			_pending_removal = addon_name
			# Shared by addons and NuGet packages, so it names what `remove` does for both rather than
			# promising a NuGet package will disappear from addons/.
			_confirm_remove.dialog_text = (
				"Remove '%s' from dotquay.toml and the lockfile?\n\nWhat it installed is removed too, along with anything only it required."
				% addon_name)
			_confirm_remove.popup_centered()

		DotquayDependencyNode.ACTION_RESOLVE, DotquayDependencyNode.ACTION_OVERRIDE:
			_open_conflict_dialog(addon_name)

		DotquayDependencyNode.ACTION_CLEAR_OVERRIDE:
			_log_line("Clearing the override on %s..." % addon_name)
			_run(DotquayWire.COMMAND_OVERRIDE, PackedStringArray(["override", addon_name, "--clear"]), true)

		DotquayDependencyNode.ACTION_CAPTURE_PATCH:
			_log_line("Capturing edits to %s as a patch..." % addon_name)
			_run(DotquayWire.COMMAND_PATCH, PackedStringArray(["patch", addon_name]), true)

		DotquayDependencyNode.ACTION_EXPLAIN:
			# `--explain` compares against the locked commit, which can mean a fetch — so it is said
			# out loud rather than leaving the panel silently busy.
			_log_line("Comparing %s against its locked commit..." % addon_name)
			var ticket := _run(DotquayWire.COMMAND_STATUS, PackedStringArray(["status", "--explain"]))
			_pending_explains[ticket] = addon_name

		DotquayDependencyNode.ACTION_OPEN_REPO:
			var node := _graph.find(addon_name) if _graph != null else null
			if node == null or node.git.is_empty():
				return

			# `shell_open` needs something a browser understands. An scp-style `git@host:repo` or a
			# local path is a valid git source and an invalid URL, so it's reported rather than fed to
			# the OS to fail silently.
			if _is_web_url(node.git):
				OS.shell_open(node.git)
			else:
				_log_warning("'%s' is not a web URL, so it can't be opened in a browser: %s" % [addon_name, node.git])


func _on_remove_confirmed() -> void:
	if _pending_removal.is_empty():
		return

	_log_line("Removing %s..." % _pending_removal)
	_run(DotquayWire.COMMAND_REMOVE, PackedStringArray(["remove", _pending_removal]), true)
	_pending_removal = ""


## Opens the picker for [param addon_name]. When the current graph came from the lockfile it holds
## no conflict data at all, so a synthetic empty conflict is shown instead — the free-text field
## still works, which is what "pin this to a version I choose" needs.
func _open_conflict_dialog(addon_name: String) -> void:
	if _graph == null:
		return

	var conflict: DotquayGraph.Conflict = _graph.conflict_for(addon_name)

	if conflict == null:
		conflict = DotquayGraph.Conflict.new()
		conflict.name = addon_name
		conflict.is_manual_pin = true
		conflict.message = (
			"No conflict was reported for '%s'. Pinning it here writes an [overrides] entry that wins over every other constraint."
			% addon_name)

		var node: DotquayGraph.AddonNode = _graph.find(addon_name)
		if node != null:
			var requirements: Array = node.requirements
			conflict.requirements = requirements

	_conflict_dialog.show_conflict(conflict)


func _on_override_chosen(addon_name: String, kind: String, value: String, git: String, install_as: String) -> void:
	var args := PackedStringArray(["override", addon_name, "--" + kind, value])
	if not git.is_empty():
		args.append("--git")
		args.append(git)

	if not install_as.is_empty():
		args.append("--install-as")
		args.append(install_as)

	if install_as.is_empty():
		_log_line("Pinning %s to %s %s..." % [addon_name, DotquayWire.display_source_kind(kind), value])
	else:
		_log_line("Pinning %s to addons/%s..." % [addon_name, install_as])

	_run(DotquayWire.COMMAND_OVERRIDE, args, true)


func _on_init_pressed() -> void:
	_log_line("Creating dotquay.toml...")
	_run(DotquayWire.COMMAND_INIT, PackedStringArray(["init"]), true)


## Feedback goes to the banner itself, not only the log (P1-4): the log is 120px tall and easy to
## miss, and this button exists specifically because the banner is already telling the user something
## is wrong — the answer belongs where the question was asked.
func _on_locate_pressed() -> void:
	var found := SETTINGS.discover_on_path()
	if found.is_empty():
		_setup_message.text = "Could not find dotquay on PATH either. Use Get dotquay or Browse instead."
		_log_error("Could not find dotquay on PATH.")
		return

	var editor_settings := EditorInterface.get_editor_settings()
	var configured := ""
	if editor_settings.has_setting(SETTINGS.CLI_PATH_SETTING):
		configured = String(editor_settings.get_setting(SETTINGS.CLI_PATH_SETTING)).strip_edges()

	# A blank setting (the common case: nothing configured yet) is never worth confirming — there is
	# nothing to lose. A non-blank one that differs was set on purpose by someone, even if it's
	# currently broken, and silently replacing it is the kind of "helpful" surprise that erases a
	# deliberate choice (a pinned build, a fork) with no way back except retyping it from memory.
	if not configured.is_empty() and configured != found:
		_pending_locate_path = found
		_confirm_overwrite_cli_path.dialog_text = (
			"dotquay was found at:\n%s\n\nThis replaces the path currently configured:\n%s"
			% [found, configured])
		_confirm_overwrite_cli_path.popup_centered()
		return

	_apply_located_path(found)


func _on_overwrite_cli_path_confirmed() -> void:
	if _pending_locate_path.is_empty():
		return

	_apply_located_path(_pending_locate_path)
	_pending_locate_path = ""


func _apply_located_path(found: String) -> void:
	EditorInterface.get_editor_settings().set_setting(SETTINGS.CLI_PATH_SETTING, found)
	_setup_message.text = "Found dotquay at %s. Refreshing..." % found
	_log_line("Found dotquay at %s." % found)
	refresh()


## Opens the release page in the system browser (P0-2). Nothing here can tell whether the user
## actually downloaded anything — there's no signal for that — so this only opens the page; the
## refresh that picks up a newly-installed binary is whatever the user presses next (Browse, Find on
## PATH, or Refresh).
func _on_get_dotquay_pressed() -> void:
	OS.shell_open(SETTINGS.RELEASES_URL)


func _on_browse_pressed() -> void:
	_cli_path_dialog.popup_centered_ratio(0.5)


func _on_cli_path_selected(path: String) -> void:
	EditorInterface.get_editor_settings().set_setting(SETTINGS.CLI_PATH_SETTING, path)
	_log_line("dotquay path set to %s." % path)
	refresh()


# ---------------------------------------------------------------------------------------------
# CLI results
# ---------------------------------------------------------------------------------------------

## The banner is a first-run affordance, not a second error log: it appears for every problem that
## leaves the CLI unusable, and offers exactly the buttons (if any) that fix what it's showing. Which
## buttons and which message is [DotquaySetupBanner]'s call, not this method's — pulled out as a pure
## function so those decisions are testable without a scene.
func _update_setup_banner(result: DotquayResult) -> void:
	var decision := DotquaySetupBanner.decide(result.error_kind)

	_setup_banner.visible = decision.visible
	_get_dotquay_button.visible = decision.show_get_dotquay
	_browse_button.visible = decision.show_browse
	_locate_button.visible = decision.show_locate
	_init_button.visible = decision.show_init

	if not decision.visible:
		return

	# None of these states is one any mutating command should be offered against.
	_project_ready = false
	_update_toolbar_state()

	# An empty decision message means the CLI's own error_message is the one worth showing (a wrong
	# path or a schema mismatch) — DotquaySetupBanner has no way to know that text, only the kind.
	_setup_message.text = decision.message if not decision.message.is_empty() else result.error_message


func _on_busy_changed(is_busy: bool) -> void:
	_busy.visible = is_busy
	_is_busy = is_busy
	_update_toolbar_state()


## Buttons exempt from the readiness half of the gate below: the only controls that must stay
## reachable even when nothing else is. RefreshButton is the one that can retry after the setup
## banner's fix; LegendButton opens a static reference dialog that reads nothing from the project, so
## gating it on a graph having loaded would be withholding a help screen for no reason.
const TOOLBAR_ALWAYS_READY := ["RefreshButton", "LegendButton"]


## Every mutating button is disabled while busy OR before the project is known to be ready — combined
## here rather than at each call site, since both [method _on_busy_changed] and a change to
## [member _project_ready] need the same two conditions applied together.
func _update_toolbar_state() -> void:
	for button in _toolbar.get_children():
		if not (button is Button):
			continue

		if TOOLBAR_ALWAYS_READY.has(button.name):
			button.disabled = _is_busy
		else:
			button.disabled = _is_busy or not _project_ready


func _on_cli_finished(result: DotquayResult) -> void:
	# Handled before the setup banner and before the shared error path. A self-check that could not
	# reach the release channel says nothing about whether this project is set up, and must not be
	# allowed to rewrite a banner about the project or to put a red line in the log: dotquay's
	# release project is private, and someone who has not logged in to it is in a perfectly ordinary
	# state that they should not be told off for once per session.
	if result.command == DotquayWire.COMMAND_SELF_CHECK:
		_report_self_check(result)
		_finish_refresh_cycle(result.ticket)
		return

	if result.command == DotquayWire.COMMAND_SELF_UPDATE:
		_report_self_update(result)
		_finish_refresh_cycle(result.ticket)
		return

	_update_setup_banner(result)

	for warning in result.warnings:
		_log_warning(warning)

	# Read once and forgotten here, on both outcomes, rather than at each of the three call sites that
	# populate it: a plain `add`, an `add --preview`, and the `add` a confirmed preview runs must all
	# stop being "pending" the moment this result comes back, or a later run could pick up a stale one.
	var tracked_request: DotquayAddRequest = _pending_requests.get(result.ticket)
	_pending_requests.erase(result.ticket)

	if not result.ok:
		_log_error(result.error_message)
		_pending_explains.erase(result.ticket)

		# A failed add or preview closed the dialog for nothing (P1-9): the values the user typed, and
		# the error that says what to fix, would otherwise only be findable by scrolling back through
		# the log. Reopening with both intact turns "fix and resubmit" back into an edit.
		if tracked_request != null and (
			result.command == DotquayWire.COMMAND_ADD
			or result.command == DotquayWire.COMMAND_ADD_PREVIEW):
			_add_dialog.reopen_after_failure(tracked_request, result.error_message)

		# A conflict is still a full graph payload — the whole point is to show it rather than stop
		# at the error line.
		if result.command == DotquayWire.COMMAND_GRAPH and not result.data.is_empty():
			_apply_graph(result.data)

		_finish_refresh_cycle(result.ticket)
		return

	match result.command:
		DotquayWire.COMMAND_GRAPH:
			_apply_graph(result.data)
		DotquayWire.COMMAND_ADD_PREVIEW:
			_show_preview(result, tracked_request)
		DotquayWire.COMMAND_STATUS:
			_show_explanation(result)
		DotquayWire.COMMAND_OUTDATED:
			_report_outdated(result.data)
		DotquayWire.COMMAND_DOCTOR:
			_report_doctor(result.data)
		_:
			_report_operation(result.data)
			_request_rescan(result.data)

	_finish_refresh_cycle(result.ticket)


## The CLI wrote to `addons/` as a child process, which Godot's file watcher does not see until the
## editor window regains OS focus. Asking for the scan here is what makes an install appear in the
## FileSystem dock without alt-tabbing away from Godot and back.
##
## Guarded on the payload rather than on "a mutating command finished": `install` reports every addon
## it checked, most runs change nothing, and a full project scan per no-op button press is its own
## kind of annoying.
func _request_rescan(payload: Dictionary) -> void:
	if not DotquayRescan.payload_touched_disk(payload):
		return

	_rescan.request()
	set_process(_rescan.is_pending())


## A mutating command changed the project, so the graph on screen is stale. Re-reading it (rather
## than editing the UI in place) is what keeps the panel from ever showing a state dotquay itself
## would disagree with.
func _finish_refresh_cycle(ticket: int) -> void:
	if _refresh_tracker.settle(ticket):
		refresh()


## [param request] is looked up by the caller from the ticket that produced this result, since it was
## already read out of [member _pending_requests] once (and erased) before the success/failure branch.
func _show_preview(result: DotquayResult, request: DotquayAddRequest) -> void:
	var preview := DotquayPreview.from_payload(result.data)

	# A preview whose request is gone (a reloaded plugin) still deserves its dialog: the graph is the
	# part worth reading, and the arguments it can't recover are the two optional ones.
	var effective_request := request if request != null else DotquayAddRequest.for_git(preview.git)

	_preview_dialog.show_preview(preview, effective_request)


## `status --explain` reports every addon; the ticket is what says which one the user asked about.
##
## A reply whose ticket is gone (the plugin reloaded mid-run) is dropped rather than guessed at: this
## dialog can end in a forced update, and opening it against an addon nobody named is not a mistake
## worth making to save a click.
func _show_explanation(result: DotquayResult) -> void:
	var addon_name: String = _pending_explains.get(result.ticket, "")
	_pending_explains.erase(result.ticket)

	if addon_name.is_empty():
		return

	var explanation := DotquayExplain.from_payload(result.data).explanation_for(addon_name)

	# Modified when the menu was opened, not modified now — an install or update finished in between,
	# or the user reverted the file themselves. Reporting that beats an empty dialog.
	if explanation == null:
		_log_line("'%s' is no longer modified — nothing to explain." % addon_name)
		refresh()
		return

	_log_line("%s" % explanation.summarize(addon_name))
	_explain_dialog.show_explanation(addon_name, explanation)


## Hides the update banner on ANY failure, without ever showing an error for it: dotquay's release
## channel can be a private project, and a user who hasn't logged in to it is in a perfectly ordinary
## state, not something worth a red error line.
func _report_self_check(result: DotquayResult) -> void:
	if not result.ok:
		_update_banner.visible = false
		return

	var latest := String(result.data.get("latest_version", ""))
	if latest.is_empty() or not bool(result.data.get("update_available", false)):
		_update_banner.visible = false
		return

	_update_message.text = "dotquay %s is available - this editor is running %s." % [
		latest, String(result.data.get("current_version", ""))]
	_update_banner.visible = true


func _report_self_update(result: DotquayResult) -> void:
	_update_button.disabled = false

	for message in result.data.get("messages", []):
		_log_line(String(message))

	if not result.ok:
		_log_error(result.error_message)
		# Left visible on purpose: the update is still available, and hiding the one control that
		# retries it would make a transient network failure look like a permanent one.
		return

	_update_banner.visible = false

	# The CLI that answers from here on is a different binary. Refreshing costs one lockfile read
	# and means the version every later envelope reports is the new one.
	refresh()


func _on_self_update_pressed() -> void:
	_update_button.disabled = true
	_update_message.text = "updating dotquay..."
	_run(DotquayWire.COMMAND_SELF_UPDATE, PackedStringArray(["self", "update"]))


func _on_self_update_dismissed() -> void:
	_update_banner.visible = false


## Only ever reached from the explain dialog, and only when it judged the change benign — so the log
## line says what was discarded rather than just naming the flag.
func _on_force_update_requested(addon_name: String) -> void:
	_log_warning("Force-updating %s, discarding the editor-generated changes listed in the dialog."
		% addon_name)
	_run(DotquayWire.COMMAND_UPDATE, PackedStringArray(["update", addon_name, "--force"]), true)


func _apply_graph(payload: Dictionary) -> void:
	_graph = DotquayGraph.from_payload(payload)
	_graph_view.render(_graph)
	_render_conflicts()
	_render_dotnet()
	_update_summary()

	# A graph result — even a failing one carrying a conflict — implies both a working CLI and an
	# existing manifest, which is everything the mutating buttons need.
	_project_ready = true
	_update_toolbar_state()


func _update_summary() -> void:
	if _graph == null:
		_summary.text = ""
		return

	if _graph.is_empty():
		_summary.text = "No dependencies yet — use Add."
		_summary.add_theme_color_override("font_color", DotquayPalette.MUTED)
		return

	var counts := _graph.counts()
	var parts := PackedStringArray()
	parts.append("%d addon(s)" % counts["addons"])

	if counts["dotnet"] > 0:
		parts.append("%d NuGet" % counts["dotnet"])
	if counts["patched"] > 0:
		parts.append("%d patched" % counts["patched"])
	if counts["linked"] > 0:
		parts.append("%d linked" % counts["linked"])
	if counts["modified"] > 0:
		parts.append("%d modified" % counts["modified"])
	if counts["missing"] > 0:
		parts.append("%d missing" % counts["missing"])
	if counts["orphans"] > 0:
		parts.append("%d orphaned" % counts["orphans"])
	if counts["conflicts"] > 0:
		parts.append("%d conflict(s)" % counts["conflicts"])

	# The lockfile view can't see conflicts at all, so saying "no conflicts" there would be a claim
	# the data doesn't support.
	var suffix := (
		"" if _graph.source == DotquayWire.GRAPH_SOURCE_RESOLVED
		else "  (from lockfile — press Re-resolve to check for conflicts)")

	_summary.text = "  " + " · ".join(parts) + suffix
	_summary.add_theme_color_override(
		"font_color",
		DotquayPalette.ERROR if counts["conflicts"] > 0 or counts["modified"] > 0 else DotquayPalette.MUTED)


func _render_conflicts() -> void:
	_clear(_conflict_list)

	if _graph == null or _graph.conflicts.is_empty():
		var empty: DotquayStatusLabel = STATUS_LABEL.instantiate()
		_conflict_list.add_child(empty)
		empty.setup(
			"No conflicts." if _graph != null and _graph.source == DotquayWire.GRAPH_SOURCE_RESOLVED
			else "Conflicts are only detected by a live re-resolve. Press Re-resolve to check.",
			DotquayPalette.MUTED, "", true)
		return

	for conflict in _graph.conflicts:
		var card: DotquayConflictCard = CONFLICT_CARD.instantiate()
		_conflict_list.add_child(card)
		card.setup(conflict)
		card.resolve_requested.connect(_open_conflict_dialog)
		card.focus_requested.connect(_graph_view.focus_addon)


func _render_dotnet() -> void:
	_clear(_dotnet_list)

	if _graph == null or _graph.dotnet.is_empty():
		var empty: DotquayStatusLabel = STATUS_LABEL.instantiate()
		_dotnet_list.add_child(empty)
		empty.setup("No NuGet packages.", DotquayPalette.MUTED)
		return

	for entry in _graph.dotnet:
		var row: DotquayDotnetRow = DOTNET_ROW.instantiate()
		_dotnet_list.add_child(row)
		row.setup(entry)
		# Not `_on_node_action.bind(ACTION_REMOVE)`: bind *appends*, so the handler would receive
		# (package_name, "remove") — action and name swapped — and the match would fall through
		# silently. The lambda puts the arguments where the handler expects them.
		row.remove_requested.connect(
			func(package_name: String) -> void:
				_on_node_action(DotquayDependencyNode.ACTION_REMOVE, package_name))


func _report_outdated(payload: Dictionary) -> void:
	var count := int(payload.get("outdated_count", 0))
	if count == 0:
		_log_line("Everything is up to date.")
		return

	_log_warning("%d dependency/dependencies have newer versions:" % count)

	for group in ["addons", "dotnet"]:
		for entry in _dictionaries_in(payload, group):
			if bool(entry.get("is_outdated", false)):
				_log_line("  %s  %s -> %s" % [
					entry.get("name", "?"),
					entry.get("current", "?"),
					DotquayWire.str_or_empty(entry.get("latest"))])


func _report_doctor(payload: Dictionary) -> void:
	if bool(payload.get("healthy", false)):
		_log_line("Doctor found no issues.")
		return

	for issue in _dictionaries_in(payload, "issues"):
		_log_warning("  " + String(issue.get("message", "")))


func _report_operation(payload: Dictionary) -> void:
	# `payload.get("messages", [])` only supplies its default when the key is absent; SPEC.md §17
	# emits an absent optional array as JSON null, not an omitted key, so an ordinary no-message
	# operation used to raise "Unable to iterate on object of type 'Nil'" on the worker thread.
	for message in DotquayWire.array_or_empty(payload.get("messages")):
		_log_line(String(message))

	for change in _dictionaries_in(payload, "changes"):
		var action := String(change.get("action", ""))
		if action == DotquayWire.ACTION_UNCHANGED:
			continue

		_log_line("  %s: %s %s" % [
			change.get("name", "?"),
			action,
			DotquayWire.str_or_empty(change.get("to"))])


# ---------------------------------------------------------------------------------------------
# Log
# ---------------------------------------------------------------------------------------------

func _log_line(text: String) -> void:
	_append(text)


func _log_warning(text: String) -> void:
	_append(DotquayPalette.bbcode(DotquayPalette.WARN, text))


func _log_error(text: String) -> void:
	_append(DotquayPalette.bbcode(DotquayPalette.ERROR, text))


func _append(bbcode: String) -> void:
	# The buffer bounds the log (an editor session can run hundreds of commands) and re-renders the
	# label through `changed`; see [DotquayLogBuffer] for why the label itself is not the store.
	_log_buffer.append(bbcode)


func _render_log() -> void:
	_log.clear()
	_log.append_text(_log_buffer.render(
		DotquayPalette.bbcode(DotquayPalette.MUTED, DotquayLogBuffer.TRIMMED_MARKER)))
	_log.scroll_to_line(max(0, _log.get_line_count() - 1))


# ---------------------------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------------------------

## The dictionary entries of the array at [param key], skipping anything else. Every `from_payload`
## guards its arrays this way; the ad-hoc reports here read raw payloads and need the same guard, or
## a single malformed entry is a script error in the middle of a log line.
static func _dictionaries_in(payload: Dictionary, key: String) -> Array:
	var entries: Variant = payload.get(key, [])
	if not (entries is Array):
		return []

	return (entries as Array).filter(func(entry: Variant) -> bool: return entry is Dictionary)


static func _is_web_url(url: String) -> bool:
	return url.begins_with("https://") or url.begins_with("http://")


func _clear(container: Node) -> void:
	DotquayNodes.clear_children(container)
