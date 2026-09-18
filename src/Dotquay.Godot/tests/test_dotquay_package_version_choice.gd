extends RefCounted

## Covers [DotquayPackageVersionChoice]: what the package version dialog says, and when its OK button
## may be pressed (DESIGN-v0.19 §7a.3).

const GRAPH := preload("res://addons/dotquay/core/dotquay_graph.gd")
const CHOICE := preload("res://addons/dotquay/core/dotquay_package_version_choice.gd")

var runner: Object


func _package(origin: String, required_by: Array = ["vbeami"]) -> DotquayGraph.PackageNode:
	return GRAPH.PackageNode.from_payload(
		{"name": "VBeami.Core", "resolved": "0.4.7", "origin": origin, "required_by": required_by})


func test_transitive_is_a_pin_and_root_is_a_change() -> void:
	runner.check(CHOICE.is_pin(_package("transitive")), "an addon's package gets pinned")
	runner.check(not CHOICE.is_pin(_package("root", ["<project>"])), "the project's own gets changed")


func test_titles_and_ok_labels_follow_the_job() -> void:
	runner.equal(CHOICE.title(_package("transitive")), "Pin VBeami.Core in your project", "pin title")
	runner.equal(CHOICE.ok_label(_package("transitive")), "Pin", "pin button")
	runner.equal(CHOICE.title(_package("root", ["<project>"])), "Change VBeami.Core's version", "change title")
	runner.equal(CHOICE.ok_label(_package("root", ["<project>"])), "Change", "change button")


func test_pin_explanation_names_the_addon_and_the_way_back() -> void:
	var text := CHOICE.explanation(_package("transitive"))

	runner.check(text.contains("vbeami"), "names the addon whose choice is overridden: %s" % text)
	runner.check(text.contains("Remove"), "says how to hand the choice back: %s" % text)


func test_change_explanation_promises_feed_and_assets_stay() -> void:
	# That's what DeclareDotnetAsync keeps (DESIGN-v0.19 §7a.1); the dialog must not promise less or more.
	var text := CHOICE.explanation(_package("root", ["<project>"]))

	runner.check(text.contains("feed") and text.contains("asset"), "says the entry's settings are kept: %s" % text)


func test_initial_version_is_what_is_locked() -> void:
	runner.equal(CHOICE.initial_version(_package("transitive")), "0.4.7", "the field starts at the locked version")


func test_plausible_versions() -> void:
	for version in ["1", "1.2", "1.2.3", "1.2.3.4", "1.0.0-beta.1", "2.0.0-rc1+build.5", " 1.2.3 "]:
		runner.check(CHOICE.is_plausible_version(version), "%s is a NuGet version" % version)


func test_implausible_versions() -> void:
	for version in ["", "latest", "1.*", "[1.0,2.0)", "1.2.3.4.5", "v1.2.3", "1..2"]:
		runner.check(not CHOICE.is_plausible_version(version), "'%s' is not an exact NuGet version" % version)


func test_blocking_reason_for_an_empty_field() -> void:
	runner.check(not CHOICE.blocking_reason(_package("transitive"), "  ").is_empty(), "an empty field blocks OK")


func test_blocking_reason_names_a_range_as_the_problem() -> void:
	var reason := CHOICE.blocking_reason(_package("transitive"), "0.4.*")
	runner.check(reason.contains("range"), "says why a range is refused: %s" % reason)


func test_pinning_the_locked_version_is_allowed() -> void:
	# It still moves the choice into the project, which is the point of a pin.
	runner.equal(CHOICE.blocking_reason(_package("transitive"), "0.4.7"), "", "pin at the locked version is fine")


func test_changing_to_the_locked_version_is_blocked() -> void:
	runner.check(not CHOICE.blocking_reason(_package("root", ["<project>"]), "0.4.7").is_empty(),
		"a change that changes nothing is refused before the CLI round trip")


func test_a_different_version_is_accepted_for_both_jobs() -> void:
	runner.equal(CHOICE.blocking_reason(_package("transitive"), "0.4.9"), "", "pin")
	runner.equal(CHOICE.blocking_reason(_package("root", ["<project>"]), "0.4.9"), "", "change")
