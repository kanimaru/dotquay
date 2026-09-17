@tool
class_name DotquayDependencyGraphView
extends GraphEdit

## Draws a [DotquayGraph] as a left-to-right layered diagram.
##
## Connections here are decorative: they describe a dependency the manifests already declared, so the
## user must not be able to draw or delete one by dragging. Editing the graph happens through the
## node menus, which map to real CLI commands.

## Note: [GraphEdit] already defines `node_selected(node)`, so this view deliberately does not
## declare a same-named signal of its own. Nothing connects to it today (C-13) — every action a node
## offers goes through its own right-click menu, which emits [signal node_action_requested] instead —
## but a future details pane that wants to react to a plain click, without that click being an action
## on its own, would read [member DotquayDependencyNode.addon_name] off the node the built-in signal
## hands over rather than this view growing a second, redundant selection signal of its own.
signal node_action_requested(action: String, addon_name: String)

const NODE_SCENE := preload("res://addons/dotquay/ui/dependency_node.tscn")

## Layout spacing. Columns are wide enough that a 240px node plus its connection curve doesn't
## overlap the next column, which is what makes a diamond readable rather than a knot.
const COLUMN_WIDTH := 320.0
const ROW_HEIGHT := 170.0
const MARGIN := Vector2(40, 40)

var _nodes_by_name: Dictionary = {}

## Addon name -> the [member GraphElement.position_offset] the user last dragged it to. Survives
## across [method render] calls (P1-22) so a refresh — which runs on every return to the tab — never
## undoes a layout the user already fixed up by hand. Forgotten only by [method auto_arrange].
var _manual_positions: Dictionary = {}

## True only while [method render] itself is assigning [member GraphElement.position_offset]. A drag
## and a layout assignment both fire [signal GraphElement.position_offset_changed]; this is what tells
## [method _on_position_offset_changed] apart which one just happened, so laying a node back onto its
## own remembered spot is never mistaken for the user moving it again.
var _rendering := false

## The graph [method render] was last called with, kept only so [method auto_arrange] can re-render
## without whoever clicks the button having to hand the graph back in.
var _last_graph: DotquayGraph


func render(graph: DotquayGraph) -> void:
	_last_graph = graph
	(%EmptyHint as Label).visible = graph == null or graph.is_empty()

	clear_connections()

	# Only GraphNodes: GraphEdit owns internal children (the connection layer, the minimap) that must
	# stay. Removed as well as freed — see [method DotquayNodes.clear_children] for why.
	for child in get_children():
		if child is GraphNode:
			remove_child(child)
			child.queue_free()

	_nodes_by_name.clear()

	if graph == null:
		return

	var roots := {}
	for root in graph.roots:
		roots[root] = true

	var layers := graph.layers()
	for column in range(layers.size()):
		var names: PackedStringArray = layers[column]
		for row in range(names.size()):
			var model: DotquayGraph.AddonNode = graph.find(names[row])
			if model == null:
				continue

			var node: DotquayDependencyNode = NODE_SCENE.instantiate()
			var default_position := MARGIN + Vector2(column * COLUMN_WIDTH, row * ROW_HEIGHT)

			_rendering = true
			node.position_offset = _manual_positions.get(model.name, default_position)
			_rendering = false

			node.position_offset_changed.connect(_on_position_offset_changed.bind(model.name, node))
			node.action_requested.connect(_on_node_action)
			add_child(node)
			node.setup(model, graph.conflict_for(model.name), roots.has(model.name))
			_nodes_by_name[model.name] = node

	# Connections must be made after every node exists: GraphEdit silently drops a connection whose
	# endpoints it can't find yet.
	for edge in graph.edges:
		var from_name: String = edge["from"]
		var to_name: String = edge["to"]

		# The project itself is not drawn as a node — a box labelled "your project" with an arrow to
		# everything adds a hub that carries no information the column position doesn't already.
		if from_name == DotquayWire.PROJECT_NODE:
			continue

		if _nodes_by_name.has(from_name) and _nodes_by_name.has(to_name):
			connect_node(_nodes_by_name[from_name].name, 0, _nodes_by_name[to_name].name, 0)


## Centres the view on one addon — used when the user clicks a conflict in the sidebar and needs to
## find the node it refers to in a graph too large to fit on screen.
func focus_addon(addon_name: String) -> void:
	if not _nodes_by_name.has(addon_name):
		return

	var node: GraphNode = _nodes_by_name[addon_name]
	scroll_offset = (node.position_offset * zoom) - (size * 0.5) + (node.size * zoom * 0.5)

	for child in get_children():
		if child is GraphNode:
			child.selected = child == node


func _on_node_action(action: String, addon_name: String) -> void:
	node_action_requested.emit(action, addon_name)


## Fires for both a real drag and [method render]'s own layout assignment; [member _rendering] is
## what tells them apart. Only a drag is worth remembering — recording the layout's own placement
## right back into [member _manual_positions] would make every render "manual" from the second one on.
func _on_position_offset_changed(addon_name: String, node: GraphNode) -> void:
	if _rendering:
		return
	_manual_positions[addon_name] = node.position_offset


## Forgets every manually dragged position and re-renders the last graph from scratch, back onto its
## computed layered layout (P1-22). The toolbar's own escape hatch for [member show_arrange_button]
## being deliberately turned off: Godot's built-in generic arrange knows nothing about the
## left-to-right column/row layout [method render] computes, so turning it back on would just replace
## one layout the user fought with another.
func auto_arrange() -> void:
	_manual_positions.clear()
	if _last_graph != null:
		render(_last_graph)


## The button lives here rather than in the scene: [method GraphEdit.get_menu_hbox] is the built-in
## toolbar's own container, populated by the engine once this view enters a live [SceneTree], so it
## isn't reachable at [method PackedScene.instantiate] time the way a scene-authored child would be —
## unlike the rest of this file, this one genuinely needs [method _ready].
func _ready() -> void:
	var arrange_button := Button.new()
	arrange_button.text = "Auto-arrange"
	arrange_button.tooltip_text = "Forget any manually dragged positions and lay the graph out fresh."
	arrange_button.pressed.connect(auto_arrange)
	get_menu_hbox().add_child(arrange_button)
