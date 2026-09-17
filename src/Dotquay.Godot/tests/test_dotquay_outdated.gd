extends RefCounted

## Covers [DotquayOutdated] — the typed view over `outdated --json` (C-24). Previously this payload
## was read ad hoc inside `dotquay_panel.gd`, with no fixture test of its own, unlike graph/preview/
## explain.

const OUTDATED := preload("res://addons/dotquay/core/dotquay_outdated.gd")

var runner: Object


func _payload() -> Dictionary:
	return {
		"outdated_count": 2,
		"addons": [
			{"name": "core", "current": "v1.0.0", "latest": "v1.2.0", "is_outdated": true},
			{"name": "leaf", "current": "v2.0.0", "latest": "v2.0.0", "is_outdated": false},
		],
		"dotnet": [
			{"name": "Newtonsoft.Json", "current": "13.0.1", "latest": "13.0.3", "is_outdated": true},
		],
	}


func test_parses_the_envelope_payload() -> void:
	var outdated: DotquayOutdated = OUTDATED.from_payload(_payload())

	runner.equal(outdated.outdated_count, 2, "the count is preserved")
	runner.equal(outdated.addons.size(), 2, "every addon entry is kept, outdated or not")
	runner.equal(outdated.dotnet.size(), 1, "every dotnet entry is kept")
	runner.check(not outdated.is_all_up_to_date(), "a non-zero count is not up to date")


func test_zero_count_reports_up_to_date() -> void:
	var outdated: DotquayOutdated = OUTDATED.from_payload({"outdated_count": 0, "addons": [], "dotnet": []})

	runner.check(outdated.is_all_up_to_date(), "a zero count reports up to date")
	runner.check(outdated.outdated_entries().is_empty(), "there is nothing to list when nothing is outdated")


func test_outdated_entries_filters_to_only_what_moved_addons_before_packages() -> void:
	var outdated: DotquayOutdated = OUTDATED.from_payload(_payload())
	var entries: Array = outdated.outdated_entries()

	runner.equal(entries.size(), 2, "the unchanged addon is excluded")
	runner.equal(entries[0].name, "core", "addons are listed before packages")
	runner.equal(entries[0].current, "v1.0.0", "current is kept")
	runner.equal(entries[0].latest, "v1.2.0", "latest is kept")
	runner.equal(entries[1].name, "Newtonsoft.Json", "the outdated package is included after addons")


func test_null_optional_fields_parse_as_empty_not_a_crash() -> void:
	# SPEC.md §17: an absent optional value is emitted as JSON null, not an omitted key. A missing
	# array here used to raise "Unable to iterate on object of type 'Nil'" instead of parsing as empty.
	var outdated: DotquayOutdated = OUTDATED.from_payload({"addons": null, "dotnet": null})

	runner.equal(outdated.addons.size(), 0, "a null addons array should parse as empty, not raise")
	runner.equal(outdated.dotnet.size(), 0, "a null dotnet array should parse as empty, not raise")

	var entry := DotquayOutdated.Entry.from_payload({"name": null, "current": null, "latest": null})
	runner.equal(entry.name, "", "a null name parses as empty, not '<null>'")
	runner.equal(entry.current, "", "a null current parses as empty, not '<null>'")
	runner.equal(entry.latest, "", "a null latest parses as empty, not '<null>'")


func test_missing_fields_do_not_break_the_parse() -> void:
	var outdated: DotquayOutdated = OUTDATED.from_payload({})

	runner.equal(outdated.outdated_count, 0, "a missing count defaults to zero")
	runner.check(outdated.is_all_up_to_date(), "a missing count reads as up to date")
