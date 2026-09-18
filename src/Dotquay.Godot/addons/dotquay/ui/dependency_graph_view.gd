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
## [param dependency_name] is an addon name or a NuGet package ID, depending on which kind of node
## asked. The panel doesn't need to know which: `update` and `remove` take either.
signal node_action_requested(action: String, dependency_name: String)

const NODE_SCENE := preload("res://addons/dotquay/ui/dependency_node.tscn")
const PACKAGE_SCENE := preload("res://addons/dotquay/ui/package_node.tscn")

## Keys for [member _nodes_by_key] and [member _manual_positions]. Namespaced by kind because an
## addon and a NuGet package can share a name, and one entry per name would silently swap one
## node's connections or remembered position for the other's (DESIGN-v0.19 §6).
const ADDON_KEY := "addon:%s"
const PACKAGE_KEY := "package:%s"

## Layout spacing. Columns are wide enough that a 240px node plus its connection curve doesn't
## overlap the next column, which is what makes a diamond readable rather than a knot.
const COLUMN_WIDTH := 320.0
const ROW_HEIGHT := 170.0
const MARGIN := Vector2(40, 40)

## [constant ADDON_KEY] / [constant PACKAGE_KEY] -> the node drawn for it.
var _nodes_by_key: Dictionary = {}

## Which node kinds are shown. Set through [method set_filter], which re-renders.
var _filter: DotquayGraphFilter.Mode = DotquayGraphFilter.Mode.ALL

## Node key -> the [member GraphElement.position_offset] the user last dragged it to. Survives
## across [method render] calls (P1-22) so a refresh — which runs on every return to the tab — never
## undoes a layout the user already fixed up by hand. Forgotten only by [method auto_arrange].
var _manual_positions: Dictionary = {}

## True only while [method render] itself is assigning [member GraphElement.position_offset]. A drag
## and a layout assignment both fire [signal GraphElement.position_offset_changed]; this is what tells
## [method _on_position_offset_changed] apart which one just happened, so laying a node back onto its
## own remembered spot is never mistaken for the user moving it again.
var _rendering := false

## Built in [method _ready] with the rest of the toolbar; null in a view that never entered a tree
## (the headless tests), which [method set_filter] tolerates.
var _filter_button: OptionButton

## The graph [method render] was last called with, kept only so [method auto_arrange] can re-render
## without whoever clicks the button having to hand the graph back in.
var _last_graph: DotquayGraph


func render(graph: DotquayGraph) -> void:
	_last_graph = graph

	var hint := %EmptyHint as Label
	hint.text = DotquayGraphFilter.empty_hint(graph, _filter)
	hint.visible = not hint.text.is_empty()

	clear_connections()

	# Only GraphNodes: GraphEdit owns internal children (the connection layer, the minimap) that must
	# stay. Removed as well as freed — see [method DotquayNodes.clear_children] for why.
	for child in get_children():
		if child is GraphNode:
			remove_child(child)
			child.queue_free()

	_nodes_by_key.clear()

	if graph == null:
		return

	var package_column := 0
	if DotquayGraphFilter.shows_addons(_filter):
		_render_addons(graph)
		package_column = graph.package_column()

	if DotquayGraphFilter.shows_packages(_filter):
		_render_packages(graph, package_column)

	_connect_edges(graph)


func _render_addons(graph: DotquayGraph) -> void:
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
			_place(node, ADDON_KEY % model.name, column, row)
			node.action_requested.connect(_on_node_action)
			add_child(node)
			node.setup(model, graph.conflict_for(model.name), roots.has(model.name),
				DotquayGraphFilter.shows_packages(_filter))
			_nodes_by_key[ADDON_KEY % model.name] = node


## Packages go in one column of their own, [param column], sorted by name. They are always sinks,
## so a dedicated column keeps them from interleaving with addons of the same depth.
func _render_packages(graph: DotquayGraph, column: int) -> void:
	var names := graph.package_names()
	for row in range(names.size()):
		var model: DotquayGraph.PackageNode = graph.find_package(names[row])
		if model == null:
			continue

		var node: DotquayPackageNode = PACKAGE_SCENE.instantiate()
		_place(node, PACKAGE_KEY % model.name, column, row)
		node.action_requested.connect(_on_node_action)
		add_child(node)
		node.setup(model, DotquayGraphFilter.shows_addons(_filter))
		_nodes_by_key[PACKAGE_KEY % model.name] = node


