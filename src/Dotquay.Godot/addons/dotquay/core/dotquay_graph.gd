@tool
class_name DotquayGraph
extends RefCounted

## A typed view over the `graph --json` payload (SPEC.md §17).
##
## The UI never touches the raw dictionaries. Parsing once, here, means a renamed wire key breaks in
## one place with a clear default instead of surfacing as an empty label three screens away — and it
## gives the layout code real objects to sort, group, and lay out. Wire literals live in
## [DotquayWire].


class AddonNode extends RefCounted:
	var name: String = ""
	var git: String = ""
	var version: String = ""
	var resolved: String = ""
	var short_sha: String = ""
	var path: String = ""
	## The folder under addons/ this addon installs into — its name unless install_as redirects it.
	var folder: String = ""
	var origin: String = DotquayWire.ORIGIN_ROOT
	var source_kind: String = ""
	var constraint: String = ""
	var is_overridden: bool = false
	var is_patched: bool = false
	var status: String = ""
	var dependencies: PackedStringArray = PackedStringArray()
	var requirements: Array = []   ## Array[Requirement]

	static func from_payload(payload: Dictionary) -> AddonNode:
		var node := AddonNode.new()
		node.name = String(payload.get("name", ""))
		node.git = String(payload.get("git", ""))
		node.version = DotquayWire.str_or_empty(payload.get("version"))
		node.resolved = DotquayWire.str_or_empty(payload.get("resolved"))
		node.short_sha = DotquayWire.str_or_empty(payload.get("short_sha"))
		node.path = String(payload.get("path", ""))
		# Falls back to the name so a payload from a dotquay predating install_as still renders.
		node.folder = String(payload.get("folder", node.name))
		node.origin = String(payload.get("origin", DotquayWire.ORIGIN_ROOT))
		node.source_kind = DotquayWire.str_or_empty(payload.get("source_kind"))
		node.constraint = DotquayWire.str_or_empty(payload.get("constraint"))
		node.is_overridden = bool(payload.get("is_overridden", false))
		node.is_patched = bool(payload.get("is_patched", false))
		node.status = DotquayWire.str_or_empty(payload.get("status"))

		for dependency in payload.get("dependencies", []):
			node.dependencies.append(String(dependency))

		for requirement in payload.get("requirements", []):
			if typeof(requirement) == TYPE_DICTIONARY:
				node.requirements.append(Requirement.from_payload(requirement))

		return node

	## What to show as the version. A branch or rev pin has no tag, so the short SHA is the only
	## honest answer; "unresolved" means the graph could not pin it at all.
	func display_version() -> String:
		if not version.is_empty():
			return version
		if not short_sha.is_empty():
			return short_sha
		return "unresolved"

	func is_healthy() -> bool:
		return (status.is_empty()
			or status == DotquayWire.STATUS_CLEAN
			or status == DotquayWire.STATUS_PATCHED
			or status == DotquayWire.STATUS_LINKED)

	## True when this addon is a live link into a folder on this machine rather than an installed
	## copy of the locked commit.
	func is_linked() -> bool:
		return status == DotquayWire.STATUS_LINKED

	## True when removing this addon is a meaningful action — only the project's own entries can be
	## removed from the manifest, because only they are in it.
	func is_removable() -> bool:
		return origin == DotquayWire.ORIGIN_ROOT

	## True when the addon is locked but nothing requires it any more.
	func is_orphan() -> bool:
		return origin == DotquayWire.ORIGIN_ORPHAN

	## True when install_as sends this addon somewhere other than addons/<name>. Worth surfacing,
	## because "where did this go?" is only ever asked when the two differ.
	func is_renamed() -> bool:
		return not folder.is_empty() and folder != name


class Requirement extends RefCounted:
	var requirer: String = ""
	var kind: String = ""
	var constraint: String = ""
	var git: String = ""

	static func from_payload(payload: Dictionary) -> Requirement:
		var requirement := Requirement.new()
		requirement.requirer = String(payload.get("requirer", ""))
		requirement.kind = String(payload.get("kind", ""))
		requirement.constraint = String(payload.get("constraint", ""))
		requirement.git = String(payload.get("git", ""))
		return requirement

	func describe() -> String:
		return "%s wants %s %s" % [DotquayWire.display_requirer(requirer), kind, constraint]


