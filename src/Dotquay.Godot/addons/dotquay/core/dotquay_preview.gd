@tool
class_name DotquayPreview
extends RefCounted

## A typed view over the `add --preview --json` payload (SPEC.md §6, §11.3).
##
## Same job as [DotquayGraph], for a different question. `graph` describes what the project has;
## this describes what one `add` would change — the transitive addons it drags in, the NuGet packages
## those addons declare, and the feeds they would ask the project to trust.
##
## Nothing here is computed by the plugin. Every field is something the resolver actually said, which
## is the point: a confirmation dialog that guessed at the consequences would be worse than no dialog,
## because the user would have agreed to the guess.


class Addon extends RefCounted:
	var name: String = ""
	var git: String = ""
	var version: String = ""
	var resolved: String = ""
	var short_sha: String = ""
	var folder: String = ""
	var is_new: bool = false
	## The commit currently locked, empty when this addon is not locked at all. Non-empty alongside
	## [member is_new] is the difference between "arrives" and "moves".
	var previous_resolved: String = ""
	var required_by: PackedStringArray = PackedStringArray()

	static func from_payload(payload: Dictionary) -> Addon:
		var addon := Addon.new()
		addon.name = String(payload.get("name", ""))
		addon.git = String(payload.get("git", ""))
		addon.version = DotquayWire.str_or_empty(payload.get("version"))
		addon.resolved = String(payload.get("resolved", ""))
		addon.short_sha = String(payload.get("short_sha", ""))
		addon.folder = String(payload.get("folder", addon.name))
		addon.is_new = bool(payload.get("is_new", false))
		addon.previous_resolved = DotquayWire.str_or_empty(payload.get("previous_resolved"))

		for requirer in payload.get("required_by", []):
			addon.required_by.append(String(requirer))

		return addon

	## True when the user asked for this addon by name, rather than inheriting it from another one.
	func is_requested() -> bool:
		return required_by.has(DotquayWire.PROJECT_NODE)

	## True when this addon is already installed and would move to a different commit.
	func is_move() -> bool:
		return is_new and not previous_resolved.is_empty()

	func display_version() -> String:
		return version if not version.is_empty() else short_sha

	## One line naming who wants it, for a row that has to explain itself without a tooltip.
	func describe_origin() -> String:
		if is_requested():
			return "you asked for this"

		var names := PackedStringArray()
		for requirer in required_by:
			names.append(DotquayWire.display_requirer(requirer))

		return "required by " + ", ".join(names)

	## What the row's leading marker means, kept next to the states it describes so a new state can't
	## be added without deciding how it reads.
	func marker() -> String:
		if not is_new:
			return "="
		return "~" if is_move() else "+"


class DotnetPackage extends RefCounted:
	var name: String = ""
	var constraint: String = ""
	## The addon whose package manifest asked for it; empty when the project declares it directly.
	var declared_by_addon: String = ""
	var is_new: bool = false

	static func from_payload(payload: Dictionary) -> DotnetPackage:
		var package := DotnetPackage.new()
		package.name = String(payload.get("name", ""))
		package.constraint = DotquayWire.str_or_empty(payload.get("constraint"))
		package.declared_by_addon = DotquayWire.str_or_empty(payload.get("declared_by_addon"))
		package.is_new = bool(payload.get("is_new", false))
		return package

	func describe_origin() -> String:
		return ("declared by this project" if declared_by_addon.is_empty()
			else "required by %s" % declared_by_addon)


