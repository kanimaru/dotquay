@tool
class_name DotquayNodeActions
extends RefCounted

## What each graph node's Actions menu offers, and why an item is greyed out, for both node kinds
## (DESIGN-v0.19 §5).
##
## The menu is a list of plain [DotquayNodeActions.Entry] values built from plain data, so every
## rule in it is a test rather than something only visible by right-clicking a node. Before v0.19
## this lived on the addon node. A second node kind would have meant either copying it or growing
## one menu that serves both kinds; the package node's greyed-out "Open repository" (tooltip: "it
## was likely declared as a NuGet package") was the symptom of the second option.
##
## The `MENU_*` ids name what a person is *asking* for ("update"). They are deliberately separate
## from [DotquayWire]'s `ACTION_*`, which name what a command already *did* ("updated") (C-25).

const MENU_UPDATE := "update"
const MENU_REMOVE := "remove"
const MENU_RESOLVE := "resolve"
const MENU_OVERRIDE := "override"
const MENU_CLEAR_OVERRIDE := "clear_override"
const MENU_OPEN_REPO := "open_repo"
const MENU_CAPTURE_PATCH := "capture_patch"
const MENU_EXPLAIN := "explain"
const MENU_OPEN_NUGET := "open_nuget"


## One row of a node's menu: an item, or a separator when [member is_separator] is set.
class Entry extends RefCounted:
	var label: String = ""
	var action: String = ""
	var disabled: bool = false
	## Shown as the item's tooltip, and only when [member disabled]. A greyed-out option says why
	## instead of just sitting there (P1-19).
	var reason: String = ""
	var is_separator: bool = false

	static func item(label: String, action: String, disabled: bool = false, reason: String = "") -> Entry:
		var entry := Entry.new()
		entry.label = label
		entry.action = action
		entry.disabled = disabled
		entry.reason = reason if disabled else ""
		return entry

	static func separator() -> Entry:
		var entry := Entry.new()
		entry.is_separator = true
		return entry


## The addon menu. The order is deliberate: resolve first when there is something to resolve, and
## "why is this modified?" above "keep my edits", because the honest order is to find out what
## changed first and then decide what to do about it.
static func for_addon(model: DotquayGraph.AddonNode, conflict: DotquayGraph.Conflict) -> Array:
	var entries: Array = []
	var is_transitive := model.origin == DotquayWire.ORIGIN_TRANSITIVE

	if conflict != null:
		entries.append(Entry.item("Resolve conflict...", MENU_RESOLVE))

	# A transitive addon has no manifest entry to re-resolve on its own; updating it means updating
	# whatever requires it. An orphan has no manifest entry either: `dotquay update <name>` throws
	# "not declared in dotquay.toml" for one, since it was never a root to begin with (P1-20).
	entries.append(Entry.item("Update", MENU_UPDATE, is_transitive or model.is_orphan(),
		update_disabled_reason(is_transitive, model.is_orphan(), model.requirements)))
	entries.append(Entry.item("Always use a specific version...", MENU_OVERRIDE))

	if model.is_overridden:
		entries.append(Entry.item("Stop forcing a version", MENU_CLEAR_OVERRIDE))

	if model.status == DotquayWire.STATUS_MODIFIED:
		entries.append(Entry.item("Why is this modified?", MENU_EXPLAIN))
		entries.append(Entry.item("Keep my edits (save as patch)", MENU_CAPTURE_PATCH))

	entries.append(Entry.separator())

	entries.append(Entry.item("Open repository", MENU_OPEN_REPO, model.git.is_empty(),
		"This addon has no repository URL to open."))
	entries.append(Entry.item("Remove", MENU_REMOVE, not model.is_removable(),
		remove_disabled_reason(model.is_removable(), is_transitive, model.is_orphan(), model.requirements)))

	return entries


## The package menu. Only actions that mean something for NuGet appear at all. There's no
## override (that is `[overrides]`, which is git-only), no patch and no repository.
static func for_package(model: DotquayGraph.PackageNode) -> Array:
	var entries: Array = []

	entries.append(Entry.item("Update", MENU_UPDATE, not model.is_root(), package_update_disabled_reason(model)))

	entries.append(Entry.separator())

	entries.append(Entry.item("Open on nuget.org", MENU_OPEN_NUGET, not model.is_on_public_feed(),
		open_nuget_disabled_reason(model)))
	entries.append(Entry.item("Remove", MENU_REMOVE, not model.is_removable(), package_remove_disabled_reason(model)))

	return entries


