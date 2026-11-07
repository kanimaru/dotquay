extends RefCounted

## Covers [DotquayWire]'s display helpers — the one place a wire literal is translated into words a
## person reads, so a translation applied in only one display site (a menu, a detail row, a log line)
## while another still prints the raw literal is a bug this suite exists to catch by pinning the rule
## itself rather than any one caller of it.

const WIRE := preload("res://addons/dotquay/core/dotquay_wire.gd")

var runner: Object


func test_rev_displays_as_commit() -> void:
	runner.equal(WIRE.display_source_kind(WIRE.SOURCE_REV), "commit",
		"'rev' is git's own jargon; a person reading a pin kind thinks in commits")


func test_version_and_branch_pass_through_unchanged() -> void:
	runner.equal(WIRE.display_source_kind(WIRE.SOURCE_VERSION), "version",
		"already a plain word — nothing to translate")
	runner.equal(WIRE.display_source_kind(WIRE.SOURCE_BRANCH), "branch",
		"already a plain word — nothing to translate")


func test_an_unrecognised_kind_passes_through_rather_than_disappearing() -> void:
	# A CLI-side kind this plugin doesn't know about yet should still show as *something* rather than
	# reading as empty — that's a schema mismatch DotquayResult's own checks are responsible for
	# catching, not something this display helper should paper over by returning "".
	runner.equal(WIRE.display_source_kind("unknown-future-kind"), "unknown-future-kind",
		"an unrecognised kind is shown as-is rather than swallowed")


## [method DotquayWire.cli_command_words] is what lets [method DotquayPanel._run]'s callers stop
## repeating a command name as both the label and a literal array element (C-20) — most wire names
## reduce to their own single-word command line, so this pins the three that don't.
func test_cli_command_words_reduces_most_commands_to_their_own_name() -> void:
	runner.equal(WIRE.cli_command_words(WIRE.COMMAND_INSTALL), PackedStringArray(["install"]),
		"most wire command names are their own CLI subcommand word")
	runner.equal(WIRE.cli_command_words(WIRE.COMMAND_GRAPH), PackedStringArray(["graph"]),
		"most wire command names are their own CLI subcommand word")


func test_cli_command_words_splits_the_self_commands_into_two_words() -> void:
	runner.equal(WIRE.cli_command_words(WIRE.COMMAND_SELF_CHECK), PackedStringArray(["self", "check"]),
		"'self-check' is one hyphenated wire name but two words on the command line")
	runner.equal(WIRE.cli_command_words(WIRE.COMMAND_SELF_UPDATE), PackedStringArray(["self", "update"]),
		"'self-update' is one hyphenated wire name but two words on the command line")


func test_cli_command_words_resolves_add_preview_to_the_real_add_subcommand() -> void:
	runner.equal(WIRE.cli_command_words(WIRE.COMMAND_ADD_PREVIEW), PackedStringArray(["add"]),
		"'add-preview' is a wire name for a payload shape, not a real CLI subcommand — the actual "
			+ "invocation is 'add ... --preview'")


## [method DotquayWire.flag_for_source_kind] is what let [DotquayGraph.Candidate]'s own
## `override_flag()` and the panel's hand-rolled "--" + kind both be deleted in favour of one mapping
## (C-10, C-21).
func test_flag_for_source_kind_matches_the_recognised_kinds() -> void:
	runner.equal(WIRE.flag_for_source_kind(WIRE.SOURCE_VERSION), "--version",
		"a version pin maps to --version")
	runner.equal(WIRE.flag_for_source_kind(WIRE.SOURCE_BRANCH), "--branch",
		"a branch pin maps to --branch")
	runner.equal(WIRE.flag_for_source_kind(WIRE.SOURCE_REV), "--rev",
		"a rev pin maps to --rev")


func test_flag_for_source_kind_falls_back_for_an_unrecognised_kind() -> void:
	# Mirrors test_an_unrecognised_kind_passes_through_rather_than_disappearing above: a picker built
	# from a real graph --resolve payload should not break outright over a kind newer than this build.
	runner.equal(WIRE.flag_for_source_kind("some-future-kind"), "--some-future-kind",
		"an unrecognised kind still produces a plausible flag rather than an empty one")


## C-1: the CLI's own error kinds live here now, not on DotquayResult.
func test_kind_constants_are_defined_on_the_wire_not_on_result() -> void:
	runner.equal(WIRE.KIND_WORKFLOW, "workflow", "workflow is a wire-reported error kind")
	runner.equal(WIRE.KIND_NO_MANIFEST, "no_manifest", "no_manifest is a wire-reported error kind")
	runner.equal(WIRE.KIND_CONFLICT, "conflict", "conflict is a wire-reported error kind")
	runner.equal(WIRE.KIND_GIT, "git", "git is a wire-reported error kind")
	runner.equal(WIRE.KIND_AUTH, "auth", "auth is a wire-reported error kind")
	runner.equal(WIRE.KIND_INVALID_PROJECT_FILE, "invalid_project_file",
		"invalid_project_file is a wire-reported error kind")
	runner.equal(WIRE.KIND_UNEXPECTED, "unexpected", "unexpected is a wire-reported error kind")
