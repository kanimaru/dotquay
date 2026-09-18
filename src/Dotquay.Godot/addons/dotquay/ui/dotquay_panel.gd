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

const CONFLICT_CARD := preload("res://addons/dotquay/ui/items/conflict_card.tscn")
const STATUS_LABEL := preload("res://addons/dotquay/ui/items/status_label.tscn")

@onready var _graph_view: DotquayDependencyGraphView = %GraphView
@onready var _summary: Label = %Summary
@onready var _log: RichTextLabel = %Log
@onready var _clear_log_button: Button = %ClearLogButton
@onready var _conflict_list: VBoxContainer = %ConflictList
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
@onready var _doctor_dialog: DotquayDoctorDialog = %DoctorDialog
@onready var _confirm_remove: ConfirmationDialog = %ConfirmRemove
@onready var _confirm_overwrite_cli_path: ConfirmationDialog = %ConfirmOverwriteCliPath
@onready var _confirm_update_all: ConfirmationDialog = %ConfirmUpdateAll

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
var _pending_requests: Dictionary[int, DotquayAddRequest] = {}

## CLI ticket to the addon name that run's `status --explain` was opened about. Keyed on the ticket
## for the same reason as [member _pending_requests]: `status` reports every addon, so the reply alone
## does not say which one the user asked about.
var _pending_explains: Dictionary[int, String] = {}

## Which in-flight runs must be followed by a graph refresh. A tracker rather than a flag, because a
## flag is consumed by whichever command finishes first — see [DotquayRefreshTracker].
var _refresh_tracker := DotquayRefreshTracker.new()

## The log's contents, in message units. The [RichTextLabel] is a view of this, rebuilt on change.
## No capacity passed (C-22): [constant DotquayLogBuffer.DEFAULT_CAPACITY] is exactly this panel's own
## 200-entry cap, and repeating that number here as a second constant is how the two used to drift.
var _log_buffer := DotquayLogBuffer.new()

## Ticket -> [method busy_label] for every run the CLI hasn't reported back on yet (P1-29). A
## dictionary rather than a single "current command" string because several runs can be in flight
## together — the startup self-check racing the first graph load is the common case — and the busy
## indicator should say all of them, not whichever happened to be tracked last.
var _running_commands: Dictionary[int, String] = {}

## Whether this editor session has already asked the release channel about the CLI (SPEC.md §17's
## `self-check` command). Once, not once per panel focus: [method refresh] runs every time the tab is
## shown, and reaching the network on each of those would be a worse habit than the one this feature
## is fixing.
var _self_checked := false

## The banner text a real "update available" self-check last set, kept so a failed self-*update* can
## restore it instead of leaving "Updating dotquay..." on screen forever (P1-7). Empty whenever the
## banner isn't showing an update offer at all.
var _update_available_message := ""

## The ticket of the most recently issued `graph` read. Several call sites can each trigger one (tab
## focus, the refresh tracker, Locate, a self-update, the explain fallback), and results can land out
## of order — this is what lets [method _on_cli_finished] ignore a reply that arrived after a newer
## read was already asked for (P1-30).
var _latest_graph_ticket := -1

## Whether the last graph result implied a working CLI and an existing manifest. Buttons that mutate
## the project are gated on this rather than just on busy state, so a fresh, unconfigured project
## can't be told to `install` or `update` before there is anything for those commands to act on.
var _project_ready := false


func _ready() -> void:
	_cli = DotquayCli.new()
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
	_preview_dialog.canceled.connect(_on_preview_cancelled)
	_explain_dialog.force_update_requested.connect(_on_force_update_requested)
	_conflict_dialog.override_chosen.connect(_on_override_chosen)
	_confirm_remove.confirmed.connect(_on_remove_confirmed)
	_confirm_overwrite_cli_path.confirmed.connect(_on_overwrite_cli_path_confirmed)
	_confirm_update_all.confirmed.connect(_on_update_all_confirmed)
	_get_dotquay_button.pressed.connect(_on_get_dotquay_pressed)
	_browse_button.pressed.connect(_on_browse_pressed)
	_explain_dialog.capture_patch_requested.connect(
		func(name: String) -> void: _on_node_action(DotquayNodeActions.MENU_CAPTURE_PATCH, name))

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
	_clear_log_button.pressed.connect(_log_buffer.clear)

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

	# Renders its own "nothing here yet" state when [member _graph] is null, so calling it before the
	# first graph ever arrives replaces a blank sidebar with that message instead of leaving it empty
	# until the tab's first `_apply_graph` (P1-6).
	_render_conflicts()


