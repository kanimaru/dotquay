@tool
class_name DotquayPanel
extends Control

## The main-screen panel: toolbar, dependency graph, sidebar, and log (SPEC.md §18).
##
## Every button here maps to exactly one CLI invocation. The panel holds no model of its own beyond
## the last graph it was handed — after any mutating command it re-runs `graph` rather than patching
## its state, so what's on screen is always something dotquay actually said, never something the UI
## inferred.

const CLI_SCRIPT := preload("res://addons/dotquay/core/dotquay_cli.gd")
const SETTINGS := preload("res://addons/dotquay/core/dotquay_settings.gd")
const RESULT := preload("res://addons/dotquay/core/dotquay_result.gd")
const GRAPH_VIEW_SCRIPT := preload("res://addons/dotquay/ui/dependency_graph_view.gd")
const NODE_SCRIPT := preload("res://addons/dotquay/ui/dependency_node.gd")
const CONFLICT_DIALOG_SCRIPT := preload("res://addons/dotquay/ui/conflict_dialog.gd")
const ADD_DIALOG_SCRIPT := preload("res://addons/dotquay/ui/add_dependency_dialog.gd")

const MAX_LOG_LINES := 200

var _cli: DotquayCli
var _graph: DotquayGraph

var _graph_view: DotquayDependencyGraphView
var _summary: Label
var _log: RichTextLabel
var _conflict_list: VBoxContainer
var _dotnet_list: VBoxContainer
var _sidebar_tabs: TabContainer
var _setup_banner: PanelContainer
var _setup_message: Label
var _locate_button: Button
var _init_button: Button
var _busy: Control
var _toolbar_buttons: Array[Button] = []

var _conflict_dialog: DotquayConflictDialog
var _add_dialog: DotquayAddDependencyDialog
var _confirm_remove: ConfirmationDialog
var _pending_removal := ""

## Set while a mutating command is in flight, so its completion triggers exactly one refresh instead
## of every command handler remembering to ask for one.
var _refresh_after_current := false


func _ready() -> void:
	set_anchors_and_offsets_preset(Control.PRESET_FULL_RECT)

	_cli = CLI_SCRIPT.new()
	_cli.name = "DotquayCli"
	_cli.finished.connect(_on_cli_finished)
	_cli.busy_changed.connect(_on_busy_changed)
	add_child(_cli)

	_build_ui()
	refresh()


## Reloads the graph from the lockfile — fast, offline, and safe to call on every panel focus.
func refresh() -> void:
	_run("graph", PackedStringArray(["graph"]))


## Re-walks live git refs. The only mode that can discover a conflict (SPEC.md §8), and the only one
## that costs network time — so it is always an explicit action, never automatic.
func resolve() -> void:
	_log_line("Re-resolving against live git refs — this can take a moment.")
	_run("graph", PackedStringArray(["graph", "--resolve"]))


# ---------------------------------------------------------------------------------------------
# UI construction
# ---------------------------------------------------------------------------------------------

func _build_ui() -> void:
	var root := VBoxContainer.new()
	root.set_anchors_and_offsets_preset(Control.PRESET_FULL_RECT)
	root.add_theme_constant_override("separation", 0)
	add_child(root)

	root.add_child(_build_toolbar())
	root.add_child(_build_setup_banner())

	var split := HSplitContainer.new()
	split.size_flags_vertical = Control.SIZE_EXPAND_FILL
	split.split_offset = -360
	root.add_child(split)

	var left := VSplitContainer.new()
	left.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	left.split_offset = -140
	split.add_child(left)

	_graph_view = GRAPH_VIEW_SCRIPT.new()
	_graph_view.size_flags_vertical = Control.SIZE_EXPAND_FILL
	_graph_view.node_action_requested.connect(_on_node_action)
	left.add_child(_graph_view)

	left.add_child(_build_log())
	split.add_child(_build_sidebar())

	_build_dialogs()


