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

## The action ids it carries are [DotquayNodeActions]' `MENU_*` constants, shared with
## [DotquayPackageNode] (DESIGN-v0.19 §5).

const STATUS_LABEL := preload("res://addons/dotquay/ui/items/status_label.tscn")

## [GraphNode]'s native title bar has no `text_overrun_behavior` of its own to lean on (P1-23), so a
## long addon or folder name is truncated by hand instead of being left to push the fixed 240px node
## wide enough to overlap the next column.
const TITLE_MAX_LENGTH := 28

var addon_name: String = ""

@onready var _status_row: HBoxContainer = %StatusRow
@onready var _details: VBoxContainer = %Details
@onready var _menu_button: MenuButton = %ActionsButton

var _model: DotquayGraph.AddonNode
var _conflict: DotquayGraph.Conflict
var _is_root := true
var _show_packages := true


func _ready() -> void:
	_menu_button.get_popup().id_pressed.connect(_on_menu_id_pressed)

	# setup() may run before the node enters the tree, in which case the @onready references were not
	# available yet. Rendering here covers both orders without the caller having to know which it got.
	if _model != null:
		_render()


## [param show_packages] is false when the view isn't drawing packages (the Addons filter). An addon
## whose only children are packages then gets no right-hand slot with nothing to connect to.
func setup(
	model: DotquayGraph.AddonNode, conflict: DotquayGraph.Conflict, is_root: bool, show_packages: bool = true
) -> void:
	_model = model
	_show_packages = show_packages
	_conflict = conflict
	_is_root = is_root

	addon_name = model.name
	title = truncate_title(model.name)
	# The full name is still one hover away — truncation must never make it undiscoverable, only
	# shorter on screen.
	tooltip_text = model.name
	name = "DotquayNode_" + model.name

	if is_node_ready():
		_render()


func _render() -> void:
	# Left slot = "something requires me", right slot = "I require something". A root has nothing on
	# its left because the project itself isn't drawn as a node — an arrow from empty space would
	# imply a parent that doesn't exist.
	# The right slot is also on for an addon that only declares NuGet packages. Their edges start
	# here too (DESIGN-v0.19).
	set_slot(0, not _is_root, 0, DotquayPalette.MUTED, has_outgoing_slot(_model, _show_packages), 0, DotquayPalette.MUTED)

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
			"Hand-edited without a captured patch. Install and update will refuse to overwrite it.\n"
			+ "Pick 'Why is this modified?' from this node's menu to see what actually "
			+ "differs — an addon Godot only rewrote a uid in reads the same as one you edited.")
	elif _model.status == DotquayWire.STATUS_MISSING:
		_badge("missing", DotquayPalette.WARN, "Locked, but the folder isn't on disk. Run Install.")
	elif _model.is_linked():
		_badge("linked", DotquayPalette.OVERRIDE,
			"Linked to a folder on this machine, so its contents are a live working copy rather than the "
			+ "locked commit. See the badge legend (?) for how to put it back.")
	elif _model.is_patched:
		_badge("patched", DotquayPalette.OK, "A captured patch is re-applied on every update.")

	if _model.is_overridden:
		_badge("override", DotquayPalette.OVERRIDE, "Forced by an [overrides] entry in dotquay.toml.")

	if _model.is_orphan():
		_badge("orphan", DotquayPalette.WARN,
			"Locked but no longer required by anything. Running \"Update all\" removes it automatically "
			+ "— a full update re-walks the graph and prunes anything unreachable.")

	# The folder is only worth showing when it isn't the name — that difference is precisely when
	# someone goes looking for the addon and can't find it.
	if _model.is_renamed():
		_badge("→ %s" % _model.folder, DotquayPalette.OVERRIDE,
			"Installed into addons/%s instead of addons/%s (install_as). Addons that preload each other "
			% [_model.folder, _model.name] + "by absolute path need a specific folder name.")


func _render_details() -> void:
	DotquayNodes.clear_children(_details)

	if not _model.constraint.is_empty():
		_detail("%s %s" % [DotquayWire.display_source_kind(_model.source_kind), _model.constraint],
			"The constraint declared in dotquay.toml.")
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
	DotquayNodeActions.apply_to(_menu_button.get_popup(), DotquayNodeActions.for_addon(_model, _conflict))


## Whether the right (outgoing) slot is on: when the addon has addon children, or declares packages
## that are being drawn. Static so the rule is a test; a detached node never runs `_render`.
static func has_outgoing_slot(model: DotquayGraph.AddonNode, packages_shown: bool) -> bool:
	return not model.dependencies.is_empty() or (packages_shown and not model.packages.is_empty())


## Shortens [param addon_name] to [constant TITLE_MAX_LENGTH] with a trailing ellipsis (P1-23). A
## plain string function of plain data, so the cutoff length is a test rather than something only
## visible by dragging a long-named addon onto the canvas. The package node uses it too.
static func truncate_title(addon_name: String) -> String:
	if addon_name.length() <= TITLE_MAX_LENGTH:
		return addon_name
	return addon_name.substr(0, TITLE_MAX_LENGTH - 1) + "…"


func _on_menu_id_pressed(id: int) -> void:
	var action := DotquayNodeActions.action_for(_menu_button.get_popup(), id)
	if not action.is_empty():
		action_requested.emit(action, addon_name)


func _badge(text: String, color: Color, tooltip: String) -> DotquayStatusLabel:
	var label: DotquayStatusLabel = STATUS_LABEL.instantiate()
	_status_row.add_child(label)
	return label.setup(text, color, tooltip)


func _detail(text: String, tooltip: String) -> void:
	var label: DotquayStatusLabel = STATUS_LABEL.instantiate()
	_details.add_child(label)
	label.setup(text, DotquayPalette.MUTED, tooltip, true)