## Only ever running while [member _rescan] is waiting for the editor to finish a scan of its own.
func _process(_delta: float) -> void:
	set_process(_rescan.pump())


## Ids for the Tools overflow menu (P1-25) — Re-resolve and Doctor moved here off the main toolbar,
## since neither is a first-click action. Positional like the node menus ([DotquayNodeActions]), and for
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
	tools_popup.set_item_tooltip(tools_popup.get_item_index(TOOLS_ITEM_RESOLVE),
		"Contact each repository to work out the newest allowed versions and report conflicts. Needs "
		+ "internet; nothing is installed.")
	tools_popup.add_item("Run doctor", TOOLS_ITEM_DOCTOR)
	tools_popup.set_item_tooltip(tools_popup.get_item_index(TOOLS_ITEM_DOCTOR),
		"Check this project for problems such as missing folders or addons nothing needs any more.")
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
	_latest_graph_ticket = _run(DotquayWire.COMMAND_GRAPH, PackedStringArray())
	_check_for_cli_update()


## Asks the CLI whether a newer one exists, at most once per editor session.
##
## The plugin is asking on behalf of a binary it does not own: the thing being replaced is the
## `dotquay` executable this panel shells out to, not the plugin, so nothing here needs a restart —
## only a re-read of the version, which the next envelope carries anyway.
func _check_for_cli_update() -> void:
	if _self_checked:
		return

	_self_checked = true
	_run(DotquayWire.COMMAND_SELF_CHECK, PackedStringArray())


## Re-walks live git refs. The only mode that can discover a conflict (SPEC.md §8), and the only one
## that costs network time — so it is always an explicit action, never automatic.
func resolve() -> void:
	_log_line("Checking repositories online for the newest allowed versions... (this can take a moment)")
	_latest_graph_ticket = _run(DotquayWire.COMMAND_GRAPH, PackedStringArray([DotquayWire.FLAG_RESOLVE]))


# ---------------------------------------------------------------------------------------------
# Commands
# ---------------------------------------------------------------------------------------------

## [param extra_args] is everything after the CLI subcommand word(s) themselves — [method
## DotquayWire.cli_command_words] supplies those from [param command_name], so a call site no longer
## repeats "install" as both the label and the first element of a literal array (C-20).
##
## Returns the run's ticket so a caller can key state on it (see [member _pending_requests]).
func _run(command_name: String, extra_args: PackedStringArray, refresh_after: bool = false) -> int:
	var args := DotquayWire.cli_command_words(command_name)
	args.append_array(extra_args)
	return _run_raw(command_name, args, refresh_after)


## The exception to [method _run]'s "just the extra args" contract: the `add`/`add --preview` call
## sites build their complete argument list, `add` included, through [method
## DotquayAddRequest.to_args] instead — which has its own reason to own the whole command line (see
## that method's doc comment) — so prepending [method DotquayWire.cli_command_words] on top of it here
## would double the leading "add".
func _run_raw(command_name: String, args: PackedStringArray, refresh_after: bool = false) -> int:
	var ticket := _cli.run(command_name, args)

	_running_commands[ticket] = busy_label(command_name)
	_update_busy_label()
	_log_heading(command_name)

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
		var ticket := _run_raw(DotquayWire.COMMAND_ADD, request.to_args(), true)
		_pending_requests[ticket] = request
		return

	_log_line("Resolving what adding %s would install..." % request.describe())

	var args := request.to_args()
	args.append(DotquayWire.FLAG_PREVIEW)
	var ticket := _run_raw(DotquayWire.COMMAND_ADD_PREVIEW, args)
	_pending_requests[ticket] = request


## The preview dialog's own Cancel button, or closing it via the titlebar, otherwise leaves the log
## sitting on "Resolving what adding x would install..." forever (P1-15) — nothing else ever says the
## add didn't happen. Never fires alongside [signal DotquayInstallPreviewDialog.install_confirmed]:
## an [AcceptDialog] emits exactly one of `confirmed`/`canceled` per popup.
func _on_preview_cancelled() -> void:
	_log_line("Cancelled — nothing was added.")


