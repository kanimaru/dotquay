extends RefCounted

## Covers [DotquaySelfCheck] — the typed view over `self-check --json` (C-24). Previously read ad hoc
## inside `dotquay_panel.gd`'s `_report_self_check`, with no fixture test of its own, unlike
## graph/preview/explain.

const SELF_CHECK := preload("res://addons/dotquay/core/dotquay_self_check.gd")

var runner: Object


func test_parses_the_envelope_payload() -> void:
	var check: DotquaySelfCheck = SELF_CHECK.from_payload({
		"current_version": "0.16.2", "latest_version": "0.17.0", "update_available": true,
	})

	runner.equal(check.current_version, "0.16.2", "current_version is kept")
	runner.equal(check.latest_version, "0.17.0", "latest_version is kept")
	runner.check(check.update_available, "the flag is kept")
	runner.check(check.has_available_update(), "a real newer version with the flag set is available")
	runner.equal(check.describe_update(), "dotquay 0.17.0 is available — you have 0.16.2.",
		"the banner sentence names both versions")


func test_no_update_available_reports_as_such() -> void:
	var check: DotquaySelfCheck = SELF_CHECK.from_payload({
		"current_version": "0.17.0", "latest_version": "0.17.0", "update_available": false,
	})

	runner.check(not check.has_available_update(), "an up-to-date check has no available update")


func test_update_available_with_no_latest_version_is_not_treated_as_available() -> void:
	# A flag with nothing to show would be a banner with no version in it — has_available_update()
	# requires both, not just the flag.
	var check: DotquaySelfCheck = SELF_CHECK.from_payload({
		"current_version": "0.17.0", "latest_version": "", "update_available": true,
	})

	runner.check(not check.has_available_update(), "no latest_version means nothing to announce")


func test_null_optional_fields_parse_as_empty_not_the_literal_null() -> void:
	var check: DotquaySelfCheck = SELF_CHECK.from_payload({
		"current_version": null, "latest_version": null,
	})

	runner.equal(check.current_version, "", "a null current_version parses as empty, not '<null>'")
	runner.equal(check.latest_version, "", "a null latest_version parses as empty, not '<null>'")
	runner.check(not check.has_available_update(), "a null latest_version means no update to announce")


func test_missing_fields_do_not_break_the_parse() -> void:
	var check: DotquaySelfCheck = SELF_CHECK.from_payload({})

	runner.equal(check.current_version, "", "a missing current_version parses as empty")
	runner.equal(check.latest_version, "", "a missing latest_version parses as empty")
	runner.check(not check.update_available, "a missing flag defaults to no update, the safe direction")