func _build_toolbar() -> Control:
	var bar := HBoxContainer.new()
	bar.add_theme_constant_override("separation", 4)

	_summary = Label.new()
	_summary.text = "Loading..."
	_summary.size_flags_horizontal = Control.SIZE_EXPAND_FILL

	_add_toolbar_button(bar, "Add...", "Add a git addon or a NuGet package.", _on_add_pressed)
	_add_toolbar_button(bar, "Install", "Make addons/ and .csproj match the lockfile exactly.", _on_install_pressed)
	_add_toolbar_button(bar, "Update all", "Re-resolve every dependency and reinstall.", _on_update_all_pressed)
	_add_toolbar_button(bar, "Check for updates", "Ask each upstream what's newer, without cloning.", _on_outdated_pressed)
	bar.add_child(VSeparator.new())
	_add_toolbar_button(bar, "Re-resolve", "Re-walk the live dependency graph and report conflicts.", resolve)
	_add_toolbar_button(bar, "Refresh", "Reload the graph from the lockfile.", refresh)
	_add_toolbar_button(bar, "Doctor", "Run dotquay's sanity checks.", _on_doctor_pressed)

	bar.add_child(_summary)

	_busy = _build_spinner()
	bar.add_child(_busy)

	return bar


func _add_toolbar_button(bar: HBoxContainer, text: String, tooltip: String, handler: Callable) -> void:
	var button := Button.new()
	button.text = text
	button.tooltip_text = tooltip
	button.pressed.connect(handler)
	bar.add_child(button)
	_toolbar_buttons.append(button)


func _build_spinner() -> Control:
	var label := Label.new()
	label.text = "working..."
	label.add_theme_color_override("font_color", NODE_SCRIPT.COLOR_WARN)
	label.visible = false
	return label


func _build_setup_banner() -> Control:
	_setup_banner = PanelContainer.new()
	_setup_banner.visible = false

	var row := HBoxContainer.new()
	row.add_theme_constant_override("separation", 8)
	_setup_banner.add_child(row)

	_setup_message = Label.new()
	_setup_message.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	_setup_message.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	_setup_message.add_theme_color_override("font_color", NODE_SCRIPT.COLOR_WARN)
	row.add_child(_setup_message)

	_locate_button = Button.new()
	_locate_button.text = "Find dotquay on PATH"
	_locate_button.tooltip_text = "Ask the OS where dotquay is and save it to Editor Settings."
	_locate_button.pressed.connect(_on_locate_pressed)
	row.add_child(_locate_button)

	_init_button = Button.new()
	_init_button.text = "Create dotquay.toml"
	_init_button.tooltip_text = "Scaffold a manifest in this Godot project, the same as running `dotquay init`."
	_init_button.pressed.connect(_on_init_pressed)
	row.add_child(_init_button)

	return _setup_banner


func _build_log() -> Control:
	_log = RichTextLabel.new()
	_log.custom_minimum_size = Vector2(0, 120)
	_log.scroll_following = true
	_log.selection_enabled = true
	_log.bbcode_enabled = true
	return _log


func _build_sidebar() -> Control:
	_sidebar_tabs = TabContainer.new()
	_sidebar_tabs.custom_minimum_size = Vector2(320, 0)

	var conflicts_scroll := ScrollContainer.new()
	conflicts_scroll.name = "Conflicts"
	_conflict_list = VBoxContainer.new()
	_conflict_list.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	conflicts_scroll.add_child(_conflict_list)
	_sidebar_tabs.add_child(conflicts_scroll)

	var dotnet_scroll := ScrollContainer.new()
	dotnet_scroll.name = "NuGet"
	_dotnet_list = VBoxContainer.new()
	_dotnet_list.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	dotnet_scroll.add_child(_dotnet_list)
	_sidebar_tabs.add_child(dotnet_scroll)

	return _sidebar_tabs


func _build_dialogs() -> void:
	_conflict_dialog = CONFLICT_DIALOG_SCRIPT.new()
	_conflict_dialog.override_chosen.connect(_on_override_chosen)
	add_child(_conflict_dialog)

	_add_dialog = ADD_DIALOG_SCRIPT.new()
	_add_dialog.add_requested.connect(_on_add_requested)
	add_child(_add_dialog)

	_confirm_remove = ConfirmationDialog.new()
	_confirm_remove.title = "Remove dependency"
	_confirm_remove.ok_button_text = "Remove"
	_confirm_remove.confirmed.connect(_on_remove_confirmed)
	add_child(_confirm_remove)