## Runs the add the preview described, carrying the user's feed approvals as `--adopt-source`.
func _on_install_confirmed(request: DotquayAddRequest, adopt_source_urls: PackedStringArray) -> void:
	var args := request.to_args()

	for url in adopt_source_urls:
		args.append(DotquayWire.FLAG_ADOPT_SOURCE)
		args.append(url)

	_log_line("Adding %s..." % request.describe())

	for url in adopt_source_urls:
		_log_line("  adopting NuGet feed %s" % url)

	var ticket := _run_raw(DotquayWire.COMMAND_ADD, args, true)
	# Kept under the plain request, not the one carrying --adopt-source: a failure here re-opens the
	# add dialog (P1-9), which only ever collects what this request already holds — the adopted feeds
	# are the preview dialog's business, and re-approving them is what re-previewing is for.
	_pending_requests[ticket] = request


func _on_install_pressed() -> void:
	_log_line("Installing from the lockfile...")
	_run(DotquayWire.COMMAND_INSTALL, PackedStringArray(), true)


## Update all has no undo — it can move every addon and NuGet package to a new commit or version in
## one click — so it gets the same confirm-first treatment Remove already had (P1-26).
func _on_update_all_pressed() -> void:
	_confirm_update_all.popup_centered()


func _on_update_all_confirmed() -> void:
	_log_line("Updating everything...")
	_run(DotquayWire.COMMAND_UPDATE, PackedStringArray(), true)


func _on_outdated_pressed() -> void:
	_run(DotquayWire.COMMAND_OUTDATED, PackedStringArray())


func _on_doctor_pressed() -> void:
	_run(DotquayWire.COMMAND_DOCTOR, PackedStringArray())


func _on_node_action(action: String, addon_name: String) -> void:
	match action:
		DotquayNodeActions.MENU_UPDATE:
			_log_line("Updating %s..." % addon_name)
			_run(DotquayWire.COMMAND_UPDATE, PackedStringArray([addon_name]), true)

		DotquayNodeActions.MENU_REMOVE:
			_pending_removal = addon_name
			# Shared by addons and NuGet packages, so it names what `remove` does for both rather than
			# promising a NuGet package will disappear from addons/.
			_confirm_remove.dialog_text = (
				"Remove '%s' from dotquay.toml and the lockfile?\n\nWhat it installed is removed too, along with anything only it required."
				% addon_name)
			_confirm_remove.popup_centered()

		# Both a real conflict's "Resolve" and a healthy addon's "Always use a specific version..." open
		# the same picker (C-22) — deliberately, not a menu that forgot to grow a second dialog:
		# [method _open_conflict_dialog] already builds a synthetic manual-pin conflict when there is no
		# real one, and [DotquayGraph.Conflict.is_manual_pin] is what the dialog itself reads to tell the
		# two apart in its own wording (P1-18).
		DotquayNodeActions.MENU_RESOLVE, DotquayNodeActions.MENU_OVERRIDE:
			_open_conflict_dialog(addon_name)

		DotquayNodeActions.MENU_CLEAR_OVERRIDE:
			_log_line("Clearing the override on %s..." % addon_name)
			_run(DotquayWire.COMMAND_OVERRIDE, PackedStringArray([addon_name, DotquayWire.FLAG_CLEAR]), true)

		DotquayNodeActions.MENU_CAPTURE_PATCH:
			_log_line("Capturing edits to %s as a patch..." % addon_name)
			_run(DotquayWire.COMMAND_PATCH, PackedStringArray([addon_name]), true)

		DotquayNodeActions.MENU_EXPLAIN:
			# `--explain` compares against the locked commit, which can mean a fetch — so it is said
			# out loud rather than leaving the panel silently busy.
			_log_line("Comparing %s against its locked commit..." % addon_name)
			var ticket := _run(DotquayWire.COMMAND_STATUS, PackedStringArray([DotquayWire.FLAG_EXPLAIN]))
			_pending_explains[ticket] = addon_name

		DotquayNodeActions.MENU_OPEN_NUGET:
			var package := _graph.find_package(addon_name) if _graph != null else null
			if package != null and package.is_on_public_feed():
				OS.shell_open(package.nuget_url())

		DotquayNodeActions.MENU_OPEN_REPO:
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
	_run(DotquayWire.COMMAND_REMOVE, PackedStringArray([_pending_removal]), true)
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
			"'%s' has no conflict right now. Pinning it here forces it to the version you choose, "
			% addon_name + "overriding whatever other addons ask for.")

		var node: DotquayGraph.AddonNode = _graph.find(addon_name)
		if node != null:
			var requirements: Array = node.requirements
			conflict.requirements = requirements

	_conflict_dialog.show_conflict(conflict)