## Fills [param popup] with [param entries]. Ids are positional, so the action travels as item
## metadata instead: reordering a menu must not silently change what a click does.
static func apply_to(popup: PopupMenu, entries: Array) -> void:
	popup.clear()

	for entry in entries:
		if entry.is_separator:
			popup.add_separator()
			continue

		var id := popup.get_item_count()
		popup.add_item(entry.label, id)

		var index := popup.get_item_index(id)
		popup.set_item_metadata(index, entry.action)
		popup.set_item_disabled(index, entry.disabled)

		if entry.disabled and not entry.reason.is_empty():
			popup.set_item_tooltip(index, entry.reason)


## The action a click on item [param id] of [param popup] asks for, or "" when it names none.
static func action_for(popup: PopupMenu, id: int) -> String:
	var index := popup.get_item_index(id)
	if index < 0:
		return ""

	var action: Variant = popup.get_item_metadata(index)
	return String(action) if typeof(action) == TYPE_STRING else ""


## Names who actually needs this addon, so the tooltip names a specific thing to go do instead of
## just saying "not here". Falls back to something true but generic when the graph never populated
## [param requirements]; the lockfile-only view of the graph doesn't carry them.
static func requirer_summary(requirements: Array) -> String:
	var names := PackedStringArray()
	for requirement in requirements:
		names.append(requirement.requirer)
	return requirer_names_summary(names)


## [method requirer_summary] for plain names, such as a package's `required_by`.
static func requirer_names_summary(requirers: PackedStringArray) -> String:
	var names := PackedStringArray()
	for requirer in requirers:
		names.append(DotquayWire.display_requirer(requirer))
	return ", ".join(names) if not names.is_empty() else "another addon"


## Explains a disabled Update/Remove on a transitive addon: it has no entry of its own in
## dotquay.toml, so [param verb] has nothing to act on until it's applied to whatever pulled this in.
static func brought_in_by_reason(is_transitive: bool, requirements: Array, verb: String) -> String:
	if not is_transitive:
		return ""
	return "Brought in by %s; %s that instead." % [requirer_summary(requirements), verb]


## Explains a disabled Update on an orphan addon: it has no manifest entry to update either (P1-20),
## since `dotquay update <name>` refuses one outright. The tooltip names the command that actually
## clears it instead of just saying "no".
static func update_disabled_reason(is_transitive: bool, is_orphan: bool, requirements: Array) -> String:
	if is_transitive:
		return brought_in_by_reason(true, requirements, "update")
	if is_orphan:
		return ("Nothing requires this any more, so it has no manifest entry to update. Running "
			+ "\"Update all\" removes it instead.")
	return ""


static func remove_disabled_reason(
	is_removable: bool, is_transitive: bool, is_orphan: bool, requirements: Array
) -> String:
	if is_removable:
		return ""
	if is_transitive:
		return brought_in_by_reason(true, requirements, "remove")
	if is_orphan:
		return ("Nothing requires this any more, but it isn't a root entry, so Remove has nothing to "
			+ "act on here. Running \"Update all\" removes it automatically instead.")
	return ""


## Updating an addon re-resolves the packages it declares (`UpdateAsync` targets every addon-declared
## package on a non-resync update), so that is the honest pointer for a transitive package.
static func package_update_disabled_reason(model: DotquayGraph.PackageNode) -> String:
	if model.is_transitive():
		return "Brought in by %s; update that instead." % requirer_names_summary(model.addon_requirers())
	if model.is_orphan():
		return "Nothing declares this any more. Remove it, or run \"Update all\"."
	return ""


static func package_remove_disabled_reason(model: DotquayGraph.PackageNode) -> String:
	if model.is_removable():
		return ""
	return ("Declared by %s, not by your dotquay.toml, so removing it here would only last until the "
		% requirer_names_summary(model.addon_requirers()) + "next update. Remove that instead.")


static func open_nuget_disabled_reason(model: DotquayGraph.PackageNode) -> String:
	if model.is_on_public_feed():
		return ""
	return "Resolved from the '%s' feed, which has no public package page." % model.source