# ---------------------------------------------------------------------------------------------
# Commands
# ---------------------------------------------------------------------------------------------

func _run(command_name: String, args: PackedStringArray, refresh_after: bool = false) -> void:
	_refresh_after_current = _refresh_after_current or refresh_after
	_cli.run(command_name, args)


func _on_add_pressed() -> void:
	_add_dialog.open()


func _on_add_requested(source: String, path_override: String) -> void:
	var args := PackedStringArray(["add", source])
	if not path_override.is_empty():
		args.append("--path")
		args.append(path_override)

	_log_line("Adding %s..." % source)
	_run("add", args, true)


func _on_install_pressed() -> void:
	_log_line("Installing from the lockfile...")
	_run("install", PackedStringArray(["install"]), true)


func _on_update_all_pressed() -> void:
	_log_line("Updating everything...")
	_run("update", PackedStringArray(["update"]), true)


func _on_outdated_pressed() -> void:
	_run("outdated", PackedStringArray(["outdated"]))


func _on_doctor_pressed() -> void:
	_run("doctor", PackedStringArray(["doctor"]))


func _on_node_action(action: String, addon_name: String) -> void:
	match action:
		NODE_SCRIPT.ACTION_UPDATE:
			_log_line("Updating %s..." % addon_name)
			_run("update", PackedStringArray(["update", addon_name]), true)

		NODE_SCRIPT.ACTION_REMOVE:
			_pending_removal = addon_name
			_confirm_remove.dialog_text = (
				"Remove '%s' from dotquay.toml, the lockfile, and addons/?\n\nAnything only it required is removed too."
				% addon_name)
			_confirm_remove.popup_centered()

		NODE_SCRIPT.ACTION_RESOLVE, NODE_SCRIPT.ACTION_OVERRIDE:
			_open_conflict_dialog(addon_name)

		NODE_SCRIPT.ACTION_CLEAR_OVERRIDE:
			_log_line("Clearing the override on %s..." % addon_name)
			_run("override", PackedStringArray(["override", addon_name, "--clear"]), true)

		NODE_SCRIPT.ACTION_CAPTURE_PATCH:
			_log_line("Capturing edits to %s as a patch..." % addon_name)
			_run("patch", PackedStringArray(["patch", addon_name]), true)

		NODE_SCRIPT.ACTION_OPEN_REPO:
			var node := _graph.find(addon_name) if _graph != null else null
			if node != null and not node.git.is_empty():
				OS.shell_open(node.git)


func _on_remove_confirmed() -> void:
	if _pending_removal.is_empty():
		return

	_log_line("Removing %s..." % _pending_removal)
	_run("remove", PackedStringArray(["remove", _pending_removal]), true)
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
		conflict.kind = ""
		conflict.message = (
			"No conflict was reported for '%s'. Pinning it here writes an [overrides] entry that wins over every other constraint (SPEC.md §8)."
			% addon_name)

		var node: DotquayGraph.AddonNode = _graph.find(addon_name)
		if node != null:
			var requirements: Array = node.requirements
			conflict.requirements = requirements

	_conflict_dialog.show_conflict(conflict)


func _on_override_chosen(addon_name: String, kind: String, value: String, git: String) -> void:
	var args := PackedStringArray(["override", addon_name, "--" + kind, value])
	if not git.is_empty():
		args.append("--git")
		args.append(git)

	_log_line("Pinning %s to %s %s..." % [addon_name, kind, value])
	_run("override", args, true)


func _on_init_pressed() -> void:
	_log_line("Creating dotquay.toml...")
	_run("init", PackedStringArray(["init"]), true)


