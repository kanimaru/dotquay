extends RefCounted

## Covers [DotquayDependencyGraphView]'s manual-position bookkeeping (P1-22).
##
## Every refresh — which runs on every return to the graph tab — used to discard wherever the user
## had just dragged a node to, recomputing every position from scratch. These tests drive the real
## scene (detached, never added to a [SceneTree] — see [method DotquayExplainDialog._bind]'s doc
## comment for why that's enough for a script that never relies on `@onready`) to prove a dragged
## position survives the next [method render], and that only [method
## DotquayDependencyGraphView.auto_arrange] forgets it again.

const GRAPH := preload("res://addons/dotquay/core/dotquay_graph.gd")
const VIEW_SCENE := preload("res://addons/dotquay/ui/dependency_graph_view.tscn")

var runner: Object


func _single_addon_graph() -> Dictionary:
	return {
		"source": "resolved",
		"project_directory": "/tmp/project",
		"roots": ["a"],
		"addons": [
			{"name": "a", "git": "https://example.com/a", "version": "v1.0.0", "resolved": "aaaaaaa1111",
			 "short_sha": "aaaaaaa", "path": "addons/a", "origin": "root", "source_kind": "version",
			 "constraint": "~1.0", "is_overridden": false, "is_patched": false, "status": "clean",
			 "dependencies": [], "requirements": []},
		],
		"edges": [{"from": "<project>", "to": "a"}],
		"dotnet": [],
		"conflicts": [],
		"overrides": [],
		"has_conflicts": false,
	}


func test_graph_view_preserves_manual_positions_across_render() -> void:
	var view: DotquayDependencyGraphView = VIEW_SCENE.instantiate()
	var graph: DotquayGraph = GRAPH.from_payload(_single_addon_graph())

	view.render(graph)
	var node: GraphNode = view.get_node("DotquayNode_a")
	node.position_offset = Vector2(999, 777)

	# A second render is exactly what happens every time the tab is shown again — it must not put the
	# node back where the layout alone would have placed it.
	view.render(graph)
	var same_node: GraphNode = view.get_node("DotquayNode_a")

	runner.equal(same_node.position_offset, Vector2(999, 777),
		"a manually dragged position must survive the next render")

	view.free()


func test_graph_view_empty_hint_only_shows_for_an_empty_or_missing_graph() -> void:
	var view: DotquayDependencyGraphView = VIEW_SCENE.instantiate()
	var hint: Label = view.get_node("%EmptyHint")

	view.render(null)
	runner.check(hint.visible, "a null graph has nothing to draw, so the hint should show")

	view.render(GRAPH.from_payload(_single_addon_graph()))
	runner.check(not hint.visible, "a graph with an addon in it should hide the empty-canvas hint")

	view.free()


func test_graph_view_auto_arrange_forgets_the_dragged_position() -> void:
	var view: DotquayDependencyGraphView = VIEW_SCENE.instantiate()
	var graph: DotquayGraph = GRAPH.from_payload(_single_addon_graph())

	view.render(graph)
	var computed_position: Vector2 = (view.get_node("DotquayNode_a") as GraphNode).position_offset
	(view.get_node("DotquayNode_a") as GraphNode).position_offset = Vector2(999, 777)

	view.auto_arrange()
	var same_node: GraphNode = view.get_node("DotquayNode_a")

	runner.equal(same_node.position_offset, computed_position,
		"Auto-arrange should drop the dragged position and go back to the computed layout")

	view.free()


## An addon "gut" that declares a NuGet package also called "gut" (different case is legal, the same
## case happens too), plus an unrelated root package.
func _addon_and_package_graph() -> Dictionary:
	var payload := _single_addon_graph()
	payload["dotnet"] = [
		{"name": "a", "resolved": "1.0.0", "origin": "transitive", "required_by": ["a"], "source": null},
		{"name": "Serilog", "resolved": "3.1.1", "origin": "root", "required_by": ["<project>"], "source": null},
	]
	return payload


