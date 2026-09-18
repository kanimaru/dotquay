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
	## NuGet package IDs this addon's own package manifest declares (v0.19). This isn't a wire field
	## on the addon: [method DotquayGraph.from_payload] fills it in as the reverse of each package's
	## `required_by`. The node needs it to know it has a right-hand slot even when it has no addon
	## children.
	var packages: PackedStringArray = PackedStringArray()

	static func from_payload(payload: Dictionary) -> AddonNode:
		var node := AddonNode.new()
		# str_or_empty throughout (C-23), not a plain String() default — SPEC.md §17 emits an absent
		# optional value as JSON null, never by omitting the key, so a bare String() default only ever
		# fires for a schema this plugin has never seen; a real, present-but-null value would otherwise
		# render as the literal text "<null>".
		node.name = DotquayWire.str_or_empty(payload.get("name"))
		node.git = DotquayWire.str_or_empty(payload.get("git"))
		node.version = DotquayWire.str_or_empty(payload.get("version"))
		node.resolved = DotquayWire.str_or_empty(payload.get("resolved"))
		node.short_sha = DotquayWire.str_or_empty(payload.get("short_sha"))
		# Falls back to the name — both when the key is absent (a payload from a dotquay predating
		# install_as) and when it is present but null (SPEC.md §17: emitted as null, not omitted, when
		# it equals the name).
		var folder := DotquayWire.str_or_empty(payload.get("folder"))
		node.folder = folder if not folder.is_empty() else node.name
		# A null origin would otherwise render as the literal text "<null>", which is neither
		# ORIGIN_ROOT nor ORIGIN_ORPHAN — is_removable() would then read a perfectly normal root addon
		# as unremovable, with no error to explain why.
		var origin := DotquayWire.str_or_empty(payload.get("origin"))
		node.origin = origin if not origin.is_empty() else DotquayWire.ORIGIN_ROOT
		node.source_kind = DotquayWire.str_or_empty(payload.get("source_kind"))
		node.constraint = DotquayWire.str_or_empty(payload.get("constraint"))
		node.is_overridden = bool(payload.get("is_overridden", false))
		node.is_patched = bool(payload.get("is_patched", false))
		node.status = DotquayWire.str_or_empty(payload.get("status"))

		for dependency in DotquayWire.array_or_empty(payload.get("dependencies")):
			node.dependencies.append(String(dependency))

		for requirement in DotquayWire.array_or_empty(payload.get("requirements")):
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
		requirement.requirer = DotquayWire.str_or_empty(payload.get("requirer"))
		requirement.kind = DotquayWire.str_or_empty(payload.get("kind"))
		# str_or_empty, not a plain String() default (C-23): "constraint" is the same wire key AddonNode
		# and PackageNode already treat as nullable below — a version-less requirement (a plain `git`
		# dependency with no [dotnet]-style range) sends it as null, not omitted.
		requirement.constraint = DotquayWire.str_or_empty(payload.get("constraint"))
		requirement.git = DotquayWire.str_or_empty(payload.get("git"))
		return requirement

	func describe() -> String:
		return "%s wants %s %s" % [
			DotquayWire.display_requirer(requirer), DotquayWire.display_source_kind(kind), constraint]


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
		var kind := DotquayWire.str_or_empty(payload.get("kind"))
		candidate.kind = kind if not kind.is_empty() else DotquayWire.SOURCE_VERSION
		candidate.value = DotquayWire.str_or_empty(payload.get("value"))
		candidate.git = DotquayWire.str_or_empty(payload.get("git"))
		candidate.resolved = DotquayWire.str_or_empty(payload.get("resolved"))

		for requirer in DotquayWire.array_or_empty(payload.get("satisfies")):
			candidate.satisfies.append(String(requirer))

		candidate.satisfies_all = bool(payload.get("satisfies_all", false))
		candidate.install_as = DotquayWire.str_or_empty(payload.get("install_as"))
		return candidate

	## True when accepting this candidate means choosing a folder rather than a version.
	func is_folder_choice() -> bool:
		return not install_as.is_empty()

	## What to show on the picker's radio button.
	func display_label() -> String:
		return ("install as addons/%s" % install_as if is_folder_choice()
			else "%s %s" % [DotquayWire.display_source_kind(kind), value])

	func describe_satisfies() -> String:
		if satisfies_all:
			return "works for everything that needs it"
		if satisfies.is_empty():
			return "works for nothing on its own"

		var names := PackedStringArray()
		for requirer in satisfies:
			names.append(DotquayWire.display_requirer(requirer))

		return "works for " + ", ".join(names)