func _on_override_chosen(addon_name: String, kind: String, value: String, git: String, install_as: String) -> void:
	var args := PackedStringArray([addon_name, DotquayWire.flag_for_source_kind(kind), value])
	if not git.is_empty():
		args.append(DotquayWire.FLAG_GIT)
		args.append(git)

	if not install_as.is_empty():
		args.append(DotquayWire.FLAG_INSTALL_AS)
		args.append(install_as)

	if install_as.is_empty():
		_log_line("Pinning %s to %s %s..." % [addon_name, DotquayWire.display_source_kind(kind), value])
	else:
		_log_line("Pinning %s to addons/%s..." % [addon_name, install_as])

	_run(DotquayWire.COMMAND_OVERRIDE, args, true)


func _on_init_pressed() -> void:
	_log_line("Creating dotquay.toml...")
	_run(DotquayWire.COMMAND_INIT, PackedStringArray(), true)


## Feedback goes to the banner itself, not only the log (P1-4): the log is 120px tall and easy to
## miss, and this button exists specifically because the banner is already telling the user something
## is wrong — the answer belongs where the question was asked.
func _on_locate_pressed() -> void:
	var found := DotquaySettings.discover_on_path()
	if found.is_empty():
		_setup_message.text = "Could not find dotquay on PATH either. Use Get dotquay or Browse instead."
		_log_error("Could not find dotquay on PATH.")
		return

	var editor_settings := EditorInterface.get_editor_settings()
	var configured := ""
	if editor_settings.has_setting(DotquaySettings.CLI_PATH_SETTING):
		configured = String(editor_settings.get_setting(DotquaySettings.CLI_PATH_SETTING)).strip_edges()

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
	EditorInterface.get_editor_settings().set_setting(DotquaySettings.CLI_PATH_SETTING, found)
	_setup_message.text = "Found dotquay at %s. Refreshing..." % found
	_log_line("Found dotquay at %s." % found)
	refresh()


## Opens the release page in the system browser (P0-2). Nothing here can tell whether the user
## actually downloaded anything — there's no signal for that — so this only opens the page; the
## refresh that picks up a newly-installed binary is whatever the user presses next (Browse, Find on
## PATH, or Refresh).
func _on_get_dotquay_pressed() -> void:
	OS.shell_open(DotquaySettings.RELEASES_URL)


func _on_browse_pressed() -> void:
	_cli_path_dialog.popup_centered_ratio(0.5)


func _on_cli_path_selected(path: String) -> void:
	EditorInterface.get_editor_settings().set_setting(DotquaySettings.CLI_PATH_SETTING, path)
	_log_line("dotquay path set to %s." % path)
	refresh()


# ---------------------------------------------------------------------------------------------
# CLI results
# ---------------------------------------------------------------------------------------------

## The banner is a first-run affordance, not a second error log: it appears for every problem that
## leaves the CLI unusable, and offers exactly the buttons (if any) that fix what it's showing. Which
## buttons and which message is [DotquaySetupBanner]'s call, not this method's — pulled out as a pure
## function so those decisions are testable without a scene.
##
## Returns the decision so [method _on_cli_finished] can skip the duplicate red log line for a
## banner-handled kind (P1-5) — the banner already says it, in the same colour, right above.
func _update_setup_banner(result: DotquayResult) -> DotquaySetupBanner.Decision:
	var decision := DotquaySetupBanner.decide(result.error_kind)

	_setup_banner.visible = decision.visible
	_get_dotquay_button.visible = decision.show_get_dotquay
	_browse_button.visible = decision.show_browse
	_locate_button.visible = decision.show_locate
	_init_button.visible = decision.show_init

	if not decision.visible:
		return decision

	# None of these states is one any mutating command should be offered against.
	_project_ready = false
	_update_toolbar_state()

	# An empty decision message means the CLI's own error_message is the one worth showing (a wrong
	# path or a schema mismatch) — DotquaySetupBanner has no way to know that text, only the kind.
	_setup_message.text = decision.message if not decision.message.is_empty() else result.error_message
	return decision


