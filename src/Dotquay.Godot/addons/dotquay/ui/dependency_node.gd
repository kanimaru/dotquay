@tool
class_name DotquayDependencyNode
extends GraphNode

## One addon drawn as a [GraphNode] inside the dependency view.
##
## The frame — title bar, status row, detail column, actions menu — is the scene. What goes *in* the
## status row is not, because it depends on data that only exists at runtime: a conflict has badges a
## clean addon doesn't, and an orphan has neither a version nor a folder worth naming. So the scene
## owns the layout and this script owns the contents, which is the split that lets the layout be
## edited in the editor without touching code.

## Emitted when the user picks something from this node's action menu.
signal action_requested(action: String, addon_name: String)

const ACTION_UPDATE := "update"
const ACTION_REMOVE := "remove"
const ACTION_RESOLVE := "resolve"
const ACTION_OVERRIDE := "override"
const ACTION_CLEAR_OVERRIDE := "clear_override"
const ACTION_OPEN_REPO := "open_repo"
const ACTION_CAPTURE_PATCH := "capture_patch"

const STATUS_LABEL := preload("res://addons/dotquay/ui/items/status_label.tscn")

var addon_name: String = ""

@onready var _status_row: HBoxContainer = %StatusRow
@onready var _details: VBoxContainer = %Details
@onready var _menu_button: MenuButton = %ActionsButton

var _model: DotquayGraph.AddonNode
var _conflict: DotquayGraph.Conflict
var _is_root := true


func _ready() -> void:
	_menu_button.get_popup().id_pressed.connect(_on_menu_id_pressed)

	# setup() may run before the node enters the tree, in which case the @onready references were not
	# available yet. Rendering here covers both orders without the caller having to know which it got.
	if _model != null:
		_render()


func setup(model: DotquayGraph.AddonNode, conflict: DotquayGraph.Conflict, is_root: bool) -> void:
	_model = model
	_conflict = conflict
	_is_root = is_root

	addon_name = model.name
	title = model.name
	name = "DotquayNode_" + model.name

	if is_node_ready():
		_render()


func _render() -> void:
	# Left slot = "something requires me", right slot = "I require something". A root has nothing on
	# its left because the project itself isn't drawn as a node — an arrow from empty space would
	# imply a parent that doesn't exist.
	set_slot(0, not _is_root, 0, DotquayPalette.MUTED, not _model.dependencies.is_empty(), 0, DotquayPalette.MUTED)

	_render_status_row()
	_render_details()
	_render_menu()


func _render_status_row() -> void:
	DotquayNodes.clear_children(_status_row)

	var version := _badge(_model.display_version(),
		Color.WHITE,
		_model.resolved if not _model.resolved.is_empty() else "Not resolved to a commit.")
	version.add_theme_font_size_override("font_size", 13)

	if _conflict != null:
		_badge("conflict", DotquayPalette.ERROR, _conflict.headline())
	elif _model.status == DotquayWire.STATUS_MODIFIED:
		_badge("modified", DotquayPalette.ERROR,
			"Hand-edited without a captured patch. Install and update will refuse to overwrite it (SPEC.md P0-6).")
	elif _model.status == DotquayWire.STATUS_MISSING:
		_badge("missing", DotquayPalette.WARN, "Locked, but the folder isn't on disk. Run Install.")
	elif _model.is_linked():
		_badge("linked", DotquayPalette.OVERRIDE,
			"Linked to a folder on this machine, so its contents are a live working copy rather than the "
			+ "locked commit. Run 'dotquay link --remove' to put it back.")
	elif _model.is_patched:
		_badge("patched", DotquayPalette.OK, "A captured patch is re-applied on every update (SPEC.md §10).")

	if _model.is_overridden:
		_badge("override", DotquayPalette.OVERRIDE, "Forced by an [overrides] entry in dotquay.toml.")

	if _model.is_orphan():
		_badge("orphan", DotquayPalette.WARN, "Locked but no longer required by anything.")

	# The folder is only worth showing when it isn't the name — that difference is precisely when
	# someone goes looking for the addon and can't find it.
	if _model.is_renamed():
		_badge("→ %s" % _model.folder, DotquayPalette.OVERRIDE,
			"Installed into addons/%s instead of addons/%s (install_as). Addons that preload each other "
			% [_model.folder, _model.name] + "by absolute path need a specific folder name.")


func _render_details() -> void:
	DotquayNodes.clear_children(_details)

	if not _model.constraint.is_empty():
		_detail("%s %s" % [_model.source_kind, _model.constraint], "The constraint declared in dotquay.toml.")
	elif _model.origin == DotquayWire.ORIGIN_TRANSITIVE:
		_detail("required by another addon", "Not declared in this project's manifest.")

	# Provenance is only known after a --resolve walk; showing nothing beats showing an empty label.
	if _model.requirements.size() > 1:
		var requirers := PackedStringArray()
		var tooltip := PackedStringArray()

		for requirement in _model.requirements:
			requirers.append(DotquayWire.display_requirer(requirement.requirer))
			tooltip.append(requirement.describe())

		_detail("needed by %d: %s" % [requirers.size(), ", ".join(requirers)], "\n".join(tooltip))


func _render_menu() -> void:
	var popup := _menu_button.get_popup()
	popup.clear()

	if _conflict != null:
		_add_menu_item(popup, "Resolve conflict...", ACTION_RESOLVE)

	# A transitive addon has no manifest entry to re-resolve on its own; updating it means updating
	# whatever requires it.
	_add_menu_item(popup, "Update", ACTION_UPDATE, _model.origin == DotquayWire.ORIGIN_TRANSITIVE)
	_add_menu_item(popup, "Pin to a version...", ACTION_OVERRIDE)

	if _model.is_overridden:
		_add_menu_item(popup, "Clear override", ACTION_CLEAR_OVERRIDE)

	if _model.status == DotquayWire.STATUS_MODIFIED:
		_add_menu_item(popup, "Capture edits as a patch", ACTION_CAPTURE_PATCH)

	popup.add_separator()

	_add_menu_item(popup, "Open repository", ACTION_OPEN_REPO, _model.git.is_empty())
	_add_menu_item(popup, "Remove", ACTION_REMOVE, not _model.is_removable())


## Ids are positional, so the action travels as metadata instead: reordering the menu must not
## silently change what a click does.
func _add_menu_item(popup: PopupMenu, text: String, action: String, disabled: bool = false) -> void:
	var id := popup.get_item_count()
	popup.add_item(text, id)

	var index := popup.get_item_index(id)
	popup.set_item_metadata(index, action)
	popup.set_item_disabled(index, disabled)


func _on_menu_id_pressed(id: int) -> void:
	var popup := _menu_button.get_popup()
	var index := popup.get_item_index(id)
	if index < 0:
		return

	var action: Variant = popup.get_item_metadata(index)
	if typeof(action) == TYPE_STRING:
		action_requested.emit(String(action), addon_name)


func _badge(text: String, color: Color, tooltip: String) -> DotquayStatusLabel:
	var label: DotquayStatusLabel = STATUS_LABEL.instantiate()
	_status_row.add_child(label)
	return label.setup(text, color, tooltip)


func _detail(text: String, tooltip: String) -> void:
	var label: DotquayStatusLabel = STATUS_LABEL.instantiate()
	_details.add_child(label)
	label.setup(text, DotquayPalette.MUTED, tooltip, true)
