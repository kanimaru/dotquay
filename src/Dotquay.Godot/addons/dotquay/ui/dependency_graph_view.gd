@tool
class_name DotquayDependencyGraphView
extends GraphEdit

## Draws a [DotquayGraph] as a left-to-right layered diagram.
##
## Connections here are decorative: they describe a dependency the manifests already declared, so the
## user must not be able to draw or delete one by dragging. Editing the graph happens through the
## node menus, which map to real CLI commands.

## Note: [GraphEdit] already defines `node_selected(node)`, so this view deliberately does not
## declare a same-named signal of its own — the panel listens to the built-in one and reads
## [member DotquayDependencyNode.addon_name] off the node it hands over.
signal node_action_requested(action: String, addon_name: String)

const NODE_SCENE := preload("res://addons/dotquay/ui/dependency_node.tscn")

## Layout spacing. Columns are wide enough that a 240px node plus its connection curve doesn't
## overlap the next column, which is what makes a diamond readable rather than a knot.
const COLUMN_WIDTH := 320.0
const ROW_HEIGHT := 170.0
const MARGIN := Vector2(40, 40)

var _nodes_by_name: Dictionary = {}


func render(graph: DotquayGraph) -> void:
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
			node.position_offset = MARGIN + Vector2(column * COLUMN_WIDTH, row * ROW_HEIGHT)
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
