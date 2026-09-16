extends RefCounted

## Covers [DotquayDependencyNode]'s disabled-menu-item explanations (P1-19) — pure static functions
## of plain data rather than instance methods, precisely so they're testable without instantiating a
## [GraphNode] inside a scene tree.

const NODE := preload("res://addons/dotquay/ui/dependency_node.gd")
const GRAPH := preload("res://addons/dotquay/core/dotquay_graph.gd")

var runner: Object


func _requirement(requirer: String) -> DotquayGraph.Requirement:
	return GRAPH.Requirement.from_payload(
		{"requirer": requirer, "kind": "version", "constraint": "~1.0", "git": ""})


func test_requirer_summary_names_a_single_requirer() -> void:
	runner.equal(NODE.requirer_summary([_requirement("core")]), "core",
		"one requirer is named plainly")


func test_requirer_summary_joins_several_requirers() -> void:
	var summary := NODE.requirer_summary([_requirement("core"), _requirement("shared")])
	runner.equal(summary, "core, shared", "several requirers are joined for one tooltip line")


func test_requirer_summary_falls_back_when_the_graph_carries_none() -> void:
	# The lockfile-only view of the graph never populates requirements — a tooltip still needs
	# something true to say rather than an empty parenthetical.
	runner.equal(NODE.requirer_summary([]), "another addon",
		"no requirement data still reads as a sentence")


func test_requirer_summary_names_the_project_readably() -> void:
	runner.equal(NODE.requirer_summary([_requirement("<project>")]), "this project",
		"'<project>' is wire syntax, not something to show verbatim")


func test_brought_in_by_reason_is_blank_for_a_root_addon() -> void:
	runner.equal(NODE.brought_in_by_reason(false, [], "update"), "",
		"an addon with its own manifest entry never needs this explanation")


func test_brought_in_by_reason_names_the_requirer_and_the_verb() -> void:
	var reason := NODE.brought_in_by_reason(true, [_requirement("core")], "update")
	runner.equal(reason, "Brought in by core; update that instead.",
		"names who to act on instead, and what to do to it")


func test_remove_disabled_reason_is_blank_for_a_removable_addon() -> void:
	runner.equal(NODE.remove_disabled_reason(true, false, false, []), "",
		"Remove is never disabled for a root addon, so there's nothing to explain")


func test_remove_disabled_reason_for_a_transitive_addon_points_at_its_requirer() -> void:
	var reason := NODE.remove_disabled_reason(false, true, false, [_requirement("shared")])
	runner.equal(reason, "Brought in by shared; remove that instead.",
		"a transitive addon's Remove points at the same fix as its Update")


func test_remove_disabled_reason_for_an_orphan_explains_without_promising_a_fix() -> void:
	var reason := NODE.remove_disabled_reason(false, false, true, [])
	runner.check(reason.contains("root entry"),
		"an orphan's Remove is disabled because it isn't a root entry, not because of anything CLI-side: %s" % reason)
