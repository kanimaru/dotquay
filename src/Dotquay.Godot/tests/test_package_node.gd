extends RefCounted

## Covers what [DotquayPackageNode] says about itself: its detail lines, which are static and
## data-only, and the real scene rendered detached.

const GRAPH := preload("res://addons/dotquay/core/dotquay_graph.gd")
const NODE := preload("res://addons/dotquay/ui/package_node.gd")

var runner: Object


func _package(payload: Dictionary) -> DotquayGraph.PackageNode:
	return GRAPH.PackageNode.from_payload(payload)


func _texts(lines: Array) -> PackedStringArray:
	var result := PackedStringArray()
	for line in lines:
		result.append(line[0])
	return result


func test_root_package_shows_its_declared_range() -> void:
	var lines := _texts(NODE.detail_lines(_package(
		{"name": "X", "origin": "root", "constraint": "13.0.*", "required_by": ["<project>"]})))

	runner.check(lines.has("range 13.0.*"), "the project's own range is worth showing: %s" % lines)


func test_transitive_package_names_the_addons_that_asked_for_it() -> void:
	var lines := _texts(NODE.detail_lines(_package({"name": "X", "origin": "transitive", "required_by": ["a", "b"]})))

	# Named, not "an addon": with the NuGet filter on there's no edge to follow.
	runner.check(lines.has("required by a, b"), "names the declaring addons: %s" % lines)


func test_root_package_also_declared_by_an_addon_says_so() -> void:
	var lines := _texts(NODE.detail_lines(_package(
		{"name": "X", "origin": "root", "required_by": ["<project>", "a"]})))

	runner.check(lines.has("also needed by a"), "the project sentinel isn't repeated as a name: %s" % lines)


func test_orphan_package_has_no_requirer_line() -> void:
	var lines := _texts(NODE.detail_lines(_package({"name": "X", "origin": "orphan", "required_by": []})))

	runner.equal(lines.size(), 0, "nothing declares an orphan, so there's nobody to name")


func test_private_feed_is_named() -> void:
	var lines := _texts(NODE.detail_lines(_package({"name": "X", "origin": "root", "source": "kemomi"})))

	runner.check(lines.has("feed: kemomi"), "a package from a private feed says which: %s" % lines)


func test_node_name_cannot_collide_with_an_addon_node() -> void:
	# The addon node is "DotquayNode_<name>"; an addon and a package called the same thing must stay
	# two nodes.
	runner.check(NODE.NAME_PREFIX != "DotquayNode_", "package nodes use their own name prefix")


func test_incoming_slot_only_when_a_drawn_addon_declared_it() -> void:
	var declared := _package({"name": "X", "origin": "transitive", "required_by": ["a"]})
	var project_only := _package({"name": "Y", "origin": "root", "required_by": ["<project>"]})

	runner.check(NODE.has_incoming_slot(declared, true), "an addon's package takes its edge on the left")
	runner.check(not NODE.has_incoming_slot(declared, false),
		"with addons hidden by the filter, there's nothing to connect in from")
	runner.check(not NODE.has_incoming_slot(project_only, true),
		"the project isn't drawn, so its own package gets no slot")
