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
