@tool
class_name DotquayDoctor
extends RefCounted

## A typed view over the `doctor --json` payload (SPEC.md §6.1, §7.1.1, §7.1.2, §7.3.1).
##
## One issue per row. `kind` is the CLI's own stable category for it — SPEC.md documents
## `addon_folder_case_mismatch` (§7.1.1), `install_folder_drift` and `package_asset_metadata_drift`
## (§7.1.2), and `fragile_uid_reference` (§7.3.1) among others, but this plugin does not maintain its
## own copy of that list: doctor can add a check dotquay's own docs haven't caught up to describing
## yet, and a UI that only recognised a fixed set would silently drop a real issue's category instead
## of just not having special-cased advice for it. [method Issue.describe] surfaces whatever `kind` the
## CLI actually sent, verbatim, ahead of the message it already wrote for a human.


class Issue extends RefCounted:
	var kind: String = ""
	var message: String = ""

	## The three facts a fix needs, set only for ragile_uid_reference (SPEC.md §18) and "" for
	## every other kind. Carried as data rather than pulled back out of [member message] by a regex
	## guessing at the CLI's sentence shape -- the CLI already computed all three before it ever
	## built that sentence.
	var file: String = ""
	var uid: String = ""
	var res_path: String = ""

	static func from_payload(payload: Dictionary) -> Issue:
		var issue := Issue.new()
		issue.kind = DotquayWire.str_or_empty(payload.get("kind"))
		issue.message = DotquayWire.str_or_empty(payload.get("message"))
		issue.file = DotquayWire.str_or_empty(payload.get("file"))
		issue.uid = DotquayWire.str_or_empty(payload.get("uid"))
		issue.res_path = DotquayWire.str_or_empty(payload.get("res_path"))
		return issue

	## True once the CLI has sent everything a fix needs: which file to open, the literal token to
	## find in it, and the res:// path to put there instead.
	func has_uid_fix() -> bool:
		return not file.is_empty() and not uid.is_empty() and not res_path.is_empty()

	## One compact line in place of the full sentence: the file, the bare token to search for, and
	## the res:// path that replaces it -- everything a hand-edit needs, none of the boilerplate
	## every other issue in the group already said once via [method Group.detail].
	func uid_fix_line() -> String:
		return "%s  |  %s  ->  %s" % [file, uid, res_path]

	## The log line for this issue. Leads with the kind only when the CLI actually sent one, so a
	## dotquay predating this field still reads as a plain sentence rather than "[] some message".
	func describe() -> String:
		return message if kind.is_empty() else "[%s] %s" % [kind, message]

	## The thing this issue is about — an addon name, a folder, a path — or "" when the message does
	## not open with one.
	##
	## Every doctor message opens with its subject in single quotes, but that is a convention of the
	## CLI's prose and not a field of the payload, so it is read conservatively: only a message that
	## *starts* with a quoted run counts, and anything else reports "" and gets shown as a whole
	## sentence instead. Nothing is hidden either way — [member message] stays the full text.
	func subject() -> String:
		if not message.begins_with("'"):
			return ""

		var closing := message.find("'", 1)
		return "" if closing <= 1 else message.substr(1, closing - 1)


var healthy: bool = false
var issues: Array = []   ## Array[Issue]


static func from_payload(payload: Dictionary) -> DotquayDoctor:
	var doctor := DotquayDoctor.new()
	doctor.healthy = bool(payload.get("healthy", false))

	for entry in DotquayWire.array_or_empty(payload.get("issues")):
		if typeof(entry) == TYPE_DICTIONARY:
			doctor.issues.append(Issue.from_payload(entry))

	return doctor


