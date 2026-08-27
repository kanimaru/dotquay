@tool
class_name DotquayDependencyNode
extends GraphNode

## One addon drawn as a [GraphNode] inside the dependency view.
##
## Built entirely in code rather than from a .tscn: the node's contents depend on data that only
## exists at runtime (a conflict has candidates, a clean addon has a version, an orphan has neither),
## so a scene file would be a skeleton every instance immediately rebuilds anyway.

## Emitted when the user picks something from this node's action menu.
signal action_requested(action: String, addon_name: String)

const ACTION_UPDATE := "update"
const ACTION_REMOVE := "remove"
const ACTION_RESOLVE := "resolve"
const ACTION_OVERRIDE := "override"
const ACTION_CLEAR_OVERRIDE := "clear_override"
const ACTION_OPEN_REPO := "open_repo"
const ACTION_CAPTURE_PATCH := "capture_patch"

## Status colours. Deliberately not the editor theme's accent colours: this panel's whole job is to
## make "something is wrong here" visible at a glance across a graph, and a theme-derived tint is too
## subtle to carry that at node size.
const COLOR_OK := Color(0.36, 0.72, 0.44)
const COLOR_WARN := Color(0.92, 0.71, 0.24)
const COLOR_ERROR := Color(0.88, 0.32, 0.32)
const COLOR_MUTED := Color(0.58, 0.60, 0.65)
const COLOR_OVERRIDE := Color(0.47, 0.62, 0.94)

var addon_name: String = ""

var _menu_button: MenuButton
var _model: DotquayGraph.AddonNode
var _conflict: DotquayGraph.Conflict


func setup(model: DotquayGraph.AddonNode, conflict: DotquayGraph.Conflict, is_root: bool) -> void:
	_model = model
	_conflict = conflict
	addon_name = model.name

	title = model.name
	name = "DotquayNode_" + model.name
	resizable = false
	draggable = true
	selectable = true
	custom_minimum_size = Vector2(240, 0)

	# Left slot = "something requires me", right slot = "I require something". A root has nothing on
	# its left because the project itself isn't drawn as a node — an arrow from empty space would
	# imply a parent that doesn't exist.
	set_slot(0, not is_root, 0, COLOR_MUTED, not model.dependencies.is_empty(), 0, COLOR_MUTED)

	_add_status_row()
	_add_detail_rows()
	_add_menu()


func _add_status_row() -> void:
	var row := HBoxContainer.new()
	row.add_theme_constant_override("separation", 6)

	var version := Label.new()
	version.text = _model.display_version()
	version.add_theme_font_size_override("font_size", 13)
	version.tooltip_text = _model.resolved if not _model.resolved.is_empty() else "Not resolved to a commit."
	row.add_child(version)

	if _conflict != null:
		row.add_child(_badge("conflict", COLOR_ERROR, _conflict.headline()))
	elif _model.status == DotquayWire.STATUS_MODIFIED:
		row.add_child(_badge("modified", COLOR_ERROR,
			"Hand-edited without a captured patch. Install and update will refuse to overwrite it (SPEC.md P0-6)."))
	elif _model.status == DotquayWire.STATUS_MISSING:
		row.add_child(_badge("missing", COLOR_WARN, "Locked, but the folder isn't on disk. Run Install."))
	elif _model.is_patched:
		row.add_child(_badge("patched", COLOR_OK, "A captured patch is re-applied on every update (SPEC.md §10)."))

	if _model.is_overridden:
		row.add_child(_badge("override", COLOR_OVERRIDE, "Forced by an [overrides] entry in dotquay.toml."))

	if _model.is_orphan():
		row.add_child(_badge("orphan", COLOR_WARN, "Locked but no longer required by anything."))

	add_child(row)


func _add_detail_rows() -> void:
	if not _model.constraint.is_empty():
		add_child(_detail("%s %s" % [_model.source_kind, _model.constraint], "The constraint declared in dotquay.toml."))
	elif _model.origin == DotquayWire.ORIGIN_TRANSITIVE:
		add_child(_detail("required by another addon", "Not declared in this project's manifest."))

	# Provenance is only known after a --resolve walk; showing nothing beats showing an empty label.
	if _model.requirements.size() > 1:
		var requirers := PackedStringArray()
		for requirement in _model.requirements:
			requirers.append(DotquayWire.display_requirer(requirement.requirer))

		var tooltip := PackedStringArray()
		for requirement in _model.requirements:
			tooltip.append(requirement.describe())

		add_child(_detail("needed by %d: %s" % [requirers.size(), ", ".join(requirers)], "\n".join(tooltip)))


func _add_menu() -> void:
	_menu_button = MenuButton.new()
	_menu_button.text = "Actions"
	_menu_button.flat = false
	_menu_button.tooltip_text = "Things you can do to this dependency."

	var popup := _menu_button.get_popup()

	if _conflict != null:
		popup.add_item("Resolve conflict...", 0)
		popup.set_item_metadata(popup.get_item_count() - 1, ACTION_RESOLVE)

	popup.add_item("Update", 1)
	popup.set_item_metadata(popup.get_item_count() - 1, ACTION_UPDATE)
	# A transitive addon has no manifest entry to re-resolve on its own; updating it means updating
	# whatever requires it.
	popup.set_item_disabled(popup.get_item_count() - 1, _model.origin == DotquayWire.ORIGIN_TRANSITIVE)

	popup.add_item("Pin to a version...", 2)
	popup.set_item_metadata(popup.get_item_count() - 1, ACTION_OVERRIDE)

	if _model.is_overridden:
		popup.add_item("Clear override", 3)
		popup.set_item_metadata(popup.get_item_count() - 1, ACTION_CLEAR_OVERRIDE)

	if _model.status == DotquayWire.STATUS_MODIFIED:
		popup.add_item("Capture edits as a patch", 4)
		popup.set_item_metadata(popup.get_item_count() - 1, ACTION_CAPTURE_PATCH)

	popup.add_separator()

	popup.add_item("Open repository", 5)
	popup.set_item_metadata(popup.get_item_count() - 1, ACTION_OPEN_REPO)
	popup.set_item_disabled(popup.get_item_count() - 1, _model.git.is_empty())

	popup.add_item("Remove", 6)
	popup.set_item_metadata(popup.get_item_count() - 1, ACTION_REMOVE)
	popup.set_item_disabled(popup.get_item_count() - 1, not _model.is_removable())

	popup.id_pressed.connect(_on_menu_id_pressed)
	add_child(_menu_button)


func _on_menu_id_pressed(id: int) -> void:
	var popup := _menu_button.get_popup()
	var index := popup.get_item_index(id)
	if index < 0:
		return

	var action: Variant = popup.get_item_metadata(index)
	if typeof(action) == TYPE_STRING:
		action_requested.emit(String(action), addon_name)


func _badge(text: String, color: Color, tooltip: String) -> Control:
	var label := Label.new()
	label.text = text
	label.add_theme_color_override("font_color", color)
	label.add_theme_font_size_override("font_size", 11)
	label.tooltip_text = tooltip
	label.mouse_filter = Control.MOUSE_FILTER_STOP  # tooltips need the node to receive mouse events
	return label


func _detail(text: String, tooltip: String) -> Control:
	var label := Label.new()
	label.text = text
	label.add_theme_color_override("font_color", COLOR_MUTED)
	label.add_theme_font_size_override("font_size", 11)
	label.tooltip_text = tooltip
	label.mouse_filter = Control.MOUSE_FILTER_STOP
	label.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	label.custom_minimum_size = Vector2(220, 0)
	return label
