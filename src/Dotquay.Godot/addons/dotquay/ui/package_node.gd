@tool
class_name DotquayPackageNode
extends GraphNode

## One NuGet package drawn as a [GraphNode] inside the dependency view (DESIGN-v0.19).
##
## A sibling of [DotquayDependencyNode], not a mode of it. The two kinds share a frame shape and
## nothing else: the badges, the details and the menu are all different. A single node class with a
## `kind` switch would end up as one long `if nuget:` per method. Layout is the scene; the contents
## depend on runtime data, so the script owns them, the same split the addon node uses.

## Emitted when the user picks something from this node's action menu. The same signature as
## [signal DotquayDependencyNode.action_requested], so the view and panel route both kinds through
## one handler.
signal action_requested(action: String, package_name: String)

const STATUS_LABEL := preload("res://addons/dotquay/ui/items/status_label.tscn")

## Node-name prefix. Distinct from the addon node's, so an addon and a package that share a name
## (addon `gut`, package `Gut`) can never be the same node in the view.
const NAME_PREFIX := "DotquayPackage_"

var package_name: String = ""

@onready var _badges: HBoxContainer = %Badges
@onready var _details: VBoxContainer = %Details
@onready var _menu_button: MenuButton = %ActionsButton

var _model: DotquayGraph.PackageNode
var _show_incoming := true


func _ready() -> void:
	_menu_button.get_popup().id_pressed.connect(_on_menu_id_pressed)
	self_modulate = DotquayPalette.PACKAGE_TINT

	if _model != null:
		_render()


## [param show_incoming] is false when the view isn't drawing addons (the NuGet filter), so no slot
## is left dangling with nothing to connect to.
func setup(model: DotquayGraph.PackageNode, show_incoming: bool = true) -> void:
	_model = model
	_show_incoming = show_incoming

	package_name = model.name
	title = DotquayDependencyNode.truncate_title(model.name)
	tooltip_text = model.name
	name = NAME_PREFIX + model.name

	if is_node_ready():
		_render()


func _render() -> void:
	# Left slot only: a package is always a sink, and its incoming edges come from the addons that
	# declared it. A root or orphan with no declaring addon gets no slot, for the same reason a root
	# addon gets none: an arrow from empty space would imply a parent that isn't there.
	set_slot(0, has_incoming_slot(_model, _show_incoming), 0, DotquayPalette.PACKAGE, false, 0, DotquayPalette.PACKAGE)

	_render_badges()
	_render_details()
	DotquayNodeActions.apply_to(_menu_button.get_popup(), DotquayNodeActions.for_package(_model))


func _render_badges() -> void:
	DotquayNodes.clear_children(_badges)

	_badge("NuGet", DotquayPalette.PACKAGE, "A NuGet package, restored by dotnet into your .csproj.")

	var version := _badge(_model.display_version(), Color.WHITE,
		"Locked at %s." % _model.resolved if not _model.resolved.is_empty() else "Not locked yet.")
	version.add_theme_font_size_override("font_size", 13)

	if _model.is_orphan():
		_badge("orphan", DotquayPalette.WARN,
			"Locked, but neither your dotquay.toml nor any addon declares it any more. Remove it, or "
			+ "run \"Update all\" to prune it.")


func _render_details() -> void:
	DotquayNodes.clear_children(_details)

	for line in detail_lines(_model):
		_detail(line[0], line[1])


## Whether the left (incoming) slot is on: only when an addon declared this package and addons are
## being drawn at all. Static so the rule is a test; a detached node never runs `_render`.
static func has_incoming_slot(model: DotquayGraph.PackageNode, addons_shown: bool) -> bool:
	return addons_shown and not model.addon_requirers().is_empty()


## The detail lines under the badges, as `[text, tooltip]` pairs. Static and data-only, like the menu
## reasons in [DotquayNodeActions], so what a package node says about itself is testable without a
## scene tree.
static func detail_lines(model: DotquayGraph.PackageNode) -> Array:
	var lines: Array = []
	var addons := DotquayNodeActions.requirer_names_summary(model.addon_requirers())

	if model.is_root():
		if not model.constraint.is_empty():
			lines.append(["range %s" % model.constraint, "The version range declared in your dotquay.toml."])
		if not model.addon_requirers().is_empty():
			lines.append(["also needed by %s" % addons,
				"These addons declare it too. Your dotquay.toml's entry wins (P1-5), so removing it hands the "
				+ "choice of version back to them."])
	elif model.is_transitive():
		# Named, not just "an addon": with the NuGet filter on there's no edge to follow, and "which
		# addon?" is the question this whole node kind exists to answer.
		lines.append(["required by %s" % addons,
			"Not in your dotquay.toml; declared by these addons' own manifests. Update or remove them instead."])

	if not model.is_on_public_feed():
		lines.append(["feed: %s" % model.source, "Resolved from this declared [nuget.sources] feed, not nuget.org."])

	return lines


func _on_menu_id_pressed(id: int) -> void:
	var action := DotquayNodeActions.action_for(_menu_button.get_popup(), id)
	if not action.is_empty():
		action_requested.emit(action, package_name)


func _badge(text: String, color: Color, tooltip: String) -> DotquayStatusLabel:
	var label: DotquayStatusLabel = STATUS_LABEL.instantiate()
	_badges.add_child(label)
	return label.setup(text, color, tooltip)


func _detail(text: String, tooltip: String) -> void:
	var label: DotquayStatusLabel = STATUS_LABEL.instantiate()
	_details.add_child(label)
	label.setup(text, DotquayPalette.MUTED, tooltip, true)
