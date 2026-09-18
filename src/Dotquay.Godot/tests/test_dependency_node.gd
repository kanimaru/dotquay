extends RefCounted

## Covers [DotquayDependencyNode]'s title truncation (P1-23) and its slot rule. Its menu and the
## disabled-item explanations moved to [DotquayNodeActions] in v0.19; see
## test_dotquay_node_actions.gd.

const NODE := preload("res://addons/dotquay/ui/dependency_node.gd")
const GRAPH := preload("res://addons/dotquay/core/dotquay_graph.gd")

var runner: Object


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


func test_outgoing_slot_follows_addon_children_and_visible_packages() -> void:
	var packages_only := GRAPH.AddonNode.from_payload({"name": "a", "dependencies": []})
	packages_only.packages = PackedStringArray(["Serilog"])
	var with_child := GRAPH.AddonNode.from_payload({"name": "b", "dependencies": ["core"]})

	runner.check(NODE.has_outgoing_slot(packages_only, true), "declared packages need an outgoing slot")
	runner.check(not NODE.has_outgoing_slot(packages_only, false),
		"not when the filter hides packages: it would dangle")
	runner.check(NODE.has_outgoing_slot(with_child, false), "an addon child always needs one")
