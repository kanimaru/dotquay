@tool
class_name DotquayAddRequest
extends RefCounted

## Everything the add form collected, and the one place it becomes CLI arguments.
##
## A request object rather than six signal parameters, because the same arguments are needed twice:
## once for `add --preview`, and again for the `add` the user confirms. Building them in two places is
## how a preview ends up describing an install that differs from the one that runs — the exact failure
## the preview exists to prevent.
##
## The rules encoded here mirror the CLI's own (SPEC.md §6.2), so the form can only produce
## combinations `add` accepts:
##
## - Exactly one of `--version` / `--branch` / `--rev`, or none for "latest release".
## - `--branch`, `--rev`, `--path`, and `--install-as` are git-only; `--version` works for both.
## - A `#ref` in the URL *is* the source, so it can't be combined with an explicit one.

## Which source the user named. [constant Source.LATEST] means "don't pass a source flag at all",
## which is what makes the CLI apply its newest-release-tag default.
enum Source { LATEST, VERSION, BRANCH, REV }

var source: String = ""
var is_git: bool = true
var source_mode: Source = Source.LATEST
var source_value: String = ""

## The subtree to extract (`--path`), when the addon isn't at `addons/<repo name>`.
var path: String = ""

## The folder under `addons/` to install into (`--install-as`), when it shouldn't be the addon's own
## name.
var install_as: String = ""


## What [method diagnose_source_shape] found in a pasted string that isn't a plain clone URL.
##
## A user reliably pastes three things into the source field that aren't `git clone` addresses: a
## GitHub/GitLab "tree" browser URL pointing at one folder on one branch, the bare `owner/repo`
## shorthand GitHub prints next to its own "Code" button, and — this one's actually fine — a URL with
## an explicit `.git` suffix. Diagnosing the shape once, as data instead of UI logic, is what lets the
## dialog both auto-correct the first case and explain the second without ever handing `add` either.
class ShapeFix extends RefCounted:
	## The corrected git URL to write into the source field. Empty means nothing needed rewriting.
	var url: String = ""
	## The branch parsed out of a tree URL. Empty when there wasn't one to parse.
	var branch: String = ""
	## The subtree parsed out of a tree URL. Empty when there wasn't one, or the link named no folder.
	var path: String = ""
	## One line for the form's hint label explaining what was found. Empty means there's nothing to
	## say beyond the ordinary hint text.
	var explanation: String = ""
	## True when the typed text can't become a request as-is and the dialog should keep OK disabled
	## over it — the bare `owner/repo` shorthand, which names no host to clone from. Rewriting one
	## host onto it would be a guess the form has no basis for; the fix here is telling the user what
	## to add, not guessing it for them.
	var blocks_submit: bool = false

	## True when [member url] has a replacement worth writing back into the field.
	func has_fix() -> bool:
		return not url.is_empty()


## Matches a GitHub or GitLab "tree" browser URL — the address bar's contents when someone is looking
## at one folder of a repo on one branch, e.g. `.../owner/repo/tree/main/addons/foo` (GitHub) or
## `.../owner/repo/-/tree/main/addons/foo` (GitLab's `-/` namespace). `repo` is matched lazily so a
## `.git` suffix on the repo segment itself is stripped rather than swallowed into the name.
##
## Compiled on each call rather than cached in a static var: this runs at most once per keystroke in
## one dialog, so there is nothing to gain from caching that's worth a static regex's initialization
## order depending on when the engine first loads this script.
static func _tree_url_regex() -> RegEx:
	return RegEx.create_from_string(
		"^(?<repo>https?://(?:www\\.)?(?:github\\.com|gitlab\\.com)/[^/]+/[^/]+?)(?:\\.git)?/(?:-/)?"
		+ "tree/(?<branch>[^/]+)(?:/(?<path>.+?))?/?$")


## Matches the bare `owner/repo` shorthand: exactly one slash, no scheme, no `.git`. Anything reaching
## this check has already failed [method looks_like_git], so a NuGet ID (which never contains a `/`)
## can't land here by accident.
static func _bare_owner_repo_regex() -> RegEx:
	return RegEx.create_from_string("^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$")


## Inspects [param text] for the three pasted-URL shapes described on [ShapeFix], so the dialog can
## auto-correct a tree URL, explain a `.git` suffix is fine, or block submission on a bare shorthand
## with a reason instead of leaving the user to discover any of it from a hard error out of `add`.
static func diagnose_source_shape(text: String) -> ShapeFix:
	var fix := ShapeFix.new()
	var trimmed := text.strip_edges()

	if trimmed.is_empty():
		return fix

	var tree_match := _tree_url_regex().search(trimmed)
	if tree_match:
		fix.url = tree_match.get_string("repo")
		fix.branch = tree_match.get_string("branch")
		fix.path = tree_match.get_string("path")

		var where := "branch '%s'" % fix.branch
		if not fix.path.is_empty():
			where += ", folder '%s'" % fix.path

		fix.explanation = (
			"That's a link into the repository's file browser, not the repository itself. Using the "
			+ "repository URL instead, pinned to %s." % where)
		return fix

	if looks_like_git(trimmed):
		if trimmed.ends_with(".git"):
			fix.explanation = "Ends in .git — that's a valid clone URL as-is."
		return fix

	if _bare_owner_repo_regex().search(trimmed):
		fix.blocks_submit = true
		fix.explanation = (
			"'%s' looks like a GitHub/GitLab shorthand ('owner/repo'), not a full URL. dotquay needs "
			+ "the whole address, e.g. https://github.com/%s") % [trimmed, trimmed]
		return fix

	return fix


