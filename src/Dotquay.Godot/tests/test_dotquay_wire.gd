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
