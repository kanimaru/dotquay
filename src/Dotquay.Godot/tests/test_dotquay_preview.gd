extends RefCounted

## Covers [DotquayPreview] — the typed view over `add --preview --json`, and the phrasing the
## confirmation dialog puts in front of a decision.
##
## The fixture is the shape v0.8 was built for: one addon the user asked for, one it drags in, a
## private NuGet package, and the feed that package needs. That is precisely the install where
## pressing "Add" without a dialog would take on a feed nobody mentioned.

const PREVIEW := preload("res://addons/dotquay/core/dotquay_preview.gd")

var runner: Object

const FEED_URL := "https://gitlab.example.com/api/v4/groups/7/-/packages/nuget/index.json"


func _payload() -> Dictionary:
	return {
		"name": "vbeami",
		"git": "https://gitlab.example.com/wild-alliance/vbeami.git",
		"addons": [
			{"name": "vbeami", "git": "https://gitlab.example.com/wild-alliance/vbeami.git",
			 "version": "v1.0.0", "resolved": "bbbbbbb1111111", "short_sha": "bbbbbbb",
			 "folder": "vbeami", "is_new": true, "previous_resolved": null,
			 "required_by": ["<project>"]},
			{"name": "vbeami_core", "git": "https://gitlab.example.com/wild-alliance/vbeami_core.git",
			 "version": "v1.1.0", "resolved": "ccccccc2222222", "short_sha": "ccccccc",
			 "folder": "core", "is_new": true, "previous_resolved": null,
			 "required_by": ["vbeami"]},
			{"name": "shared", "git": "https://gitlab.example.com/wild-alliance/shared.git",
			 "version": "v3.0.0", "resolved": "ddddddd3333333", "short_sha": "ddddddd",
			 "folder": "shared", "is_new": true, "previous_resolved": "eeeeeee4444444",
			 "required_by": ["vbeami"]},
			{"name": "settled", "git": "https://gitlab.example.com/wild-alliance/settled.git",
			 "version": "v2.0.0", "resolved": "fffffff5555555", "short_sha": "fffffff",
			 "folder": "settled", "is_new": false, "previous_resolved": "fffffff5555555",
			 "required_by": ["vbeami"]},
		],
		"dotnet": [
			{"name": "VBeami.Core", "constraint": "1.0.0", "declared_by_addon": "vbeami", "is_new": true},
			{"name": "Newtonsoft.Json", "constraint": "13.0.*", "declared_by_addon": null, "is_new": false},
		],
		"sources_to_adopt": [],
		"sources_needing_approval": [
			{"name": "vbeami", "url": FEED_URL, "declared_by_addon": "vbeami",
			 "is_trusted_host": false, "packages": ["VBeami.*"], "package_ids": ["VBeami.Core"]},
		],
		"needs_approval": true,
		"is_no_op": false,
	}


func test_parses_the_envelope_payload() -> void:
	var preview: DotquayPreview = PREVIEW.from_payload(_payload())

	runner.equal(preview.name, "vbeami", "preview names the addon being added")
	runner.equal(preview.addons.size(), 4, "every addon in the resolved graph is kept")
	runner.equal(preview.dotnet.size(), 2, "every package is kept, new or not")
	runner.check(preview.needs_approval, "an unapproved feed sets needs_approval")
	runner.check(not preview.is_no_op, "an install that changes things is not a no-op")


func test_separates_what_you_asked_for_from_what_it_drags_in() -> void:
	var preview: DotquayPreview = PREVIEW.from_payload(_payload())

	# The count that justifies showing the dialog at all. "settled" is excluded because it isn't
	# changing, so warning about it would be noise.
	runner.equal(preview.transitive_addons().size(), 2, "two addons arrive that were never named")
	runner.equal(preview.new_addons().size(), 3, "the unchanged addon is not counted as new")

	var requested: DotquayPreview.Addon = preview.addons[0]
	runner.check(requested.is_requested(), "the <project> requirer marks the addon the user typed")
	runner.equal(requested.describe_origin(), "you asked for this", "the requested addon says so plainly")


func test_distinguishes_an_addon_arriving_from_one_moving() -> void:
	var preview: DotquayPreview = PREVIEW.from_payload(_payload())

	var arriving: DotquayPreview.Addon = preview.addons[1]
	runner.check(not arriving.is_move(), "an addon with no previous commit is arriving, not moving")
	runner.equal(arriving.marker(), "+", "an arriving addon is marked +")

	# The outcome people most need warning about: an add that moves something already installed.
	var moving: DotquayPreview.Addon = preview.addons[2]
	runner.check(moving.is_move(), "a new commit over an existing one is a move")
	runner.equal(moving.marker(), "~", "a moving addon is marked ~")

	var unchanged: DotquayPreview.Addon = preview.addons[3]
	runner.equal(unchanged.marker(), "=", "an unchanged addon is marked =")


func test_names_the_addon_that_wants_each_dependency() -> void:
	var preview: DotquayPreview = PREVIEW.from_payload(_payload())

	runner.equal(preview.addons[1].describe_origin(), "required by vbeami",
		"a transitive addon names its requirer")

	var package: DotquayPreview.DotnetPackage = preview.dotnet[0]
	runner.equal(package.describe_origin(), "required by vbeami", "an addon-declared package names the addon")
	runner.equal(preview.dotnet[1].describe_origin(), "declared by this project",
		"a project-declared package says so")


