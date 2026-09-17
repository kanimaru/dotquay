extends RefCounted

## Covers [DotquayDoctor] — the typed view over `doctor --json` (C-24). Previously `issue.kind` was
## parsed off the payload and then dropped on the floor entirely; this locks in that it is now read
## and surfaced.

const DOCTOR := preload("res://addons/dotquay/core/dotquay_doctor.gd")

var runner: Object


func _payload() -> Dictionary:
	return {
		"healthy": false,
		"issues": [
			{"kind": "addon_folder_case_mismatch", "message": "addons/Gut should be addons/gut"},
			{"kind": "fragile_uid_reference", "message": "res/scripts/foo.gd:12 loads a bare uid://..."},
		],
	}


func test_parses_the_envelope_payload() -> void:
	var doctor: DotquayDoctor = DOCTOR.from_payload(_payload())

	runner.check(not doctor.healthy, "an unhealthy project is reported as such")
	runner.equal(doctor.issues.size(), 2, "every issue is kept")
	runner.equal(doctor.issues[0].kind, "addon_folder_case_mismatch", "the kind is read, not dropped")


func test_healthy_project_has_no_issues() -> void:
	var doctor: DotquayDoctor = DOCTOR.from_payload({"healthy": true, "issues": []})

	runner.check(doctor.healthy, "a healthy project reports healthy")
	runner.check(doctor.issues.is_empty(), "a healthy project lists no issues")


func test_describe_leads_with_the_kind_when_present() -> void:
	var issue := DotquayDoctor.Issue.from_payload({
		"kind": "install_folder_drift", "message": "'core' installs to addons/core, manifest asks for addons/Core",
	})

	runner.equal(issue.describe(),
		"[install_folder_drift] 'core' installs to addons/core, manifest asks for addons/Core",
		"the kind is prefixed onto the CLI's own message (C-24)")


func test_describe_falls_back_to_the_plain_message_when_kind_is_absent() -> void:
	# An older dotquay that predates the kind field must not read as "[] some message".
	var issue := DotquayDoctor.Issue.from_payload({"message": "git is not on PATH"})

	runner.equal(issue.describe(), "git is not on PATH",
		"no kind means no bracketed prefix, just the message")


func test_null_optional_fields_parse_as_empty_not_a_crash() -> void:
	var doctor: DotquayDoctor = DOCTOR.from_payload({"issues": null})
	runner.equal(doctor.issues.size(), 0, "a null issues array should parse as empty, not raise")

	var issue := DotquayDoctor.Issue.from_payload({"kind": null, "message": null})
	runner.equal(issue.kind, "", "a null kind parses as empty, not '<null>'")
	runner.equal(issue.message, "", "a null message parses as empty, not '<null>'")


func test_missing_fields_do_not_break_the_parse() -> void:
	var doctor: DotquayDoctor = DOCTOR.from_payload({})

	runner.check(not doctor.healthy, "a missing healthy flag defaults to unhealthy, the safe direction")
	runner.equal(doctor.issues.size(), 0, "missing issues parse as empty")