## `add <package-id> --version <version>`: what the package version dialog runs to pin or change a
## NuGet package (DESIGN-v0.19 §7a.1). Built here rather than by hand at the call site, so it goes
## through the same [method to_args] as the Add form.
static func for_nuget_version(package_id: String, version: String) -> DotquayAddRequest:
	var request := DotquayAddRequest.new()
	request.source = package_id
	request.is_git = false
	request.source_mode = Source.VERSION
	request.source_value = version
	return request


static func for_git(git_url: String) -> DotquayAddRequest:
	var request := DotquayAddRequest.new()
	request.source = git_url
	request.is_git = true
	return request


## Whether [param text] is shaped like a git source rather than a NuGet package ID, mirroring the
## CLI's own routing-by-shape. Three shapes count: a URL with a scheme (`https://`, `ssh://`,
## `git://`, `file://`), an scp-style remote (`git@github.com:owner/repo`), and anything ending in
## `.git`. A NuGet ID is dotted identifiers with none of those, so a false positive is unlikely; the
## form uses this to flip its kind picker, and a false negative there is a needless hard error from
## `add` rather than a wrong install.
static func looks_like_git(text: String) -> bool:
	var candidate := for_git(text.strip_edges()).git_url()

	if candidate.contains("://"):
		return true

	if candidate.ends_with(".git"):
		return true

	# user@host:path — the `@` before the first `:` and no `://` is the scp form and nothing else.
	var at := candidate.find("@")
	var colon := candidate.find(":")
	return at > 0 and colon > at


## True when the form's fields describe a runnable `add`: a source, and — when a pin mode is
## selected — something to pin to, unless the URL already names the ref itself. The dialog disables
## its OK button on the inverse, so "Add" can never close the form having done nothing.
func is_complete() -> bool:
	if source.strip_edges().is_empty():
		return false

	if source_mode == Source.LATEST:
		return true

	return names_a_source() or has_inline_ref()


## The URL with any `#ref` fragment removed — the form the CLI echoes back in a preview payload
## (C-14). Not a lookup key itself: [DotquayPanel] keys its own pending-request tracking on the CLI
## call's ticket, not on this URL, because several previews of the same URL can be in flight at once
## and only the ticket names exactly which call a given result answers.
func git_url() -> String:
	var index := _ref_index()
	return source if index < 0 else source.substr(0, index)


## The ref named inline as `<url>#main`, or empty when there isn't one.
func inline_ref() -> String:
	var index := _ref_index()
	return "" if index < 0 else source.substr(index + 1)


## True when the source is fully specified by the URL itself, so the mode picker has nothing to add.
func has_inline_ref() -> bool:
	return not inline_ref().is_empty()


## Whether this request actually names a source, as opposed to leaving the CLI's default in place.
func names_a_source() -> bool:
	return source_mode != Source.LATEST and not source_value.strip_edges().is_empty()


## The complete argument list, `add` included, ready for [method DotquayCli.run].
func to_args() -> PackedStringArray:
	var args := PackedStringArray([DotquayWire.COMMAND_ADD, source])

	# An inline ref and an explicit flag are the same statement made twice, and the CLI rejects the
	# pair outright rather than picking one. The URL wins here because the user typed it into the
	# field that also decides which repository is being added.
	if names_a_source() and not has_inline_ref():
		var flag := _source_flag()
		if not flag.is_empty():
			args.append(flag)
			args.append(source_value.strip_edges())

	# Git-only, and not merely ignored for NuGet: `add` refuses them with an explanation rather than
	# accepting them silently, so sending them would turn a hidden field into a hard error.
	if is_git:
		if not path.strip_edges().is_empty():
			args.append(DotquayWire.FLAG_PATH)
			args.append(path.strip_edges())

		if not install_as.strip_edges().is_empty():
			args.append(DotquayWire.FLAG_INSTALL_AS)
			args.append(install_as.strip_edges())

	return args


## A one-line description of what will be added, for the log — so the line the panel prints says the
## same thing the arguments do.
func describe() -> String:
	var parts := PackedStringArray()

	if has_inline_ref():
		parts.append("at %s" % inline_ref())
	elif names_a_source():
		parts.append("%s %s" % [_source_noun(), source_value.strip_edges()])

	if is_git and not install_as.strip_edges().is_empty():
		parts.append("into addons/%s" % install_as.strip_edges())

	return source if parts.is_empty() else "%s (%s)" % [source, ", ".join(parts)]


func _source_flag() -> String:
	match source_mode:
		Source.VERSION:
			return DotquayWire.FLAG_VERSION
		# Both are meaningless for a NuGet package, which has no branches or commits. Guarded here as
		# well as in the form, so a request built any other way still can't produce a rejected command.
		Source.BRANCH:
			return DotquayWire.FLAG_BRANCH if is_git else ""
		Source.REV:
			return DotquayWire.FLAG_REV if is_git else ""
		_:
			return ""


func _source_noun() -> String:
	match source_mode:
		Source.VERSION:
			return "version"
		Source.BRANCH:
			return "branch"
		Source.REV:
			return "commit"
		_:
			return ""


## Mirrors `AddCommand.SplitRef` on the C# side: the *last* `#`, and only when something follows it.
## A fragment has no meaning in a git URL so this can't collide with a real one, but a trailing bare
## `#` is a typo rather than a request for an empty ref.
func _ref_index() -> int:
	var index := source.rfind("#")
	return index if index > 0 and index < source.length() - 1 else -1
