extends RefCounted

## Covers [DotquayNodeActions]: the menu each node kind offers, and every "why is this greyed out"
## explanation (P1-19, DESIGN-v0.19 §5). All of it is plain data, so every cell of the design doc's
## action table is checked here without a [GraphNode] in a tree.

const GRAPH := preload("res://addons/dotquay/core/dotquay_graph.gd")
const ACTIONS := preload("res://addons/dotquay/core/dotquay_node_actions.gd")

var runner: Object


func _requirement(requirer: String) -> DotquayGraph.Requirement:
	return GRAPH.Requirement.from_payload(
		{"requirer": requirer, "kind": "version", "constraint": "~1.0", "git": ""})


func _addon(overrides: Dictionary = {}) -> DotquayGraph.AddonNode:
	var payload := {
		"name": "a", "git": "https://example.com/a", "origin": "root", "status": "clean",
		"is_overridden": false, "dependencies": [], "requirements": [],
	}
	payload.merge(overrides, true)
	return GRAPH.AddonNode.from_payload(payload)


func _package(origin: String, required_by: Array = [], source: Variant = null) -> DotquayGraph.PackageNode:
	return GRAPH.PackageNode.from_payload({
		"name": "Serilog", "resolved": "3.1.1", "origin": origin, "required_by": required_by, "source": source,
		"declared_by_project": origin == "root",
	})


## The entry for [param action], or null.
func _entry(entries: Array, action: String) -> DotquayNodeActions.Entry:
	for entry in entries:
		if not entry.is_separator and entry.action == action:
			return entry
	return null


func _actions(entries: Array) -> PackedStringArray:
	var result := PackedStringArray()
	for entry in entries:
		if not entry.is_separator:
			result.append(entry.action)
	return result


# --- reasons (moved here from the addon node in v0.19) ---

func test_requirer_summary_names_a_single_requirer() -> void:
	runner.equal(ACTIONS.requirer_summary([_requirement("core")]), "core", "one requirer is named plainly")


func test_requirer_summary_joins_several_requirers() -> void:
	runner.equal(ACTIONS.requirer_summary([_requirement("core"), _requirement("shared")]), "core, shared",
		"several requirers are joined for one tooltip line")


func test_requirer_summary_falls_back_when_the_graph_carries_none() -> void:
	# The lockfile-only view of the graph never populates requirements — a tooltip still needs
	# something true to say rather than an empty parenthetical.
	runner.equal(ACTIONS.requirer_summary([]), "another addon", "no requirement data still reads as a sentence")


func test_requirer_summary_names_the_project_readably() -> void:
	runner.equal(ACTIONS.requirer_summary([_requirement("<project>")]), "this project",
		"'<project>' is wire syntax, not something to show verbatim")


func test_brought_in_by_reason_is_blank_for_a_root_addon() -> void:
	runner.equal(ACTIONS.brought_in_by_reason(false, [], "update"), "",
		"an addon with its own manifest entry never needs this explanation")


func test_brought_in_by_reason_names_the_requirer_and_the_verb() -> void:
	runner.equal(ACTIONS.brought_in_by_reason(true, [_requirement("core")], "update"),
		"Brought in by core; update that instead.", "names who to act on instead, and what to do to it")


func test_remove_disabled_reason_is_blank_for_a_removable_addon() -> void:
	runner.equal(ACTIONS.remove_disabled_reason(true, false, false, []), "",
		"Remove is never disabled for a root addon, so there's nothing to explain")


func test_remove_disabled_reason_for_a_transitive_addon_points_at_its_requirer() -> void:
	runner.equal(ACTIONS.remove_disabled_reason(false, true, false, [_requirement("shared")]),
		"Brought in by shared; remove that instead.", "a transitive addon's Remove points at the same fix as its Update")


func test_remove_disabled_reason_for_an_orphan_names_update_all_as_the_fix() -> void:
	# P1-20: a fresh manifest-rooted walk prunes any lockfile addon it doesn't reach, so any Update
	# clears an orphan addon.
	var reason := ACTIONS.remove_disabled_reason(false, false, true, [])
	runner.check(reason.contains("root entry"), "an orphan's Remove is disabled because it isn't a root entry: %s" % reason)
	runner.check(reason.contains("Update all"), "should name the actual remedy: %s" % reason)


func test_update_disabled_reason_is_blank_for_a_root_addon() -> void:
	runner.equal(ACTIONS.update_disabled_reason(false, false, []), "",
		"an addon with its own manifest entry can always be updated by name")


func test_update_disabled_reason_for_a_transitive_addon_points_at_its_requirer() -> void:
	runner.equal(ACTIONS.update_disabled_reason(true, false, [_requirement("core")]),
		"Brought in by core; update that instead.", "a transitive addon's Update points at the same fix as its Remove")


func test_update_disabled_reason_for_an_orphan_names_update_all() -> void:
	var reason := ACTIONS.update_disabled_reason(false, true, [])
	runner.check(reason.contains("Update all"), "should name the actual remedy: %s" % reason)


