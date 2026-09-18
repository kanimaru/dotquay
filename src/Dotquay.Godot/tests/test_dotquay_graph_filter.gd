extends RefCounted

## Covers [DotquayGraphFilter], the graph toolbar's All / Addons / NuGet switch (DESIGN-v0.19 §1).

const GRAPH := preload("res://addons/dotquay/core/dotquay_graph.gd")
const FILTER := preload("res://addons/dotquay/core/dotquay_graph_filter.gd")

var runner: Object


func _graph(addons: bool, packages: bool) -> DotquayGraph:
	return GRAPH.from_payload({
		"roots": ["a"] if addons else [],
		"addons": [{"name": "a", "origin": "root"}] if addons else [],
		"edges": [],
		"dotnet": [{"name": "Serilog", "origin": "root", "required_by": ["<project>"]}] if packages else [],
	})


func test_all_shows_both_kinds() -> void:
	runner.check(FILTER.shows_addons(FILTER.Mode.ALL) and FILTER.shows_packages(FILTER.Mode.ALL), "All shows everything")


func test_addons_hides_packages() -> void:
	runner.check(FILTER.shows_addons(FILTER.Mode.ADDONS), "Addons shows addons")
	runner.check(not FILTER.shows_packages(FILTER.Mode.ADDONS), "Addons hides packages")


func test_packages_hides_addons() -> void:
	runner.check(not FILTER.shows_addons(FILTER.Mode.PACKAGES), "NuGet hides addons")
	runner.check(FILTER.shows_packages(FILTER.Mode.PACKAGES), "NuGet shows packages")


func test_every_mode_has_a_label_and_tooltip() -> void:
	for mode in FILTER.MODES:
		runner.check(not FILTER.label(mode).is_empty(), "mode %d has a label" % mode)
		runner.check(not FILTER.tooltip(mode).is_empty(), "mode %d has a tooltip" % mode)


func test_empty_hint_for_a_project_with_nothing() -> void:
	runner.check(not FILTER.empty_hint(null, FILTER.Mode.ALL).is_empty(), "no graph at all gets a hint")
	runner.check(not FILTER.empty_hint(_graph(false, false), FILTER.Mode.ALL).is_empty(), "an empty graph gets a hint")


func test_empty_hint_is_blank_when_something_is_visible() -> void:
	runner.equal(FILTER.empty_hint(_graph(true, true), FILTER.Mode.ALL), "", "a populated graph needs no hint")
	runner.equal(FILTER.empty_hint(_graph(false, true), FILTER.Mode.PACKAGES), "", "packages under NuGet need no hint")


func test_empty_hint_explains_a_filter_that_hides_everything() -> void:
	# A blank canvas with dependencies in the project would read as "dotquay lost my addons".
	runner.check(FILTER.empty_hint(_graph(false, true), FILTER.Mode.ADDONS).contains("All"),
		"Addons on a packages-only project says where they went")
	runner.check(not FILTER.empty_hint(_graph(true, false), FILTER.Mode.PACKAGES).is_empty(),
		"NuGet on an addons-only project says there are none")