## Plain-language wording for each issue kind: a noun phrase to count, and one line of explanation.
##
## Keyed by the CLI's own wire value (`JsonMapper.ToWire(DoctorIssueKind)`), which is a documented wire
## contract rather than an internal name, so an entry here stays valid as long as that value does.
##
## Wording only. A kind missing from this table still renders as its own group with its own count and
## its messages listed verbatim ([constant KIND_UNKNOWN]) — which is what a dotquay newer than this plugin
## gets: a finding shown plainly, never a finding dropped for not being recognised.
const KIND_WORDING := {
	"git_not_on_path": {
		"one": "git installation problem",
		"many": "git installation problems",
		"detail": "Every addon comes from a git repository, so nothing installs or updates until git is on your PATH.",
	},
	"dangling_lockfile_entry": {
		"one": "locked entry dotquay.toml no longer declares",
		"many": "locked entries dotquay.toml no longer declares",
		"detail": "Usually a hand-edit to dotquay.toml that skipped `dotquay remove`. Running an update rewrites the lockfile from the manifest.",
	},
	"plugin_cfg_version_drift": {
		"one": "addon whose plugin.cfg disagrees with the lockfile",
		"many": "addons whose plugin.cfg disagrees with the lockfile",
		"detail": "The folder's contents and the locked version have drifted apart, usually through an edit made inside addons/.",
	},
	"orphaned_addon_folder": {
		"one": "folder in addons/ that dotquay doesn't track",
		"many": "folders in addons/ that dotquay doesn't track",
		"detail": "A leftover from a manual copy, or a rename that didn't clean up the old folder. dotquay never deletes these by itself.",
	},
	"unreadable_package_manifest": {
		"one": "addon shipping a dotquay.toml dotquay can't read",
		"many": "addons shipping a dotquay.toml dotquay can't read",
		"detail": "Usually a missing [package] header. The addon installs fine, but every dependency it declares is silently ignored.",
	},
	"credential_file_permissions": {
		"one": "credential file readable by more than you",
		"many": "credential files readable by more than you",
		"detail": "It holds tokens in plain text. Tighten the file back to owner-only.",
	},
	"secret_in_manifest": {
		"one": "token or password sitting in dotquay.toml",
		"many": "tokens or passwords sitting in dotquay.toml",
		"detail": "dotquay.toml is committed, so this is already in your git history. Move it to `dotquay login` and rotate it.",
	},
	"trusted_host_unusable": {
		"one": "trusted_hosts entry that can't match anything",
		"many": "trusted_hosts entries that can't match anything",
		"detail": "A wildcard, a typo, a host carrying a path, or plain HTTP. It fails silently: feeds are never adopted and you keep being asked to approve them.",
	},
	"fragile_uid_reference": {
		"one": "bare uid:// load() that an update could break",
		"many": "bare uid:// load() calls that an update could break",
		"detail": "A scene stores a path next to its uid and repairs itself; a uid:// string handed to load() has no such fallback. Load it by its res:// path instead.",
	},
	"addon_folder_case_mismatch": {
		"one": "addon installed under the wrong folder casing",
		"many": "addons installed under the wrong folder casing",
		"detail": "It works on Windows and fails on Linux, and Godot re-saves scenes to correct the casing, which leaves the addon reading as permanently modified. Each message names the install_as line that fixes it.",
	},
	"package_asset_metadata_drift": {
		"one": "package whose .csproj lost its declared asset settings",
		"many": "packages whose .csproj lost their declared asset settings",
		"detail": "IncludeAssets/ExcludeAssets/PrivateAssets in the manifest no longer match the project file. An install rewrites it from the lockfile.",
	},
	"install_folder_drift": {
		"one": "addon sitting somewhere other than where its manifest asks",
		"many": "addons sitting somewhere other than where their manifest asks",
		"detail": "An install replays the lockfile and never re-reads the manifest, so an install_as that changed since only takes effect on the next update.",
	},
	"linked_project_missing": {
		"one": "local link pointing at a .csproj that is gone",
		"many": "local links pointing at a .csproj that is gone",
		"detail": "The build fails with an MSBuild error naming the missing project rather than anything mentioning dotquay. The linked checkout was probably moved or deleted.",
	},
	"addon_link_mismatch": {
		"one": "linked folder that disagrees with dotquay.local.toml",
		"many": "linked folders that disagree with dotquay.local.toml",
		"detail": "The junction on disk and the declaration in the local file are two separate statements, and they have drifted. dotquay refuses to install into either until they agree.",
	},
	"local_overlay_not_imported": {
		"one": "local overlay the .csproj never imports",
		"many": "local overlays the .csproj never import",
		"detail": "The link silently does nothing and the build keeps using the published package. The <Import> line was most likely lost in a merge.",
	},
	"ssh_host_not_trusted": {
		"one": "ssh host this machine has no trusted key for",
		"many": "ssh hosts this machine has no trusted key for",
		"detail": "The fetch fails deep inside git with \"Host key verification failed\", which reads like a credentials problem and is not one. Connect to the host once by hand to record its key.",
	},
	"package_also_built_from_source": {
		"one": "package referenced twice: from source and from NuGet",
		"many": "packages referenced twice: from source and from NuGet",
		"detail": "The .csproj builds it from a ProjectReference and also pulls the published package, so the build may compile against the package instead of your source. An install removes the PackageReference, unless the source reference has a condition.",
	},
	"source_build_version_mismatch": {
		"one": "package built from source at a different version than locked",
		"many": "packages built from source at a different version than locked",
		"detail": "This project compiles the source version, but anyone installing its addons gets the locked one from the feed. Publish the source version, or align its <Version> with the lockfile.",
	},
	"nuget_source_mapping_drift": {
		"one": "package NuGet.Config doesn't bind to its locked feed",
		"many": "packages NuGet.Config doesn't bind to their locked feed",
		"detail": "A package matching no pattern falls through to the * catch-all and is fetched from nuget.org instead. An install rewrites the file from the lockfile.",
	},
	"addon_folder_is_godot_project": {
		"one": "addon folder that is a whole Godot project rather than an addon",
		"many": "addon folders that are whole Godot projects rather than addons",
		"detail": "The manifest declaring it points its path at the repository root. Godot reports the plugin as missing, which points at the editor rather than at the real cause.",
	},
	"lfs_pointer_file": {
		"one": "addon holding Git LFS placeholders instead of its real files",
		"many": "addons holding Git LFS placeholders instead of their real files",
		"detail": "It was fetched on a machine without git-lfs, so git left small text files where the binaries belong. dotquay calls it clean because the lockfile hash was taken over the placeholders. Install git-lfs, run `git lfs install`, delete the addon folder and run `dotquay install`.",
	},
}