func _on_locate_pressed() -> void:
	var found := SETTINGS.discover_on_path()
	if found.is_empty():
		_log_error("Could not find dotquay on PATH. Set the path by hand in Editor Settings -> Dotquay.")
		return

	EditorInterface.get_editor_settings().set_setting(SETTINGS.CLI_PATH_SETTING, found)
	_log_line("Found dotquay at %s." % found)
	refresh()


# ---------------------------------------------------------------------------------------------
# CLI results
# ---------------------------------------------------------------------------------------------

## The banner is a first-run affordance, not a second error log: it appears only for the two problems
## that have a one-click fix, and offers exactly the button that fixes the one it's showing.
func _update_setup_banner(result: DotquayResult) -> void:
	var not_configured := result.error_kind == RESULT.KIND_NOT_CONFIGURED
	var no_manifest := result.error_kind == RESULT.KIND_NO_MANIFEST

	_setup_banner.visible = not_configured or no_manifest
	_locate_button.visible = not_configured
	_init_button.visible = no_manifest

	if not _setup_banner.visible:
		return

	_setup_message.text = (
		result.error_message if not_configured
		else "This Godot project has no dotquay.toml yet, so there is nothing to show.")


func _on_busy_changed(is_busy: bool) -> void:
	_busy.visible = is_busy
	for button in _toolbar_buttons:
		button.disabled = is_busy


func _on_cli_finished(result: DotquayResult) -> void:
	_update_setup_banner(result)

	for warning in result.warnings:
		_log_warning(warning)

	if not result.ok:
		_log_error(result.error_message)

		# A conflict is still a full graph payload — the whole point is to show it rather than stop
		# at the error line.
		if result.command == "graph" and not result.data.is_empty():
			_apply_graph(result.data)

		_finish_refresh_cycle()
		return

	match result.command:
		"graph":
			_apply_graph(result.data)
		"outdated":
			_report_outdated(result.data)
		"doctor":
			_report_doctor(result.data)
		_:
			_report_operation(result.data)

	_finish_refresh_cycle()


## A mutating command changed the project, so the graph on screen is stale. Re-reading it (rather
## than editing the UI in place) is what keeps the panel from ever showing a state dotquay itself
## would disagree with.
func _finish_refresh_cycle() -> void:
	if not _refresh_after_current:
		return

	_refresh_after_current = false
	refresh()


func _apply_graph(payload: Dictionary) -> void:
	_graph = DotquayGraph.from_payload(payload)
	_graph_view.render(_graph)
	_render_conflicts()
	_render_dotnet()
	_update_summary()


func _update_summary() -> void:
	if _graph == null:
		_summary.text = ""
		return

	if _graph.is_empty():
		_summary.text = "No dependencies yet — use Add."
		_summary.add_theme_color_override("font_color", NODE_SCRIPT.COLOR_MUTED)
		return

	var counts := _graph.counts()
	var parts := PackedStringArray()
	parts.append("%d addon(s)" % counts["addons"])

	if counts["dotnet"] > 0:
		parts.append("%d NuGet" % counts["dotnet"])
	if counts["patched"] > 0:
		parts.append("%d patched" % counts["patched"])
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
		NODE_SCRIPT.COLOR_ERROR if counts["conflicts"] > 0 or counts["modified"] > 0 else NODE_SCRIPT.COLOR_MUTED)


func _render_conflicts() -> void:
	for child in _conflict_list.get_children():
		child.queue_free()

	if _graph == null or _graph.conflicts.is_empty():
		var empty := Label.new()
		empty.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
		empty.custom_minimum_size = Vector2(300, 0)
		empty.text = (
			"No conflicts." if _graph != null and _graph.source == DotquayWire.GRAPH_SOURCE_RESOLVED
			else "Conflicts are only detected by a live re-resolve. Press Re-resolve to check.")
		empty.add_theme_color_override("font_color", NODE_SCRIPT.COLOR_MUTED)
		_conflict_list.add_child(empty)
		return

	for conflict in _graph.conflicts:
		_conflict_list.add_child(_conflict_card(conflict))