func _on_busy_changed(is_busy: bool) -> void:
	_busy.visible = is_busy
	_is_busy = is_busy
	_update_toolbar_state()


## Names what is actually running (P1-29) instead of the bare "Working..." the label used to be stuck
## with regardless of which button was clicked. [member _running_commands] can hold more than one
## label at once — shown together, oldest first, rather than picking one arbitrarily and hiding that
## something else is also in flight.
func _update_busy_label() -> void:
	if _running_commands.is_empty():
		_busy.text = "Working..."
		return
	_busy.text = ", ".join(_running_commands.values())


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
	# Every path below returns through here exactly once per ticket, so this is the one place that's
	# guaranteed to run for every run this dialog started — the matching half of [method _run]'s own
	# bookkeeping (P1-29).
	_running_commands.erase(result.ticket)
	_update_busy_label()

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

	var banner_decision := _update_setup_banner(result)

	for warning in result.warnings:
		_log_warning(warning)

	# Read once and forgotten here, on both outcomes, rather than at each of the three call sites that
	# populate it: a plain `add`, an `add --preview`, and the `add` a confirmed preview runs must all
	# stop being "pending" the moment this result comes back, or a later run could pick up a stale one.
	var tracked_request: DotquayAddRequest = _pending_requests.get(result.ticket)
	_pending_requests.erase(result.ticket)

	if not result.ok:
		# A banner-handled kind (not configured, no manifest, or the CLI unusable in some way) already
		# said this, in the same colour, right above the graph — a second red line in the 140px log is
		# noise repeating what's already the biggest thing on screen, not new information (P1-5).
		if not banner_decision.visible:
			_log_error(result.error_message)
			# The CLI's own message names what git or the registry rejected, not what to do about it
			# (P1-33) — a one-line nudge is blank for every kind but an auth failure.
			var hint := DotquayResult.auth_hint(result.error_kind)
			if not hint.is_empty():
				_log_line(hint)
		_pending_explains.erase(result.ticket)

		# A failed add or preview closed the dialog for nothing (P1-9): the values the user typed, and
		# the error that says what to fix, would otherwise only be findable by scrolling back through
		# the log. Reopening with both intact turns "fix and resubmit" back into an edit.
		if tracked_request != null and (
			result.command == DotquayWire.COMMAND_ADD
			or result.command == DotquayWire.COMMAND_ADD_PREVIEW):
			_add_dialog.reopen_after_failure(tracked_request, result.error_message)

		# A conflict is still a full graph payload — the whole point is to show it rather than stop
		# at the error line. Still subject to the same staleness check as the success path (P1-30): a
		# failing `graph --resolve` that lands after a newer read was already asked for must not flash
		# its conflict over whatever that newer read is about to show.
		if (result.command == DotquayWire.COMMAND_GRAPH and not result.data.is_empty()
			and should_apply_graph_result(result, _latest_graph_ticket)):
			_apply_graph(result.data)

		_finish_refresh_cycle(result.ticket)
		return

	match result.command:
		DotquayWire.COMMAND_GRAPH:
			if should_apply_graph_result(result, _latest_graph_ticket):
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
		# A not-configured or launch-failed CLI never actually reached the release channel, so this
		# was not a real check — resetting the flag lets the next refresh (after the path is fixed)
		# try again instead of the update notice staying dark for the rest of the session (P1-7).
		if result.error_kind == DotquayResult.KIND_NOT_CONFIGURED or result.error_kind == DotquayResult.KIND_LAUNCH_FAILED:
			_self_checked = false
		return

	var check := DotquaySelfCheck.from_payload(result.data)
	if not check.has_available_update():
		_update_banner.visible = false
		return

	_update_available_message = check.describe_update()
	_update_message.text = _update_available_message
	_update_banner.visible = true


func _report_self_update(result: DotquayResult) -> void:
	_update_button.disabled = false

	# Reads through DotquayOperation (C-24) rather than its own parser: self-update reports "messages"
	# in the identical shape every other mutating command does, and never reports "changes".
	for message in DotquayOperation.from_payload(result.data).messages:
		_log_line(message)

	if not result.ok:
		_log_error(result.error_message)
		# Left visible on purpose: the update is still available, and hiding the one control that
		# retries it would make a transient network failure look like a permanent one. The message is
		# restored too, rather than left reading "Updating dotquay..." forever (P1-7).
		_update_message.text = _update_available_message
		return

	_update_banner.visible = false

	# The CLI that answers from here on is a different binary. Refreshing costs one lockfile read
	# and means the version every later envelope reports is the new one.
	refresh()