# --- addon menu ---

func test_addon_menu_never_offers_nuget_actions() -> void:
	var actions := _actions(ACTIONS.for_addon(_addon(), null))

	runner.check(not actions.has(ACTIONS.MENU_OPEN_NUGET), "an addon has no nuget.org page")
	runner.check(actions.has(ACTIONS.MENU_OPEN_REPO), "an addon keeps Open repository")


func test_addon_menu_offers_resolve_only_with_a_conflict() -> void:
	var conflict := DotquayGraph.Conflict.new()
	runner.check(_actions(ACTIONS.for_addon(_addon(), conflict)).has(ACTIONS.MENU_RESOLVE), "a conflict gets Resolve")
	runner.check(not _actions(ACTIONS.for_addon(_addon(), null)).has(ACTIONS.MENU_RESOLVE), "no conflict, no Resolve")


func test_addon_menu_puts_explain_before_capture_for_a_modified_addon() -> void:
	var actions := _actions(ACTIONS.for_addon(_addon({"status": "modified"}), null))

	runner.check(actions.find(ACTIONS.MENU_EXPLAIN) < actions.find(ACTIONS.MENU_CAPTURE_PATCH),
		"find out what changed before deciding to keep it")


func test_addon_menu_disables_update_and_remove_for_a_transitive_addon() -> void:
	var entries := ACTIONS.for_addon(_addon({"origin": "transitive"}), null)

	runner.check(_entry(entries, ACTIONS.MENU_UPDATE).disabled, "a transitive addon has no entry to update")
	runner.check(_entry(entries, ACTIONS.MENU_REMOVE).disabled, "or to remove")


func test_addon_menu_offers_clear_override_only_when_overridden() -> void:
	runner.check(_actions(ACTIONS.for_addon(_addon({"is_overridden": true}), null)).has(ACTIONS.MENU_CLEAR_OVERRIDE),
		"an overridden addon can drop its pin")
	runner.check(not _actions(ACTIONS.for_addon(_addon(), null)).has(ACTIONS.MENU_CLEAR_OVERRIDE),
		"nothing to clear otherwise")


# --- package menu (DESIGN-v0.19 §5, one test per cell) ---

func test_package_menu_offers_only_nuget_actions() -> void:
	var actions := _actions(ACTIONS.for_package(_package("root", ["<project>"])))

	runner.equal(actions, PackedStringArray([ACTIONS.MENU_UPDATE, ACTIONS.MENU_OPEN_NUGET, ACTIONS.MENU_REMOVE]),
		"no override, patch, explain or repository on a package")


func test_root_package_can_be_updated_opened_and_removed() -> void:
	var entries := ACTIONS.for_package(_package("root", ["<project>"]))

	runner.check(not _entry(entries, ACTIONS.MENU_UPDATE).disabled, "root: Update")
	runner.check(not _entry(entries, ACTIONS.MENU_OPEN_NUGET).disabled, "root on nuget.org: Open")
	runner.check(not _entry(entries, ACTIONS.MENU_REMOVE).disabled, "root: Remove")


func test_transitive_package_points_update_at_its_addon() -> void:
	var update := _entry(ACTIONS.for_package(_package("transitive", ["vbeami"])), ACTIONS.MENU_UPDATE)

	runner.check(update.disabled, "a transitive package isn't updated by name")
	runner.equal(update.reason, "Brought in by vbeami; update that instead.", "names the addon to update instead")


func test_transitive_package_remove_names_its_addon() -> void:
	var remove := _entry(ACTIONS.for_package(_package("transitive", ["vbeami", "other"])), ACTIONS.MENU_REMOVE)

	runner.check(remove.disabled, "the addon would bring it straight back")
	runner.check(remove.reason.contains("vbeami, other"), "names every declaring addon: %s" % remove.reason)


func test_orphan_package_can_be_removed_but_not_updated() -> void:
	var entries := ACTIONS.for_package(_package("orphan"))

	runner.check(not _entry(entries, ACTIONS.MENU_REMOVE).disabled, "an orphan's lock entry can be removed (DESIGN-v0.19 §4)")
	var update := _entry(entries, ACTIONS.MENU_UPDATE)
	runner.check(update.disabled, "nothing declares it, so there's nothing to update")
	runner.check(update.reason.contains("Update all"), "names a remedy: %s" % update.reason)


func test_private_feed_package_cannot_be_opened_on_nuget_org() -> void:
	var open := _entry(ACTIONS.for_package(_package("root", ["<project>"], "kemomi")), ACTIONS.MENU_OPEN_NUGET)

	runner.check(open.disabled, "a private feed has no public page")
	runner.check(open.reason.contains("'kemomi'"), "names the feed: %s" % open.reason)


func test_enabled_entries_carry_no_reason() -> void:
	# A tooltip on an enabled item explaining why it's disabled would be a lie.
	for entry in ACTIONS.for_package(_package("root", ["<project>"])):
		if not entry.is_separator:
			runner.equal(entry.reason, "", "%s is enabled, so it has no reason" % entry.action)
