extends RefCounted

## Fixtures for the `status --explain --json` parser (docs/DESIGN-v0.12-MODIFIED-REASON.md).
##
## The invariant worth naming, because every test here is a face of it: an unrecognised value must
## read as *unsafe*. A newer CLI can add a change class or a verdict this build has never seen, and a
## dialog whose OK button says "Force update" is the wrong place to be optimistic about that.

const EXPLAIN := preload("res://addons/dotquay/core/dotquay_explain.gd")

var runner: Object


static func _entry(name: String, status: String, explanation: Variant) -> Dictionary:
	return {"name": name, "status": status, "explanation": explanation}


static func _explanation(verdict: String, counts: Dictionary, changes: Array) -> Dictionary:
	return {
		"verdict": verdict,
		"counts": counts,
		"changes": changes,
		"unavailable_reason": null,
		"baseline_includes_patch": false,
		"patch_could_not_be_applied": false,
	}


static func _counts(content: int, uid_only: int, line_endings: int, artifacts: int) -> Dictionary:
	return {
		"content": content,
		"uid_only": uid_only,
		"line_endings_only": line_endings,
		"editor_artifact": artifacts,
		"total": content + uid_only + line_endings + artifacts,
	}


func test_parses_a_safe_to_force_explanation() -> void:
	var status: DotquayExplain = EXPLAIN.from_payload({
		"addons": [_entry("gut", "modified", _explanation(
			DotquayWire.VERDICT_SAFE_TO_FORCE,
			_counts(0, 1, 0, 0),
			[{"path": "gut.tscn", "kind": "changed", "class": "uid_only"}]))],
		"all_clean": false,
	})

	var explanation: DotquayExplain.Explanation = status.explanation_for("gut")

	runner.check(explanation != null, "the entry's explanation should parse")
	runner.check(explanation.is_safe_to_force(), "a safe_to_force verdict should read as safe")
	runner.equal(explanation.counts.total, 1, "the total should come from the payload, not the list length")

	var change: DotquayExplain.Change = explanation.changes[0]
	runner.equal(change.path, "gut.tscn", "the change should carry its path")
	runner.equal(change.marker(), "~", "a changed file is marked with a tilde")
	runner.check(change.is_benign(), "a uid-only rewrite is benign")


func test_a_real_content_change_is_never_benign() -> void:
	var status: DotquayExplain = EXPLAIN.from_payload({
		"addons": [_entry("gut", "modified", _explanation(
			DotquayWire.VERDICT_REVIEW_NEEDED,
			_counts(1, 0, 0, 0),
			[{"path": "my-notes.gd", "kind": "added", "class": "content"}]))],
	})

	var explanation: DotquayExplain.Explanation = status.explanation_for("gut")

	runner.check(not explanation.is_safe_to_force(), "review_needed must not read as safe")
	runner.check(not explanation.changes[0].is_benign(), "a content change is never benign")
	runner.equal(explanation.changes[0].marker(), "+", "an added file is marked with a plus")


## The forward-compatibility rule, from both sides. This is the one that stops a newer CLI from
## turning "I don't know what that is" into an enabled Force update button.
func test_unknown_verdicts_and_classes_read_as_unsafe() -> void:
	var status: DotquayExplain = EXPLAIN.from_payload({
		"addons": [_entry("gut", "modified", _explanation(
			"some_future_verdict",
			_counts(0, 0, 0, 0),
			[{"path": "x.gd", "kind": "mangled", "class": "some_future_class"}]))],
	})

	var explanation: DotquayExplain.Explanation = status.explanation_for("gut")

	runner.check(not explanation.is_safe_to_force(), "an unrecognised verdict must not read as safe")
	runner.check(not explanation.is_unavailable(), "nor should it be mistaken for 'could not compare'")
	runner.check(not explanation.changes[0].is_benign(), "an unrecognised class must not read as benign")
	runner.equal(explanation.changes[0].marker(), "?", "an unrecognised kind gets a marker that admits it")


func test_unavailable_carries_its_reason_and_is_not_safe() -> void:
	var status: DotquayExplain = EXPLAIN.from_payload({
		"addons": [_entry("gut", "modified", {
			"verdict": DotquayWire.VERDICT_UNAVAILABLE,
			"counts": _counts(0, 0, 0, 0),
			"changes": [],
			"unavailable_reason": "Failed to fetch abc123 from https://example.com/gut.git",
			"baseline_includes_patch": false,
			"patch_could_not_be_applied": false,
		})],
	})

	var explanation: DotquayExplain.Explanation = status.explanation_for("gut")

	runner.check(explanation.is_unavailable(), "an unavailable verdict should be recognised as such")
	runner.check(not explanation.is_safe_to_force(), "'could not compare' is not 'safe to force'")
	runner.check(explanation.unavailable_reason.contains("Failed to fetch"), "the reason should survive parsing")
	runner.check(explanation.summarize("gut").contains("offline"),
		"the headline should name the likeliest cause rather than only quoting git")


## A null `unavailable_reason` must not render as the literal "<null>", which is what `String(null)`
## produces — the trap [method DotquayWire.str_or_empty] exists for.
func test_a_null_reason_renders_as_empty() -> void:
	var status: DotquayExplain = EXPLAIN.from_payload({
		"addons": [_entry("gut", "modified", _explanation(
			DotquayWire.VERDICT_SAFE_TO_FORCE, _counts(0, 1, 0, 0), []))],
	})

	runner.equal(status.explanation_for("gut").unavailable_reason, "", "a null reason is empty, not '<null>'")