class Conflict extends RefCounted:
	var name: String = ""
	var kind: String = ""
	var message: String = ""
	var requirements: Array = []   ## Array[Requirement]
	var candidates: Array = []     ## Array[Candidate]

	## True for a synthetic conflict built by "Always use a specific version..." on an addon that has
	## no real conflict (DotquayPanel._open_conflict_dialog picking this addon's own version picker),
	## as opposed to one parsed from a real `graph --resolve` payload. Kept as its own flag rather than
	## inferred from a blank [member kind]: a genuine conflict can arrive with a kind this plugin
	## doesn't recognise too, and that case deserves the generic "could not be resolved" headline, not
	## a dialog that quietly relabels itself as something the user never asked for (P1-18).
	var is_manual_pin: bool = false

	static func from_payload(payload: Dictionary) -> Conflict:
		var conflict := Conflict.new()
		conflict.name = DotquayWire.str_or_empty(payload.get("name"))
		conflict.kind = DotquayWire.str_or_empty(payload.get("kind"))
		conflict.message = DotquayWire.str_or_empty(payload.get("message"))

		for requirement in DotquayWire.array_or_empty(payload.get("requirements")):
			if typeof(requirement) == TYPE_DICTIONARY:
				conflict.requirements.append(Requirement.from_payload(requirement))

		for candidate in DotquayWire.array_or_empty(payload.get("candidates")):
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
		if is_manual_pin:
			return "Pin '%s' to a version" % name

		match kind:
			DotquayWire.CONFLICT_DIFFERENT_GIT_URLS:
				return "'%s' is required from two different repositories." % name
			DotquayWire.CONFLICT_DISJOINT_PINS:
				return "'%s' is pinned to incompatible commits." % name
			DotquayWire.CONFLICT_NO_COMMON_VERSION:
				return "No released version of '%s' works for everything that needs it." % name
			DotquayWire.CONFLICT_INVALID_CONSTRAINT:
				return "'%s' has a version requirement dotquay can't read. Check its entry in dotquay.toml." % name
			DotquayWire.CONFLICT_DISAGREEING_INSTALL_FOLDERS:
				return "'%s' is wanted in two different folders under addons/." % name
			_:
				return "'%s' could not be resolved." % name


	## The dialog's window title (P1-18). A real conflict keeps one fixed, generic title, since
	## [method headline] already names the specific problem in the dialog body; a manual pin has
	## nothing else worth calling itself, so its title and headline say the same thing.
	func dialog_title() -> String:
		return headline() if is_manual_pin else "Resolve dependency conflict"


	## The heading over the candidate list — "force" reads as an escape hatch you reach for under
	## duress, which is wrong for a pin chosen on an addon that was never in trouble (P1-18).
	func candidates_heading() -> String:
		return "Choose the version dotquay should always use" if is_manual_pin else "Pick a version to force"


	## The dialog's OK button. "Apply override" is accurate CLI vocabulary and unhelpful UI vocabulary
	## on a healthy addon that was never overridden until this click (P1-18).
	func ok_button_label() -> String:
		return "Pin" if is_manual_pin else "Apply override"


