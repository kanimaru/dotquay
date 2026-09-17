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


func test_git_url_strips_the_ref_so_it_matches_what_the_cli_echoes_back() -> void:
	# The CLI reports the URL without the fragment in a preview payload (C-14) — this is what a
	# caller compares that payload's own "git" field against, not a key anything is stored under
	# (the panel keys its pending requests on the CLI call's ticket instead).
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


func test_a_url_with_no_ref_is_unchanged() -> void:
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


# -- is the form complete? -------------------------------------------------------------------

func test_a_blank_source_is_incomplete() -> void:
	var request: DotquayAddRequest = REQUEST.new()
	request.source = "   "
	runner.check(not request.is_complete(), "nothing to add means nothing to run")


func test_latest_needs_only_a_source() -> void:
	runner.check(_git().is_complete(), "latest release needs no pin value")


func test_a_pin_mode_with_no_value_is_incomplete() -> void:
	runner.check(not _git(REQUEST.Source.VERSION, "").is_complete(),
		"a chosen pin mode with an empty field is an unfinished form, not a request")
	runner.check(not _git(REQUEST.Source.BRANCH, "  ").is_complete(), "whitespace is still empty")


func test_a_pin_mode_with_a_value_is_complete() -> void:
	runner.check(_git(REQUEST.Source.REV, "abc123").is_complete(), "a pin with a value can run")


func test_an_inline_ref_completes_a_pin_mode_on_its_own() -> void:
	var request := _git(REQUEST.Source.BRANCH, "")
	request.source = URL + "#main"
	runner.check(request.is_complete(), "the URL names the ref, so the empty field is not a gap")


# -- does it look like git? ------------------------------------------------------------------

func test_scheme_urls_look_like_git() -> void:
	for source in [URL, "ssh://git@host/repo", "git://host/repo", "file:///c/repos/x", URL + "#main"]:
		runner.check(REQUEST.looks_like_git(source), "%s should be recognised as git" % source)


func test_scp_style_remotes_look_like_git() -> void:
	runner.check(REQUEST.looks_like_git("git@github.com:owner/repo.git"), "scp form with .git")
	runner.check(REQUEST.looks_like_git("git@gitlab.kemomi.de:group/sub/repo"), "scp form without .git")


func test_a_dot_git_suffix_looks_like_git() -> void:
	runner.check(REQUEST.looks_like_git("C:/repos/addon.git"), "a bare .git path is a git source")


func test_nuget_ids_do_not_look_like_git() -> void:
	for source in ["Newtonsoft.Json", "Microsoft.Extensions.Logging", "System.Text.Json", "VBeami.Core", ""]:
		runner.check(not REQUEST.looks_like_git(source), "%s is a package ID, not a repository" % source)


# -- input-shape diagnosis (P1-8) --------------------------------------------------------------

func test_add_form_parses_github_tree_urls_into_branch_and_subtree() -> void:
	var fix := REQUEST.diagnose_source_shape(
		"https://github.com/nathanhoad/godot_dialogue_manager/tree/main/addons/dialogue_manager")

	runner.check(fix.has_fix(), "a tree URL has a corrected repository URL")
	runner.equal(fix.url, URL, "the /tree/... suffix is stripped back to the repository URL")
	runner.equal(fix.branch, "main", "the branch is recovered")
	runner.equal(fix.path, "addons/dialogue_manager", "the subtree is recovered")
	runner.check(not fix.explanation.is_empty(), "the fix explains what it did")
	runner.check(not fix.blocks_submit, "a tree URL is corrected, not rejected")


func test_a_gitlab_tree_url_handles_the_dash_segment() -> void:
	# GitLab's own namespace puts a "-/" in front of "tree/"; GitHub has no equivalent.
	var fix := REQUEST.diagnose_source_shape("https://gitlab.com/o/r/-/tree/develop/subdir")

	runner.equal(fix.url, "https://gitlab.com/o/r", "the -/ segment is stripped along with tree/")
	runner.equal(fix.branch, "develop", "the branch is recovered")
	runner.equal(fix.path, "subdir", "the subtree is recovered")


func test_a_tree_url_with_no_subtree_still_recovers_the_branch() -> void:
	var fix := REQUEST.diagnose_source_shape("https://github.com/o/r/tree/main")

	runner.equal(fix.url, "https://github.com/o/r", "the repository URL is recovered")
	runner.equal(fix.branch, "main", "the branch is recovered")
	runner.equal(fix.path, "", "a link to a branch root names no subtree")


func test_a_plain_git_url_needs_no_fix() -> void:
	var fix := REQUEST.diagnose_source_shape(URL)

	runner.check(not fix.has_fix(), "an ordinary clone URL is left alone")
	runner.check(not fix.blocks_submit, "an ordinary clone URL never blocks submission")
	runner.equal(fix.explanation, "", "there is nothing to say about a plain URL")


func test_a_dot_git_suffix_is_acknowledged_but_not_rewritten() -> void:
	# P1-8: ".git suffix (fine, say so)" — the URL is already correct, so nothing is rewritten, but
	# the hint should say the suffix isn't a problem rather than staying silent about it.
	var fix := REQUEST.diagnose_source_shape(URL + ".git")

	runner.check(not fix.has_fix(), "a .git suffix needs no correction")
	runner.check(not fix.explanation.is_empty(), "the form acknowledges the suffix is fine")


func test_add_form_rejects_bare_owner_repo_with_a_hint() -> void:
	var fix := REQUEST.diagnose_source_shape("nathanhoad/godot_dialogue_manager")

	runner.check(fix.blocks_submit, "a bare owner/repo names no host to clone from")
	runner.check(not fix.has_fix(), "there is no URL to guess a host for")
	runner.check(fix.explanation.contains("nathanhoad/godot_dialogue_manager"),
		"the hint repeats what was typed back: %s" % fix.explanation)


func test_a_nuget_package_id_is_never_treated_as_a_bare_shorthand() -> void:
	for source in ["Newtonsoft.Json", "Microsoft.Extensions.Logging", "System.Text.Json"]:
		var fix := REQUEST.diagnose_source_shape(source)
		runner.check(not fix.blocks_submit, "%s has no slash, so it can't be a shorthand" % source)


func test_a_blank_source_is_diagnosed_as_nothing_to_fix() -> void:
	var fix := REQUEST.diagnose_source_shape("   ")

	runner.check(not fix.has_fix(), "nothing typed yet is not a shape problem")
	runner.check(not fix.blocks_submit, "nothing typed yet does not block anything on its own")