## Positions [param node] at its remembered spot, or at ([param column], [param row]) of the
## computed layout, and starts remembering drags under [param key].
func _place(node: GraphNode, key: String, column: int, row: int) -> void:
	var default_position := MARGIN + Vector2(column * COLUMN_WIDTH, row * ROW_HEIGHT)

	_rendering = true
	node.position_offset = _manual_positions.get(key, default_position)
	_rendering = false

	node.position_offset_changed.connect(_on_position_offset_changed.bind(key, node))


## Connections must be made after every node exists: GraphEdit silently drops a connection whose
## endpoints it can't find yet. Endpoints hidden by the filter are simply not there, so their
## connections are skipped rather than drawn into nothing.
func _connect_edges(graph: DotquayGraph) -> void:
	for edge in graph.edges:
		var from_name: String = edge["from"]
		var to_name: String = edge["to"]

		# The project itself is not drawn as a node — a box labelled "your project" with an arrow to
		# everything adds a hub that carries no information the column position doesn't already.
		if from_name == DotquayWire.PROJECT_NODE:
			continue

		_connect(ADDON_KEY % from_name, ADDON_KEY % to_name)

	# Addon -> package, from each package's required_by (DESIGN-v0.19 §3). The addon edges above
	# never include these: the CLI keeps the two namespaces apart on the wire as well.
	for package in graph.dotnet:
		for requirer in package.addon_requirers():
			_connect(ADDON_KEY % requirer, PACKAGE_KEY % package.name)


func _connect(from_key: String, to_key: String) -> void:
	if _nodes_by_key.has(from_key) and _nodes_by_key.has(to_key):
		connect_node(_nodes_by_key[from_key].name, 0, _nodes_by_key[to_key].name, 0)


## Shows only the node kinds [param mode] allows and re-renders the last graph. Remembered drag
## positions survive, since they're keyed per node rather than per layout.
func set_filter(mode: DotquayGraphFilter.Mode) -> void:
	_filter = mode
	if _filter_button != null:
		_filter_button.select(_filter_button.get_item_index(mode))
	render(_last_graph)


func get_filter() -> DotquayGraphFilter.Mode:
	return _filter


## Centres the view on one addon — used when the user clicks a conflict in the sidebar and needs to
## find the node it refers to in a graph too large to fit on screen.
func focus_addon(addon_name: String) -> void:
	var key := ADDON_KEY % addon_name
	if not _nodes_by_key.has(key):
		return

	var node: GraphNode = _nodes_by_key[key]
	scroll_offset = (node.position_offset * zoom) - (size * 0.5) + (node.size * zoom * 0.5)

	for child in get_children():
		if child is GraphNode:
			child.selected = child == node


func _on_node_action(action: String, dependency_name: String) -> void:
	node_action_requested.emit(action, dependency_name)


## Fires for both a real drag and [method render]'s own layout assignment; [member _rendering] is
## what tells them apart. Only a drag is worth remembering — recording the layout's own placement
## right back into [member _manual_positions] would make every render "manual" from the second one on.
func _on_position_offset_changed(key: String, node: GraphNode) -> void:
	if _rendering:
		return
	_manual_positions[key] = node.position_offset


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
	# The filter sits in the same engine-owned toolbar as Auto-arrange, for the same reason.
	_filter_button = OptionButton.new()
	_filter_button.tooltip_text = "Which kinds of dependency to show."
	for mode in DotquayGraphFilter.MODES:
		_filter_button.add_item(DotquayGraphFilter.label(mode), mode)
		_filter_button.set_item_tooltip(_filter_button.get_item_index(mode), DotquayGraphFilter.tooltip(mode))
	_filter_button.select(_filter_button.get_item_index(_filter))
	_filter_button.item_selected.connect(
		func(index: int) -> void: set_filter(_filter_button.get_item_id(index) as DotquayGraphFilter.Mode))
	get_menu_hbox().add_child(_filter_button)

	var arrange_button := Button.new()
	arrange_button.text = "Auto-arrange"
	arrange_button.tooltip_text = "Forget any manually dragged positions and lay the graph out fresh."
	arrange_button.pressed.connect(auto_arrange)
	get_menu_hbox().add_child(arrange_button)