func _package_node(view: DotquayDependencyGraphView, package_name: String) -> GraphNode:
	return view.get_node_or_null(NodePath(DotquayPackageNode.NAME_PREFIX + package_name))


func test_graph_view_draws_an_addon_and_a_package_with_the_same_name_as_two_nodes() -> void:
	var view: DotquayDependencyGraphView = VIEW_SCENE.instantiate()
	view.render(GRAPH.from_payload(_addon_and_package_graph()))

	runner.check(view.get_node_or_null("DotquayNode_a") != null, "the addon node exists")
	runner.check(_package_node(view, "a") != null, "and so does the package node of the same name")

	view.free()


func test_graph_view_connects_an_addon_to_the_packages_it_declares() -> void:
	var view: DotquayDependencyGraphView = VIEW_SCENE.instantiate()
	view.render(GRAPH.from_payload(_addon_and_package_graph()))

	var package_node := _package_node(view, "a")
	runner.check(view.is_node_connected(&"DotquayNode_a", 0, package_node.name, 0),
		"an edge runs from the addon to the package it declared")

	var serilog := _package_node(view, "Serilog")
	runner.equal(view.get_connection_list().size(), 1,
		"the project-declared package gets no edge: the project isn't drawn as a node")
	runner.check(serilog != null, "but it is still drawn")

	view.free()


func test_graph_view_puts_packages_in_their_own_column() -> void:
	var view: DotquayDependencyGraphView = VIEW_SCENE.instantiate()
	view.render(GRAPH.from_payload(_addon_and_package_graph()))

	var addon: GraphNode = view.get_node("DotquayNode_a")
	runner.check(_package_node(view, "a").position_offset.x > addon.position_offset.x,
		"packages sit to the right of every addon")
	runner.equal(_package_node(view, "a").position_offset.x, _package_node(view, "Serilog").position_offset.x,
		"every package shares one column")

	view.free()


func test_graph_view_filter_hides_and_restores_packages() -> void:
	var view: DotquayDependencyGraphView = VIEW_SCENE.instantiate()
	view.render(GRAPH.from_payload(_addon_and_package_graph()))

	view.set_filter(DotquayGraphFilter.Mode.ADDONS)
	runner.check(_package_node(view, "Serilog") == null, "Addons hides packages")
	runner.check(view.get_node_or_null("DotquayNode_a") != null, "and keeps addons")

	view.set_filter(DotquayGraphFilter.Mode.PACKAGES)
	runner.check(view.get_node_or_null("DotquayNode_a") == null, "NuGet hides addons")
	runner.equal(_package_node(view, "Serilog").position_offset.x, DotquayDependencyGraphView.MARGIN.x,
		"with no addons drawn, packages start at the left edge")
	runner.equal(view.get_connection_list().size(), 0, "no edge is drawn to a hidden addon")

	view.set_filter(DotquayGraphFilter.Mode.ALL)
	runner.check(_package_node(view, "Serilog") != null and view.get_node_or_null("DotquayNode_a") != null,
		"All brings both back")

	view.free()


func test_graph_view_remembers_a_dragged_package_separately_from_its_namesake_addon() -> void:
	var view: DotquayDependencyGraphView = VIEW_SCENE.instantiate()
	var graph: DotquayGraph = GRAPH.from_payload(_addon_and_package_graph())

	view.render(graph)
	_package_node(view, "a").position_offset = Vector2(555, 444)
	var addon_position: Vector2 = (view.get_node("DotquayNode_a") as GraphNode).position_offset

	view.render(graph)

	runner.equal(_package_node(view, "a").position_offset, Vector2(555, 444), "the package keeps its dragged spot")
	runner.equal((view.get_node("DotquayNode_a") as GraphNode).position_offset, addon_position,
		"and the addon of the same name didn't inherit it")

	view.free()
