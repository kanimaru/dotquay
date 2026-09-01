extends RefCounted

## Covers [DotquayAddRequest] — the add form's arguments, and the rules that keep them to
## combinations `dotquay add` accepts (SPEC.md §6.2).
##
## Worth its own suite because these arguments are built once and used twice: for `add --preview` and
## for the `add` the user confirms. A rule applied in only one of those places is a preview that
## describes a different install from the one that runs.

const REQUEST := preload("res://addons/dotquay/core/dotquay_add_request.gd")

var runner: Object

const URL := "https://github.com/nathanhoad/godot_dialogue_manager"


func _git(mode: int = 0, value: String = "") -> DotquayAddRequest:
	var request: DotquayAddRequest = REQUEST.new()
	request.source = URL
	request.is_git = true
	request.source_mode = mode
	request.source_value = value
	return request


func _joined(request: DotquayAddRequest) -> String:
	return " ".join(request.to_args())


# -- naming a source -------------------------------------------------------------------------

func test_latest_passes_no_source_flag() -> void:
	# The absence of a flag is what makes the CLI apply its newest-release-tag default, so "latest"
	# must add nothing rather than add something meaning the same.
	runner.equal(_joined(_git()), "add " + URL, "latest release passes the URL alone")


func test_each_mode_maps_to_its_own_flag() -> void:
	runner.equal(_joined(_git(REQUEST.Source.VERSION, "~2.5")), "add %s --version ~2.5" % URL,
		"version maps to --version")
	runner.equal(_joined(_git(REQUEST.Source.BRANCH, "main")), "add %s --branch main" % URL,
		"branch maps to --branch")
	runner.equal(_joined(_git(REQUEST.Source.REV, "9f2c1ab")), "add %s --rev 9f2c1ab" % URL,
		"commit maps to --rev")


func test_a_named_mode_with_no_value_falls_back_to_the_default() -> void:
	# Picking "Branch" and typing nothing is an unfinished thought. Emitting a bare --branch would be
	# a parse error; emitting the default is what the user would get anyway if they closed the row.
	runner.equal(_joined(_git(REQUEST.Source.BRANCH, "   ")), "add " + URL,
		"a blank value emits no flag")


func test_values_are_trimmed() -> void:
	runner.equal(_joined(_git(REQUEST.Source.VERSION, "  ~2.5  ")), "add %s --version ~2.5" % URL,
		"a pasted value keeps no surrounding whitespace")


# -- the git-only flags ----------------------------------------------------------------------

func test_git_carries_the_subtree_and_install_folder() -> void:
	var request := _git(REQUEST.Source.BRANCH, "main")
	request.path = "addons/dialogue_manager"
	request.install_as = "dialogue_manager"

	runner.equal(
		_joined(request),
		"add %s --branch main --path addons/dialogue_manager --install-as dialogue_manager" % URL,
		"every named option reaches the command line")


func test_blank_optional_fields_are_omitted() -> void:
	var request := _git()
	request.path = "  "
	request.install_as = ""

	runner.equal(_joined(request), "add " + URL, "untouched optional fields add nothing")


func test_nuget_never_receives_a_git_only_flag() -> void:
	# `add` refuses these with an explanation rather than ignoring them, so sending one would turn a
	# hidden form row into a hard error the user cannot trace back.
	var request: DotquayAddRequest = REQUEST.new()
	request.source = "Newtonsoft.Json"
	request.is_git = false
	request.source_mode = REQUEST.Source.BRANCH
	request.source_value = "main"
	request.path = "somewhere"
	request.install_as = "elsewhere"

	runner.equal(_joined(request), "add Newtonsoft.Json", "a NuGet package gets no git-only flag")


func test_nuget_still_accepts_an_exact_version() -> void:
	# --version means the same thing on both sides, matching the manifest (SPEC.md §6.2).
	var request: DotquayAddRequest = REQUEST.new()
	request.source = "Newtonsoft.Json"
	request.is_git = false
	request.source_mode = REQUEST.Source.VERSION
	request.source_value = "13.0.3"

	runner.equal(_joined(request), "add Newtonsoft.Json --version 13.0.3",
		"version is the one source flag NuGet shares with git")


# -- the #ref shorthand ----------------------------------------------------------------------

func test_an_inline_ref_suppresses_the_explicit_source() -> void:
	# The CLI refuses the pair outright: "already names a ref, so --version/--branch/--rev would
	# contradict it". Emitting both would be a guaranteed failure after the dialog had closed.
	var request := _git(REQUEST.Source.BRANCH, "develop")
	request.source = URL + "#main"

	runner.equal(_joined(request), "add %s#main" % URL, "the URL's ref wins and no flag is added")


func test_git_url_strips_the_ref_so_a_preview_can_be_matched_back() -> void:
	# The CLI reports the URL without the fragment in a preview payload, and the panel keys pending
	# requests by it — a mismatch here loses the user's --path and --install-as on confirmation.
	var request := _git()
	request.source = URL + "#main"

	runner.equal(request.git_url(), URL, "the fragment is removed")
	runner.equal(request.inline_ref(), "main", "the ref is recovered")


func test_a_trailing_hash_is_a_typo_not_a_ref() -> void:
	# Mirrors AddCommand.SplitRef: only a '#' with something after it counts.
	var request := _git()
	request.source = URL + "#"

	runner.check(not request.has_inline_ref(), "a bare trailing # names no ref")
	runner.equal(request.git_url(), URL + "#", "and the URL is left exactly as typed")


func test_the_last_hash_wins() -> void:
	var request := _git()
	request.source = "https://example.com/a#b#v1.0"

	runner.equal(request.inline_ref(), "v1.0", "the ref is taken from the last '#'")


func test_a_url_with_no_ref_is_its_own_key() -> void:
	runner.equal(_git().git_url(), URL, "a plain URL is unchanged")


# -- what the log says -----------------------------------------------------------------------

func test_describe_says_the_same_thing_the_arguments_do() -> void:
	var request := _git(REQUEST.Source.BRANCH, "main")
	request.install_as = "dialogue_manager"

	var described := request.describe()
	runner.check(described.contains("branch main"), "the log line names the pin: %s" % described)
	runner.check(described.contains("addons/dialogue_manager"),
		"the log line names the install folder: %s" % described)


func test_describe_of_a_plain_add_is_just_the_source() -> void:
	runner.equal(_git().describe(), URL, "with nothing named, there is nothing to add in brackets")