func _on_self_update_pressed() -> void:
	_update_button.disabled = true
	_update_message.text = "Updating dotquay..."
	_run(DotquayWire.COMMAND_SELF_UPDATE, PackedStringArray())


func _on_self_update_dismissed() -> void:
	_update_banner.visible = false


## Only ever reached from the explain dialog, and only when it judged the change benign — so the log
## line says what was discarded rather than just naming the flag.
func _on_force_update_requested(addon_name: String) -> void:
	_log_warning("Force-updating %s, discarding the editor-generated changes listed in the dialog."
		% addon_name)
	_run(DotquayWire.COMMAND_UPDATE, PackedStringArray([addon_name, DotquayWire.FLAG_FORCE]), true)


func _apply_graph(payload: Dictionary) -> void:
	_graph = DotquayGraph.from_payload(payload)
	_graph_view.render(_graph)
	_render_conflicts()
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
	parts.append(DotquayText.plural(counts["addons"], "%d addon", "%d addons"))

	if counts["dotnet"] > 0:
		parts.append(DotquayText.plural(counts["dotnet"], "%d NuGet package", "%d NuGet packages"))
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
		parts.append(DotquayText.plural(counts["conflicts"], "%d conflict", "%d conflicts"))

	# A lockfile-sourced graph never ran a live resolve, so it genuinely has nothing to say about
	# conflicts (P1-24) — but "press Re-resolve" is no longer accurate wording once the action moved
	# into the Tools menu (P1-25), so this no longer names a specific button.
	_summary.text = "  " + " · ".join(parts)
	_summary.add_theme_color_override(
		"font_color",
		DotquayPalette.ERROR if counts["conflicts"] > 0 or counts["modified"] > 0 else DotquayPalette.MUTED)


func _render_conflicts() -> void:
	DotquayNodes.clear_children(_conflict_list)

	if _graph == null or _graph.conflicts.is_empty():
		var empty: DotquayStatusLabel = STATUS_LABEL.instantiate()
		_conflict_list.add_child(empty)
		empty.setup(
			"No conflicts." if _graph != null and _graph.source == DotquayWire.GRAPH_SOURCE_RESOLVED
			else "No conflicts known. \"Check for conflicts (online)\" in the Tools menu checks for sure.",
			DotquayPalette.MUTED, "", true)
		return

	for conflict in _graph.conflicts:
		var card: DotquayConflictCard = CONFLICT_CARD.instantiate()
		_conflict_list.add_child(card)
		card.setup(conflict)
		card.resolve_requested.connect(_open_conflict_dialog)
		card.focus_requested.connect(_graph_view.focus_addon)


func _report_outdated(payload: Dictionary) -> void:
	var outdated := DotquayOutdated.from_payload(payload)
	if outdated.is_all_up_to_date():
		_log_line("Everything is up to date.")
		return

	_log_warning(DotquayText.plural(outdated.reported_count(),
		"Newer versions are available for %d addon/package:",
		"Newer versions are available for %d addons/packages:"))

	for entry in outdated.outdated_entries():
		_log_line("  %s  %s -> %s" % [
			DotquayText.or_unknown(entry.name),
			DotquayText.or_unknown(entry.current),
			DotquayText.or_unknown(entry.latest)])


## One line in the log, the findings in a dialog.
##
## Doctor is the one command whose result is routinely dozens of lines, and they are a checklist
## rather than a record of what happened — printed into the log they buried everything else in it
## and repeated one long explanation once per finding. The log keeps the sentence worth having in
## the history ("ran it, found this much"); [DotquayDoctorDialog] holds what to do about it.
##
## Opened even when the project is healthy: the user pressed a button and a dialog saying "nothing
## needs fixing" answers them, where a single log line among many is easy to miss entirely.
func _report_doctor(payload: Dictionary) -> void:
	var doctor := DotquayDoctor.from_payload(payload)

	if doctor.healthy:
		_log_line(doctor.summarize())
	else:
		_log_warning(doctor.summarize())

	_doctor_dialog.show_report(doctor)