func _conflict_card(conflict: DotquayGraph.Conflict) -> Control:
	var card := VBoxContainer.new()
	card.add_theme_constant_override("separation", 4)

	var headline := Label.new()
	headline.text = conflict.headline()
	headline.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	headline.custom_minimum_size = Vector2(300, 0)
	headline.add_theme_color_override("font_color", NODE_SCRIPT.COLOR_ERROR)
	card.add_child(headline)

	for requirement in conflict.requirements:
		var line := Label.new()
		line.text = "  • " + requirement.describe()
		line.add_theme_font_size_override("font_size", 11)
		line.add_theme_color_override("font_color", NODE_SCRIPT.COLOR_MUTED)
		line.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
		line.custom_minimum_size = Vector2(300, 0)
		card.add_child(line)

	var buttons := HBoxContainer.new()

	var resolve_button := Button.new()
	resolve_button.text = "Resolve..."
	resolve_button.pressed.connect(_open_conflict_dialog.bind(conflict.name))
	buttons.add_child(resolve_button)

	var focus_button := Button.new()
	focus_button.text = "Show in graph"
	focus_button.pressed.connect(_graph_view.focus_addon.bind(conflict.name))
	buttons.add_child(focus_button)

	card.add_child(buttons)
	card.add_child(HSeparator.new())
	return card


func _render_dotnet() -> void:
	for child in _dotnet_list.get_children():
		child.queue_free()

	if _graph == null or _graph.dotnet.is_empty():
		var empty := Label.new()
		empty.text = "No NuGet packages."
		empty.add_theme_color_override("font_color", NODE_SCRIPT.COLOR_MUTED)
		_dotnet_list.add_child(empty)
		return

	for entry in _graph.dotnet:
		var row := HBoxContainer.new()

		var label := Label.new()
		label.text = "%s  %s" % [entry.name, entry.display_version()]
		label.size_flags_horizontal = Control.SIZE_EXPAND_FILL
		if not entry.declared_by_project:
			label.add_theme_color_override("font_color", NODE_SCRIPT.COLOR_MUTED)
			label.tooltip_text = "Required by an addon, not declared in this project's manifest (SPEC.md P1-5)."
			label.mouse_filter = Control.MOUSE_FILTER_STOP
		row.add_child(label)

		if entry.declared_by_project:
			var remove := Button.new()
			remove.text = "Remove"
			remove.pressed.connect(_on_node_action.bind(NODE_SCRIPT.ACTION_REMOVE, entry.name))
			row.add_child(remove)

		_dotnet_list.add_child(row)


func _report_outdated(payload: Dictionary) -> void:
	var count := int(payload.get("outdated_count", 0))
	if count == 0:
		_log_line("Everything is up to date.")
		return

	_log_warning("%d dependency/dependencies have newer versions:" % count)

	for group in ["addons", "dotnet"]:
		for entry in payload.get(group, []):
			if bool(entry.get("is_outdated", false)):
				_log_line("  %s  %s -> %s" % [
					entry.get("name", "?"),
					entry.get("current", "?"),
					DotquayWire.str_or_empty(entry.get("latest"))])


func _report_doctor(payload: Dictionary) -> void:
	if bool(payload.get("healthy", false)):
		_log_line("Doctor found no issues.")
		return

	for issue in payload.get("issues", []):
		_log_warning("  " + String(issue.get("message", "")))


func _report_operation(payload: Dictionary) -> void:
	for message in payload.get("messages", []):
		_log_line(String(message))

	for change in payload.get("changes", []):
		var action := String(change.get("action", ""))
		if action == "unchanged":
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
	_append("[color=#eab53d]%s[/color]" % text)


func _log_error(text: String) -> void:
	_append("[color=#e05252]%s[/color]" % text)


func _append(bbcode: String) -> void:
	_log.append_text(bbcode + "\n")

	# An editor session can run hundreds of commands; an unbounded log is a slow memory leak in a
	# panel nobody ever closes.
	if _log.get_line_count() > MAX_LOG_LINES:
		_log.text = ""
		_log.append_text("[color=#95999f](earlier output trimmed)[/color]\n")
