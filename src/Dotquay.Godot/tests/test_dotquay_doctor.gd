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


func _issue(kind: String, message: String) -> Dictionary:
	return {"kind": kind, "message": message}


func _orphan(folder: String) -> Dictionary:
	return _issue("orphaned_addon_folder",
		"'addons/%s' is on disk but isn't tracked by any locked addon %s a leftover from a manual copy?" % [folder, "—"])


func test_groups_collapse_one_kind_into_one_entry() -> void:
	var doctor: DotquayDoctor = DOCTOR.from_payload({"healthy": false, "issues": [
		_orphan("a"), _orphan("b"), _orphan("c"),
	]})

	var groups: Array = doctor.groups()

	runner.equal(groups.size(), 1, "three issues of one kind are one group")
	runner.equal(groups[0].count(), 3, "and the group keeps all three")
	runner.equal(groups[0].kind, "orphaned_addon_folder", "keyed by the CLI's own kind")


func test_group_order_follows_the_cli_not_the_alphabet() -> void:
	# doctor runs its checks in a deliberate order (git on PATH before anything needing git), so the
	# first kind reported stays first even when a later kind has more issues or sorts earlier.
	var doctor: DotquayDoctor = DOCTOR.from_payload({"healthy": false, "issues": [
		_issue("plugin_cfg_version_drift", "'vbeami': its plugin.cfg reports 0.7.21."),
		_orphan("a"), _orphan("b"),
	]})

	var groups: Array = doctor.groups()

	runner.equal(groups.size(), 2, "two kinds are two groups")
	runner.equal(groups[0].kind, "plugin_cfg_version_drift", "the kind the CLI reported first comes first")
	runner.equal(groups[1].kind, "orphaned_addon_folder", "even though it has fewer issues than the next")


func test_a_group_says_its_explanation_once_and_counts_the_rest() -> void:
	var doctor: DotquayDoctor = DOCTOR.from_payload({"healthy": false, "issues": [
		_orphan("a"), _orphan("b"),
	]})
	var group: Variant = doctor.groups()[0]

	runner.check(group.headline().begins_with("2 folders"),
		"the heading counts them and names what they are, rather than repeating a sentence twice")
	runner.check(not group.detail().is_empty(), "a known kind explains itself once for the whole group")
	runner.equal(group.subjects().size(), 2, "and lists what it applies to")


func test_singular_and_plural_headings_both_read_correctly() -> void:
	var one: DotquayDoctor = DOCTOR.from_payload({"healthy": false, "issues": [_orphan("a")]})
	var many: DotquayDoctor = DOCTOR.from_payload({"healthy": false, "issues": [_orphan("a"), _orphan("b")]})

	runner.check(one.groups()[0].headline().begins_with("1 folder in"),
		"exactly one should not read as '1 folders'")
	runner.check(many.groups()[0].headline().begins_with("2 folders in"), "and two should pluralise")


func test_subjects_are_read_only_from_a_leading_quoted_name() -> void:
	var doctor: DotquayDoctor = DOCTOR.from_payload({"healthy": false, "issues": [
		_orphan("twitcher-ext"),
	]})
	var group: Variant = doctor.groups()[0]

	runner.equal(group.subjects()[0], "addons/twitcher-ext",
		"the quoted name the message opens with is the subject")
	runner.check(group.has_subjects(), "so the group can be shown as a list of names")


func test_a_message_that_does_not_open_with_a_name_falls_back_to_whole_sentences() -> void:
	# Nothing is hidden when the convention does not hold: the group shows the CLI's sentences instead
	# of a list of empty names.
	var doctor: DotquayDoctor = DOCTOR.from_payload({"healthy": false, "issues": [
		_issue("git_not_on_path", "git is not on PATH."),
	]})
	var group: Variant = doctor.groups()[0]

	runner.check(not group.has_subjects(), "a message with no leading quoted name yields no subject")
	runner.equal(group.messages()[0], "git is not on PATH.", "and the full sentence is what gets shown")


func test_an_unknown_kind_still_gets_its_own_group_and_its_messages() -> void:
	# A dotquay newer than this plugin can report a check this wording table has never heard of. It
	# must still be shown, not dropped for not being recognised.
	var doctor: DotquayDoctor = DOCTOR.from_payload({"healthy": false, "issues": [
		_issue("some_future_check", "'thing' went wrong in a way this plugin has no wording for."),
	]})
	var group: Variant = doctor.groups()[0]

	runner.equal(group.kind, "some_future_check", "the unknown kind is preserved verbatim")
	runner.equal(group.headline(), "1 issue", "and is counted, without inventing an explanation for it")
	runner.equal(group.detail(), "", "with no detail line, since none is known")
	runner.equal(group.messages().size(), 1, "the CLI's own message still carries the meaning")


func test_every_wording_entry_pluralises_and_explains() -> void:
	# The table is hand-written, so this is the check that a new entry is complete rather than a
	# half-filled row that renders as an empty heading.
	for kind in DotquayDoctor.KIND_WORDING:
		var wording: Dictionary = DotquayDoctor.KIND_WORDING[kind]
		runner.check(not String(wording["one"]).is_empty(), "%s has a singular noun phrase" % kind)
		runner.check(not String(wording["many"]).is_empty(), "%s has a plural noun phrase" % kind)
		runner.check(not String(wording["detail"]).is_empty(), "%s explains itself" % kind)
		runner.check(String(wording["one"]) != String(wording["many"]) or kind == "git_not_on_path",
			"%s should read differently in singular and plural" % kind)


func test_summarize_counts_issues_and_categories() -> void:
	var doctor: DotquayDoctor = DOCTOR.from_payload({"healthy": false, "issues": [
		_orphan("a"), _orphan("b"),
		_issue("plugin_cfg_version_drift", "'vbeami': drift."),
	]})

	runner.equal(doctor.summarize(), "Doctor found 3 issues in 2 categories.",
		"the log line says how much there is without listing any of it")


func test_summarize_says_so_when_there_is_nothing_wrong() -> void:
	var doctor: DotquayDoctor = DOCTOR.from_payload({"healthy": true, "issues": []})

	runner.equal(doctor.summarize(), "Doctor found no issues.", "a healthy project gets a plain sentence")