func _report_operation(payload: Dictionary) -> void:
	var operation := DotquayOperation.from_payload(payload)

	for message in operation.messages:
		_log_line(message)

	for change in operation.meaningful_changes():
		# Only the name gets a placeholder: `action` is required by the wire, and an empty `to` is
		# correct for a removal rather than a field the CLI forgot to send.
		_log_line("  %s: %s %s" % [DotquayText.or_unknown(change.name), change.action, change.to])

	# A command that touched nothing (everything already `unchanged`, no messages of its own) used to
	# end silently right after its opening log line, reading like it had hung rather than finished
	# (P1-27).
	_log_line("Done — nothing needed changing." if operation.did_nothing() else "Done.")


# ---------------------------------------------------------------------------------------------
# Log
# ---------------------------------------------------------------------------------------------

## Every log entry passes through here on its way into [member _log_buffer], which the log's
## [RichTextLabel] renders as BBCode — so [param text] is escaped before it becomes part of that
## buffer, not after, whether or not this particular line also gets a colour tag (P1-28). An addon
## name, a NuGet range like `[1.0,2.0)`, or a `[addons.x]` TOML table the CLI quoted back in an error
## would otherwise be read as markup instead of the text it actually is.
func _log_line(text: String) -> void:
	_append(DotquayPalette.escape_bbcode(text))


func _log_warning(text: String) -> void:
	_append(DotquayPalette.bbcode(DotquayPalette.WARN, DotquayPalette.escape_bbcode(text)))


func _log_error(text: String) -> void:
	_append(DotquayPalette.bbcode(DotquayPalette.ERROR, DotquayPalette.escape_bbcode(text)))


## One line marking where a command's own output begins (P1-29), so two runs that overlap in the log
## — the startup self-check racing the first graph load is the common case — read as two blocks
## instead of one interleaved wall of text.
func _log_heading(command_name: String) -> void:
	_append(DotquayPalette.bbcode(DotquayPalette.MUTED, "── %s ──" % busy_label(command_name).trim_suffix("...")))


## A human-readable, present-tense label for [param command_name] — the same string [method _run]
## hands to [method DotquayCli.run] — used by both the busy indicator and the log heading above it
## (P1-29). A static function of plain data, matching [DotquayNodeActions]' reason helpers, so
## the mapping is a test rather than something only visible by clicking every button once.
static func busy_label(command_name: String) -> String:
	match command_name:
		DotquayWire.COMMAND_ADD, DotquayWire.COMMAND_ADD_PREVIEW:
			return "Adding..."
		DotquayWire.COMMAND_GRAPH:
			return "Checking dependencies..."
		DotquayWire.COMMAND_OUTDATED:
			return "Checking for updates..."
		DotquayWire.COMMAND_DOCTOR:
			return "Running doctor..."
		DotquayWire.COMMAND_STATUS:
			return "Checking status..."
		DotquayWire.COMMAND_SELF_CHECK:
			return "Checking dotquay version..."
		DotquayWire.COMMAND_SELF_UPDATE:
			return "Updating dotquay..."
		DotquayWire.COMMAND_INSTALL:
			return "Installing..."
		DotquayWire.COMMAND_UPDATE:
			return "Updating..."
		DotquayWire.COMMAND_OVERRIDE:
			return "Pinning..."
		DotquayWire.COMMAND_PATCH:
			return "Patching..."
		DotquayWire.COMMAND_REMOVE:
			return "Removing..."
		DotquayWire.COMMAND_INIT:
			return "Creating dotquay.toml..."
		_:
			return "Working..."


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

static func _is_web_url(url: String) -> bool:
	return url.begins_with("https://") or url.begins_with("http://")


## Whether [param result] is worth acting on: a non-graph result always is, and a graph result only if
## nothing newer has been asked for since. Several call sites can each trigger a `graph` read (tab
## focus, the refresh tracker, Locate, a self-update, the explain fallback) and their replies can land
## out of order — applying a stale one would flash old data over what a newer, still-pending read is
## about to correct (P1-30). Pulled out as a static function of plain data, mirroring
## [DotquayNodeActions]' disabled-reason helpers, so the ordering rule is a test rather than
## something only provable by racing two real CLI calls.
static func should_apply_graph_result(result: DotquayResult, latest_graph_ticket: int) -> bool:
	return result.command != DotquayWire.COMMAND_GRAPH or result.ticket == latest_graph_ticket
