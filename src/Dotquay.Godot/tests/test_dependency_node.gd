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


func test_remove_disabled_reason_for_an_orphan_names_update_all_as_the_fix() -> void:
	# P1-20: `SyncAddonGraphAsync` (Dotquay.Core) deletes any lockfile addon a fresh manifest-rooted
	# walk doesn't reach, so any Update — not just this addon's own, which it doesn't have — clears an
	# orphan. Verified by reading the workflow, not assumed: this review used to leave the remedy
	# unstated for exactly that reason.
	var reason := NODE.remove_disabled_reason(false, false, true, [])
	runner.check(reason.contains("root entry"),
		"an orphan's Remove is disabled because it isn't a root entry, not because of anything CLI-side: %s" % reason)
	runner.check(reason.contains("Update all"), "should name the actual remedy: %s" % reason)


func test_update_disabled_reason_is_blank_for_a_root_addon() -> void:
	runner.equal(NODE.update_disabled_reason(false, false, []), "",
		"an addon with its own manifest entry can always be updated by name")


func test_update_disabled_reason_for_a_transitive_addon_points_at_its_requirer() -> void:
	var reason := NODE.update_disabled_reason(true, false, [_requirement("core")])
	runner.equal(reason, "Brought in by core; update that instead.",
		"a transitive addon's Update points at the same fix as its Remove")


func test_update_disabled_reason_for_an_orphan_names_update_all() -> void:
	# `dotquay update <orphan-name>` throws "not declared in dotquay.toml" (DependencyWorkflow.cs) —
	# an orphan is exactly the addon that was never a manifest root, so Update-by-name would always
	# fail for it. Update is disabled for the same reason Remove is, but the explanation differs.
	var reason := NODE.update_disabled_reason(false, true, [])
	runner.check(reason.contains("Update all"), "should name the actual remedy: %s" % reason)


func test_truncate_title_leaves_short_names_alone() -> void:
	runner.equal(NODE.truncate_title("gut"), "gut", "a short name needs no truncation")


func test_truncate_title_leaves_a_name_exactly_at_the_limit_alone() -> void:
	var exact_length_name := "a".repeat(NODE.TITLE_MAX_LENGTH)
	runner.equal(NODE.truncate_title(exact_length_name), exact_length_name,
		"a name exactly at the limit is not itself too long")


func test_truncate_title_shortens_long_names_with_a_trailing_ellipsis() -> void:
	var long_name := "a_very_long_addon_folder_name_indeed_longer_than_any_node_is_wide"
	var truncated: String = NODE.truncate_title(long_name)

	runner.check(truncated.length() <= NODE.TITLE_MAX_LENGTH,
		"a truncated title must not exceed the configured max length: %s" % truncated)
	runner.check(truncated.ends_with("…"), "a truncated title should end with an ellipsis: %s" % truncated)
	runner.check(long_name.begins_with(truncated.substr(0, truncated.length() - 1)),
		"the visible prefix should still be a real prefix of the original name: %s" % truncated)