class Source extends RefCounted:
	var name: String = ""
	var url: String = ""
	var declared_by_addon: String = ""
	## True when [nuget] trusted_hosts already covers this host, so adoption is automatic. A UI must
	## say so rather than offer a checkbox that changes nothing.
	var is_trusted_host: bool = false
	## The package ID patterns this feed would own — the concrete scope of what adopting it grants.
	var packages: PackedStringArray = PackedStringArray()
	## The declaring addon's own [dotnet] packages, i.e. what actually depends on the answer.
	var package_ids: PackedStringArray = PackedStringArray()

	static func from_payload(payload: Dictionary) -> Source:
		var source := Source.new()
		source.name = String(payload.get("name", ""))
		source.url = String(payload.get("url", ""))
		source.declared_by_addon = String(payload.get("declared_by_addon", ""))
		source.is_trusted_host = bool(payload.get("is_trusted_host", false))

		for pattern in payload.get("packages", []):
			source.packages.append(String(pattern))

		for package_id in payload.get("package_ids", []):
			source.package_ids.append(String(package_id))

		return source

	## The host, for a sentence about trust. Parsed rather than shown whole because a GitLab feed URL
	## is 70 characters of path and 18 of the thing that matters.
	func host() -> String:
		var without_scheme := url.trim_prefix("https://").trim_prefix("http://")
		var slash := without_scheme.find("/")
		return without_scheme if slash < 0 else without_scheme.substr(0, slash)

	## What this feed would be allowed to serve. Falls back to the concrete package IDs, mirroring the
	## CLI's own default when an addon declares no `packages` patterns.
	func owned_patterns() -> PackedStringArray:
		return packages if not packages.is_empty() else package_ids


var name: String = ""
var git: String = ""
var addons: Array = []                     ## Array[Addon]
var dotnet: Array = []                     ## Array[DotnetPackage]
var sources_to_adopt: Array = []           ## Array[Source]
var sources_needing_approval: Array = []   ## Array[Source]
var needs_approval: bool = false
var is_no_op: bool = false


static func from_payload(payload: Dictionary) -> DotquayPreview:
	var preview := DotquayPreview.new()
	preview.name = String(payload.get("name", ""))
	preview.git = String(payload.get("git", ""))
	preview.needs_approval = bool(payload.get("needs_approval", false))
	preview.is_no_op = bool(payload.get("is_no_op", false))

	for entry in payload.get("addons", []):
		if typeof(entry) == TYPE_DICTIONARY:
			preview.addons.append(Addon.from_payload(entry))

	for entry in payload.get("dotnet", []):
		if typeof(entry) == TYPE_DICTIONARY:
			preview.dotnet.append(DotnetPackage.from_payload(entry))

	for entry in payload.get("sources_to_adopt", []):
		if typeof(entry) == TYPE_DICTIONARY:
			preview.sources_to_adopt.append(Source.from_payload(entry))

	for entry in payload.get("sources_needing_approval", []):
		if typeof(entry) == TYPE_DICTIONARY:
			preview.sources_needing_approval.append(Source.from_payload(entry))

	return preview


## The addons the user did not ask for. The count that makes the dialog worth showing at all — an add
## that pulls in nothing extra needs no explaining.
func transitive_addons() -> Array:
	return addons.filter(func(addon: Addon) -> bool: return addon.is_new and not addon.is_requested())


func new_addons() -> Array:
	return addons.filter(func(addon: Addon) -> bool: return addon.is_new)


func new_packages() -> Array:
	return dotnet.filter(func(package: DotnetPackage) -> bool: return package.is_new)


## Every feed the add would touch, approved or not — the dialog lists them together so a user reading
## "feeds" sees all of them, with each row saying which kind it is.
func all_sources() -> Array:
	var combined: Array = []
	combined.append_array(sources_to_adopt)
	combined.append_array(sources_needing_approval)
	return combined


## A one-line summary for the dialog's headline.
func summarize() -> String:
	if is_no_op:
		return "'%s' is already installed at this commit — nothing on disk would change." % name

	var parts := PackedStringArray()
	parts.append(DotquayText.plural(new_addons().size(), "%d addon", "%d addons"))

	var transitive := transitive_addons().size()
	if transitive > 0:
		parts.append("%d of them pulled in as dependencies" % transitive)

	var packages := new_packages().size()
	if packages > 0:
		parts.append(DotquayText.plural(packages, "%d NuGet package", "%d NuGet packages"))

	var feeds := all_sources().size()
	if feeds > 0:
		parts.append(DotquayText.plural(feeds, "%d NuGet feed", "%d NuGet feeds"))

	# "Adding … installs …", not "Installing … adds …" (P1-14) — matches the dialog's own "Add '%s'?"
	# title, and stays clear of the toolbar's unrelated Install button.
	return "Adding '%s' installs " % name + ", ".join(parts) + "."