## One NuGet package, drawn as its own kind of node in the graph (DESIGN-v0.19).
##
## This replaces the old `DotnetEntry`, which knew only `declared_by_project`. That was enough to
## grey out Remove, but not enough to say *which* addon pulled a package in, and "which one" is the
## whole question.
class PackageNode extends RefCounted:
	const NUGET_PACKAGE_URL := "https://www.nuget.org/packages/%s"

	var name: String = ""
	var constraint: String = ""
	var resolved: String = ""
	var declared_by_project: bool = true
	var origin: String = DotquayWire.ORIGIN_ROOT
	## `"<project>"` first when the project declares it, then addon names — exactly as the CLI sent
	## them.
	var required_by: PackedStringArray = PackedStringArray()
	## The declared feed name it resolved from. Empty means nuget.org.
	var source: String = ""

	static func from_payload(payload: Dictionary) -> PackageNode:
		var node := PackageNode.new()
		node.name = DotquayWire.str_or_empty(payload.get("name"))
		node.constraint = DotquayWire.str_or_empty(payload.get("constraint"))
		node.resolved = DotquayWire.str_or_empty(payload.get("resolved"))
		node.declared_by_project = bool(payload.get("declared_by_project", true))
		node.source = DotquayWire.str_or_empty(payload.get("source"))

		for requirer in DotquayWire.array_or_empty(payload.get("required_by")):
			node.required_by.append(String(requirer))

		# A dotquay older than v0.19 sends no origin. declared_by_project is the most that CLI could
		# say, and it can't tell a transitive package from an orphan, so it reads as transitive: the
		# one choice that never offers a Remove that would fail.
		var origin := DotquayWire.str_or_empty(payload.get("origin"))
		if origin.is_empty():
			origin = DotquayWire.ORIGIN_ROOT if node.declared_by_project else DotquayWire.ORIGIN_TRANSITIVE
		node.origin = origin
		return node

	func display_version() -> String:
		if not resolved.is_empty():
			return resolved
		if not constraint.is_empty():
			return constraint
		return "?"

	func is_root() -> bool:
		return origin == DotquayWire.ORIGIN_ROOT

	func is_transitive() -> bool:
		return origin == DotquayWire.ORIGIN_TRANSITIVE

	func is_orphan() -> bool:
		return origin == DotquayWire.ORIGIN_ORPHAN

	## True when `dotquay remove <id>` can succeed. A root has a manifest entry to delete, and an
	## orphan has a lock entry nothing else defends (DESIGN-v0.19 §4). A transitive package has
	## neither: its addon would bring it straight back.
	func is_removable() -> bool:
		return is_root() or is_orphan()

	## The addons that declared this package, without the project sentinel. These are the nodes the
	## graph draws an edge from.
	func addon_requirers() -> PackedStringArray:
		var result := PackedStringArray()
		for requirer in required_by:
			if requirer != DotquayWire.PROJECT_NODE:
				result.append(requirer)
		return result

	## True when the package resolved from nuget.org, the only feed with a public package page to
	## open. A private feed's URL is an API endpoint, not something a browser can show.
	func is_on_public_feed() -> bool:
		return source.is_empty()

	## The package's nuget.org page, pinned to the resolved version when there is one.
	func nuget_url() -> String:
		var url := NUGET_PACKAGE_URL % name.uri_encode()
		return url + "/" + resolved.uri_encode() if not resolved.is_empty() else url


var source: String = DotquayWire.GRAPH_SOURCE_LOCKFILE
var project_directory: String = ""
var roots: PackedStringArray = PackedStringArray()
var addons: Array = []      ## Array[AddonNode]
var edges: Array = []       ## Array of {from: String, to: String}
var dotnet: Array = []      ## Array[PackageNode]
var conflicts: Array = []   ## Array[Conflict]


static func from_payload(payload: Dictionary) -> DotquayGraph:
	var graph := DotquayGraph.new()
	var source := DotquayWire.str_or_empty(payload.get("source"))
	graph.source = source if not source.is_empty() else DotquayWire.GRAPH_SOURCE_LOCKFILE
	graph.project_directory = DotquayWire.str_or_empty(payload.get("project_directory"))

	for root in DotquayWire.array_or_empty(payload.get("roots")):
		graph.roots.append(String(root))

	for entry in DotquayWire.array_or_empty(payload.get("addons")):
		if typeof(entry) == TYPE_DICTIONARY:
			graph.addons.append(AddonNode.from_payload(entry))

	for entry in DotquayWire.array_or_empty(payload.get("edges")):
		if typeof(entry) == TYPE_DICTIONARY:
			graph.edges.append({
				"from": DotquayWire.str_or_empty(entry.get("from")),
				"to": DotquayWire.str_or_empty(entry.get("to")),
			})

	for entry in DotquayWire.array_or_empty(payload.get("dotnet")):
		if typeof(entry) == TYPE_DICTIONARY:
			graph.dotnet.append(PackageNode.from_payload(entry))

	graph._index_packages_by_addon()

	for entry in DotquayWire.array_or_empty(payload.get("conflicts")):
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


func find_package(package_name: String) -> PackageNode:
	for node in dotnet:
		if node.name == package_name:
			return node
	return null


## Fills [member AddonNode.packages] from each package's `required_by`. Done once at parse time,
## so a node asking "do I declare any packages?" never has to scan every package itself.
func _index_packages_by_addon() -> void:
	for package in dotnet:
		for requirer in package.addon_requirers():
			var addon := find(requirer)
			if addon != null and not addon.packages.has(package.name):
				addon.packages.append(package.name)


## The column packages are drawn in: one to the right of the deepest addon column. Packages are
## always sinks, so giving them a column of their own keeps them from interleaving with addons that
## happen to sit at the same depth. The layout also stays put when an addon's depth changes.
func package_column() -> int:
	return layers().size()


## Package names in drawing order: sorted, so refreshes don't shuffle the column.
func package_names() -> PackedStringArray:
	var names := PackedStringArray()
	for node in dotnet:
		names.append(node.name)
	names.sort()
	return names


func conflict_for(addon_name: String) -> Conflict:
	for conflict in conflicts:
		if conflict.name == addon_name:
			return conflict
	return null


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

	for package in dotnet:
		if package.is_orphan():
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