class Candidate extends RefCounted:
	var kind: String = DotquayWire.SOURCE_VERSION
	var value: String = ""
	var git: String = ""
	var resolved: String = ""
	var satisfies: PackedStringArray = PackedStringArray()
	var satisfies_all: bool = false
	## Set only for a disagreeing_install_folders conflict, where the disagreement is about the
	## destination folder rather than the commit.
	var install_as: String = ""

	static func from_payload(payload: Dictionary) -> Candidate:
		var candidate := Candidate.new()
		candidate.kind = String(payload.get("kind", DotquayWire.SOURCE_VERSION))
		candidate.value = String(payload.get("value", ""))
		candidate.git = String(payload.get("git", ""))
		candidate.resolved = DotquayWire.str_or_empty(payload.get("resolved"))

		for requirer in payload.get("satisfies", []):
			candidate.satisfies.append(String(requirer))

		candidate.satisfies_all = bool(payload.get("satisfies_all", false))
		candidate.install_as = DotquayWire.str_or_empty(payload.get("install_as"))
		return candidate

	## The CLI flag this candidate maps to, so the picker never translates anything itself.
	func override_flag() -> String:
		return "--" + kind

	## True when accepting this candidate means choosing a folder rather than a version.
	func is_folder_choice() -> bool:
		return not install_as.is_empty()

	## What to show on the picker's radio button.
	func display_label() -> String:
		return "install as addons/%s" % install_as if is_folder_choice() else "%s %s" % [kind, value]

	func describe_satisfies() -> String:
		if satisfies_all:
			return "satisfies every requirer"
		if satisfies.is_empty():
			return "satisfies nothing on its own"

		var names := PackedStringArray()
		for requirer in satisfies:
			names.append(DotquayWire.display_requirer(requirer))

		return "satisfies " + ", ".join(names)


class Conflict extends RefCounted:
	var name: String = ""
	var kind: String = ""
	var message: String = ""
	var requirements: Array = []   ## Array[Requirement]
	var candidates: Array = []     ## Array[Candidate]

	static func from_payload(payload: Dictionary) -> Conflict:
		var conflict := Conflict.new()
		conflict.name = String(payload.get("name", ""))
		conflict.kind = String(payload.get("kind", ""))
		conflict.message = String(payload.get("message", ""))

		for requirement in payload.get("requirements", []):
			if typeof(requirement) == TYPE_DICTIONARY:
				conflict.requirements.append(Requirement.from_payload(requirement))

		for candidate in payload.get("candidates", []):
			if typeof(candidate) == TYPE_DICTIONARY:
				conflict.candidates.append(Candidate.from_payload(candidate))

		return conflict

	## Candidates that satisfy every requirer come first — "this one makes everyone happy" is the
	## only ranking a user actually cares about, and burying it under a newer-but-broken tag turns a
	## one-click fix into a reading exercise.
	func ranked_candidates() -> Array:
		var preferred: Array = []
		var rest: Array = []

		for candidate in candidates:
			if candidate.satisfies_all:
				preferred.append(candidate)
			else:
				rest.append(candidate)

		preferred.append_array(rest)
		return preferred

	## A short, plain-language headline. The CLI's own [member message] is the full explanation and is
	## always shown underneath; this is what fits on a dialog's title line.
	func headline() -> String:
		match kind:
			DotquayWire.CONFLICT_DIFFERENT_GIT_URLS:
				return "'%s' is required from two different repositories." % name
			DotquayWire.CONFLICT_DISJOINT_PINS:
				return "'%s' is pinned to incompatible commits." % name
			DotquayWire.CONFLICT_NO_COMMON_VERSION:
				return "No released version of '%s' satisfies every requirer." % name
			DotquayWire.CONFLICT_INVALID_CONSTRAINT:
				return "'%s' has a malformed version constraint." % name
			DotquayWire.CONFLICT_DISAGREEING_INSTALL_FOLDERS:
				return "'%s' is wanted in two different folders under addons/." % name
			_:
				return "'%s' could not be resolved." % name


class DotnetEntry extends RefCounted:
	var name: String = ""
	var constraint: String = ""
	var resolved: String = ""
	var declared_by_project: bool = true

	static func from_payload(payload: Dictionary) -> DotnetEntry:
		var entry := DotnetEntry.new()
		entry.name = String(payload.get("name", ""))
		entry.constraint = DotquayWire.str_or_empty(payload.get("constraint"))
		entry.resolved = DotquayWire.str_or_empty(payload.get("resolved"))
		entry.declared_by_project = bool(payload.get("declared_by_project", true))
		return entry

	func display_version() -> String:
		if not resolved.is_empty():
			return resolved
		if not constraint.is_empty():
			return constraint
		return "?"