func test_source_states_the_scope_of_what_it_would_serve() -> void:
	var preview: DotquayPreview = PREVIEW.from_payload(_payload())
	var source: DotquayPreview.Source = preview.sources_needing_approval[0]

	runner.equal(source.host(), "gitlab.example.com", "the host is extracted for a sentence about trust")
	runner.equal(source.declared_by_addon, "vbeami", "the asking addon is named")
	runner.check(not source.is_trusted_host, "an unapproved feed is not on a trusted host")
	runner.equal(String(", ".join(source.owned_patterns())), "VBeami.*",
		"declared patterns are what the feed would own")


func test_source_falls_back_to_concrete_package_ids_when_no_patterns_are_declared() -> void:
	# Mirrors the CLI's own default (SPEC.md §11.3 D7): absent `packages`, the feed owns exactly the
	# IDs the addon declared. Showing nothing here would understate the scope.
	var source: DotquayPreview.Source = PREVIEW.Source.from_payload({
		"name": "vbeami", "url": FEED_URL, "declared_by_addon": "vbeami",
		"is_trusted_host": false, "packages": [], "package_ids": ["VBeami.Core"]})

	runner.equal(String(", ".join(source.owned_patterns())), "VBeami.Core",
		"package ids stand in for absent patterns")


func test_trusted_feed_is_reported_as_automatic() -> void:
	var payload := _payload()
	payload["sources_to_adopt"] = [{
		"name": "vbeami", "url": FEED_URL, "declared_by_addon": "vbeami",
		"is_trusted_host": true, "packages": ["VBeami.*"], "package_ids": ["VBeami.Core"]}]
	payload["sources_needing_approval"] = []
	payload["needs_approval"] = false

	var preview: DotquayPreview = PREVIEW.from_payload(payload)

	runner.check(not preview.needs_approval, "a trusted host needs no approval")
	runner.check(preview.sources_to_adopt[0].is_trusted_host, "the automatic adoption is flagged as such")

	# Still listed, because a feed being taken on silently is exactly the thing worth being able to
	# see you agreed to.
	runner.equal(preview.all_sources().size(), 1, "an automatic feed still appears in the dialog")


func test_summary_counts_what_the_install_actually_adds() -> void:
	var summary: String = PREVIEW.from_payload(_payload()).summarize()

	runner.check(summary.contains("3 addon(s)"), "the summary counts new addons: %s" % summary)
	runner.check(summary.contains("2 of them pulled in"), "the summary calls out unrequested addons: %s" % summary)
	runner.check(summary.contains("1 NuGet package(s)"), "the summary counts new packages: %s" % summary)
	runner.check(summary.contains("1 NuGet feed(s)"), "the summary counts feeds: %s" % summary)


func test_summary_says_plainly_when_nothing_would_change() -> void:
	var payload := _payload()
	payload["is_no_op"] = true

	var summary: String = PREVIEW.from_payload(payload).summarize()

	runner.check(summary.contains("already installed"), "a no-op says so rather than counting to zero: %s" % summary)


func test_null_arrays_parse_as_empty_not_a_crash() -> void:
	# SPEC.md §17: optional array fields are emitted as JSON null, never omitted. `payload.get(key,
	# [])` only supplies its default when the key is absent, so a present-but-null "addons" used to
	# raise "Unable to iterate on object of type 'Nil'" instead of parsing.
	var payload := _payload()
	payload["addons"] = null
	payload["dotnet"] = null
	payload["sources_to_adopt"] = null
	payload["sources_needing_approval"] = null

	var preview: DotquayPreview = PREVIEW.from_payload(payload)

	runner.equal(preview.addons.size(), 0, "a null addons array should parse as empty, not raise")
	runner.equal(preview.dotnet.size(), 0, "a null dotnet array should parse as empty, not raise")
	runner.equal(preview.sources_to_adopt.size(), 0, "a null sources_to_adopt array should parse as empty")
	runner.equal(preview.sources_needing_approval.size(), 0,
		"a null sources_needing_approval array should parse as empty")


func test_null_required_by_parses_as_empty_not_a_crash() -> void:
	var addon := PREVIEW.Addon.from_payload({
		"name": "leaf", "git": "https://example.com/leaf", "version": "v1.0.0",
		"resolved": "aaaaaaa", "short_sha": "aaaaaaa", "folder": "leaf", "is_new": true,
		"required_by": null,
	})

	runner.equal(addon.required_by.size(), 0, "a null required_by array should parse as empty, not raise")


func test_null_resolved_short_sha_and_folder_read_as_empty_or_default() -> void:
	# `resolved` and `short_sha` must read as "" like DotquayGraph's AddonNode already does; `folder`
	# must still fall back to the name rather than render the literal text "<null>".
	var addon := PREVIEW.Addon.from_payload({
		"name": "leaf", "git": "https://example.com/leaf", "version": null,
		"resolved": null, "short_sha": null, "folder": null, "is_new": true,
	})

	runner.equal(addon.resolved, "", "a null resolved should read as absent, not '<null>'")
	runner.equal(addon.short_sha, "", "a null short_sha should read as absent, not '<null>'")
	runner.equal(addon.folder, "leaf", "a null folder means the addon's own name")


func test_missing_fields_do_not_break_the_parse() -> void:
	# A payload from a dotquay older or newer than this plugin must degrade to empty rather than
	# throw — the schema check in DotquayResult is the guard for real mismatches, and this is the
	# backstop for everything it lets through.
	var preview: DotquayPreview = PREVIEW.from_payload({})

	runner.equal(preview.name, "", "a missing name is empty, not <null>")
	runner.equal(preview.addons.size(), 0, "missing arrays parse as empty")
	runner.check(not preview.needs_approval, "a missing flag defaults to not needing approval")