## The fallback for a kind this plugin has no wording for. Counts the issues and says nothing else,
## leaving the CLI's own sentences to carry the meaning.
const KIND_UNKNOWN := {"one": "issue", "many": "issues", "detail": ""}


## One kind's worth of issues, collapsed into a single heading.
##
## The repetition is the problem this exists to solve: thirty-eight orphaned folders are thirty-eight
## copies of one long sentence, and printing every one buries whatever else the log had to say. Said
## once here, under a heading carrying the count and the CLI's `kind`, with the individual subjects
## listed separately.
class Group extends RefCounted:
	var kind: String = ""
	var issues: Array = []   ## Array[Issue]

	func count() -> int:
		return issues.size()

	func _wording() -> Dictionary:
		return DotquayDoctor.KIND_WORDING.get(kind, DotquayDoctor.KIND_UNKNOWN)

	## The heading: how many, and of what, in words rather than in a wire value.
	func headline() -> String:
		var wording := _wording()
		return DotquayText.plural(count(), "%d " + wording["one"], "%d " + wording["many"])

	## The one-line explanation shared by every issue in this group, or "" when this plugin has no
	## wording for the kind and the messages have to speak for themselves.
	func detail() -> String:
		return String(_wording()["detail"])

	## True when every issue in the group named its subject, so the group can be shown as a list of
	## names instead of a stack of near-identical sentences.
	func has_subjects() -> bool:
		for issue in issues:
			if issue.subject().is_empty():
				return false

		return not issues.is_empty()

	func subjects() -> PackedStringArray:
		var names := PackedStringArray()
		for issue in issues:
			names.append(issue.subject())

		return names

	func messages() -> PackedStringArray:
		var lines := PackedStringArray()
		for issue in issues:
			lines.append(issue.message)

		return lines

	## True when every issue in the group carries a full [method Issue.has_uid_fix] -- the CLI has
	## already resolved exactly what to change and to what, for every one of them.
	func has_uid_fixes() -> bool:
		for issue in issues:
			if not issue.has_uid_fix():
				return false

		return not issues.is_empty()

	## One compact line per issue in place of the repeated sentence: see [method Issue.uid_fix_line].
	func fix_lines() -> PackedStringArray:
		var lines := PackedStringArray()
		for issue in issues:
			lines.append(issue.uid_fix_line())

		return lines

	## The whole group's fixes as one block of text, ready to paste into an issue tracker or a chat
	## with whoever owns the referencing script -- one line per occurrence, ile | uid -> res_path.
	func copy_text() -> String:
		return "\n".join(fix_lines())


## The issues collapsed by kind, in the order the CLI first reported each one.
##
## The CLI's order, deliberately, rather than alphabetical or biggest-first: `doctor` runs its checks
## in an order that means something (git on PATH before anything that needs git), and re-sorting here
## would throw that away for a cosmetic gain.
func groups() -> Array:
	var by_kind := {}
	var ordered: Array = []

	for issue in issues:
		if not by_kind.has(issue.kind):
			var group := Group.new()
			group.kind = issue.kind
			by_kind[issue.kind] = group
			ordered.append(group)

		by_kind[issue.kind].issues.append(issue)

	return ordered


## One line for the panel log and the dialog's own header.
func summarize() -> String:
	if healthy:
		return "Doctor found no issues."

	return "Doctor found %s in %s." % [
		DotquayText.plural(issues.size(), "%d issue", "%d issues"),
		DotquayText.plural(groups().size(), "%d category", "%d categories")]