var source: String = DotquayWire.GRAPH_SOURCE_LOCKFILE
var project_directory: String = ""
var roots: PackedStringArray = PackedStringArray()
var addons: Array = []      ## Array[AddonNode]
var edges: Array = []       ## Array of {from: String, to: String}
var dotnet: Array = []      ## Array[DotnetEntry]
var conflicts: Array = []   ## Array[Conflict]
var overrides: PackedStringArray = PackedStringArray()
var has_conflicts: bool = false


static func from_payload(payload: Dictionary) -> DotquayGraph:
	var graph := DotquayGraph.new()
	graph.source = String(payload.get("source", DotquayWire.GRAPH_SOURCE_LOCKFILE))
	graph.project_directory = String(payload.get("project_directory", ""))
	graph.has_conflicts = bool(payload.get("has_conflicts", false))

	for root in payload.get("roots", []):
		graph.roots.append(String(root))

	for name in payload.get("overrides", []):
		graph.overrides.append(String(name))

	for entry in payload.get("addons", []):
		if typeof(entry) == TYPE_DICTIONARY:
			graph.addons.append(AddonNode.from_payload(entry))

	for entry in payload.get("edges", []):
		if typeof(entry) == TYPE_DICTIONARY:
			graph.edges.append({"from": String(entry.get("from", "")), "to": String(entry.get("to", ""))})

	for entry in payload.get("dotnet", []):
		if typeof(entry) == TYPE_DICTIONARY:
			graph.dotnet.append(DotnetEntry.from_payload(entry))

	for entry in payload.get("conflicts", []):
		if typeof(entry) == TYPE_DICTIONARY:
			graph.conflicts.append(Conflict.from_payload(entry))

	return graph


func is_empty() -> bool:
	return addons.is_empty() and dotnet.is_empty()


func find(addon_name: String) -> AddonNode:
	for node in addons:
		if node.name == addon_name:
			return node
	return null


func conflict_for(addon_name: String) -> Conflict:
	for conflict in conflicts:
		if conflict.name == addon_name:
			return conflict
	return null


func has_conflict(addon_name: String) -> bool:
	return conflict_for(addon_name) != null


func children_of(addon_name: String) -> PackedStringArray:
	var result := PackedStringArray()
	for edge in edges:
		if edge["from"] == addon_name:
			result.append(edge["to"])
	return result


## Depth of each node from the project root, for a layered left-to-right layout. A node reachable by
## several paths takes its *shallowest* depth, so a shared dependency sits next to the requirer that
## needs it soonest rather than being pushed right by the longest chain that happens to reach it.
func depths() -> Dictionary:
	var result := {}
	var queue: Array[String] = []

	for root in roots:
		if not result.has(root):
			result[root] = 0
			queue.append(root)

	while not queue.is_empty():
		var current: String = queue.pop_front()
		var next_depth: int = int(result[current]) + 1
		for child in children_of(current):
			if not result.has(child):
				result[child] = next_depth
				queue.append(child)

	# Anything the walk never reached (an orphan, or a transitive addon whose parent failed to
	# resolve) still needs a column, or it would silently vanish from the panel.
	for node in addons:
		if not result.has(node.name):
			result[node.name] = 0

	return result


## Addon names grouped by depth, each group sorted by name so the layout is stable across refreshes —
## nodes jumping columns on every reload makes a graph impossible to read.
func layers() -> Array:
	var by_depth := depths()
	var grouped := {}

	for node in addons:
		var depth: int = int(by_depth.get(node.name, 0))
		if not grouped.has(depth):
			grouped[depth] = PackedStringArray()
		grouped[depth].append(node.name)

	var depth_keys := grouped.keys()
	depth_keys.sort()

	var result: Array = []
	for depth in depth_keys:
		var names: PackedStringArray = grouped[depth]
		names.sort()
		result.append(names)

	return result


func counts() -> Dictionary:
	var modified := 0
	var missing := 0
	var patched := 0
	var linked := 0
	var orphans := 0

	for node in addons:
		match node.status:
			DotquayWire.STATUS_MODIFIED:
				modified += 1
			DotquayWire.STATUS_MISSING:
				missing += 1
			DotquayWire.STATUS_PATCHED:
				patched += 1
			DotquayWire.STATUS_LINKED:
				linked += 1
		if node.is_orphan():
			orphans += 1

	return {
		"addons": addons.size(),
		"dotnet": dotnet.size(),
		"conflicts": conflicts.size(),
		"modified": modified,
		"missing": missing,
		"patched": patched,
		"linked": linked,
		"orphans": orphans,
	}