func test_truncation_is_the_counts_disagreeing_with_the_list() -> void:
	var status: DotquayExplain = EXPLAIN.from_payload({
		"addons": [_entry("gut", "modified", _explanation(
			DotquayWire.VERDICT_REVIEW_NEEDED,
			_counts(150, 0, 0, 0),
			[{"path": "a.gd", "kind": "changed", "class": "content"}]))],
	})

	var explanation: DotquayExplain.Explanation = status.explanation_for("gut")

	runner.check(explanation.is_truncated(), "150 counted against 1 listed is a truncated report")
	runner.equal(explanation.hidden_change_count(), 149, "the hidden count is the difference")


func test_a_clean_addon_has_no_explanation() -> void:
	var status: DotquayExplain = EXPLAIN.from_payload({
		"addons": [_entry("gut", "clean", null)],
		"all_clean": true,
	})

	runner.check(status.explanation_for("gut") == null, "a clean addon carries no explanation")
	runner.check(status.explanation_for("nothing-of-that-name") == null, "an absent addon is null, not an error")
	runner.check(status.all_clean, "all_clean should survive parsing")


## `status` without `--explain` is the same payload shape minus the object, and the panel routes both
## through this parser.
func test_an_unexplained_modified_addon_parses_without_an_explanation() -> void:
	var status: DotquayExplain = EXPLAIN.from_payload({"addons": [{"name": "gut", "status": "modified"}]})

	var entry: DotquayExplain.Entry = status.find("gut")
	runner.equal(entry.status, DotquayWire.STATUS_MODIFIED, "the status should still parse")
	runner.check(entry.explanation == null, "a missing explanation is null rather than an empty one")


func test_benign_summary_names_what_it_found() -> void:
	var status: DotquayExplain = EXPLAIN.from_payload({
		"addons": [_entry("gut", "modified", _explanation(
			DotquayWire.VERDICT_SAFE_TO_FORCE, _counts(0, 3, 0, 9), []))],
	})

	var summary: String = status.explanation_for("gut").summarize("gut")

	runner.check(summary.contains("3 uid-only"), "the headline should name the uid rewrites it counted")
	runner.check(summary.contains("9 editor artifact"), "and the artifacts")


## Byte-identical to its locked commit and still reported modified — the case the sync engine handles
## by comparing trees rather than hashes. "0 files differ" alone would read as a bug.
func test_no_differences_at_all_says_so_explicitly() -> void:
	var status: DotquayExplain = EXPLAIN.from_payload({
		"addons": [_entry("gut", "modified", _explanation(
			DotquayWire.VERDICT_SAFE_TO_FORCE, _counts(0, 0, 0, 0), []))],
	})

	runner.check(status.explanation_for("gut").summarize("gut").contains("Nothing in addons/gut differs"),
		"an empty diff should be stated rather than left as a blank list")


func test_null_changes_parses_as_empty_not_a_crash() -> void:
	# SPEC.md §17: optional array fields are emitted as JSON null, never omitted. `payload.get(key,
	# [])` only supplies its default when the key is absent, so a present-but-null "changes" used to
	# raise "Unable to iterate on object of type 'Nil'" instead of parsing.
	var status: DotquayExplain = EXPLAIN.from_payload({
		"addons": [_entry("gut", "modified", {
			"verdict": DotquayWire.VERDICT_SAFE_TO_FORCE,
			"counts": _counts(0, 0, 0, 0),
			"changes": null,
			"unavailable_reason": null,
			"baseline_includes_patch": false,
			"patch_could_not_be_applied": false,
		})],
	})

	var explanation: DotquayExplain.Explanation = status.explanation_for("gut")

	runner.check(explanation != null, "the entry's explanation should still parse")
	runner.equal(explanation.changes.size(), 0, "a null changes array should parse as empty, not raise")


func test_null_addons_parses_as_empty_not_a_crash() -> void:
	var status: DotquayExplain = EXPLAIN.from_payload({"addons": null, "all_clean": true})

	runner.equal(status.addons.size(), 0, "a null addons array should parse as empty, not raise")


func test_patch_reconstruction_failure_is_distinct_from_having_no_patch() -> void:
	var without_patch: DotquayExplain = EXPLAIN.from_payload({
		"addons": [_entry("gut", "modified", _explanation(
			DotquayWire.VERDICT_REVIEW_NEEDED, _counts(1, 0, 0, 0), []))],
	})

	runner.check(not without_patch.explanation_for("gut").patch_could_not_be_applied,
		"an ordinary unpatched addon must not claim its patch failed")

	var failed: DotquayExplain = EXPLAIN.from_payload({
		"addons": [_entry("gut", "modified", {
			"verdict": DotquayWire.VERDICT_REVIEW_NEEDED,
			"counts": _counts(1, 0, 0, 0),
			"changes": [],
			"baseline_includes_patch": false,
			"patch_could_not_be_applied": true,
		})],
	})

	runner.check(failed.explanation_for("gut").patch_could_not_be_applied,
		"a declared patch that could not be reconstructed should be reported")
