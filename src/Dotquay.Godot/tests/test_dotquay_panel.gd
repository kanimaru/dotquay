extends RefCounted

## Covers [DotquayPanel]'s pure static helpers. The panel itself is a scene-bound [Control] this suite
## deliberately never instantiates (it owns a live [DotquayCli] child and a dozen dialogs), but the
## ordering rule graph reads need (P1-30) doesn't touch any of that — pulled out as a static function
## of plain data, the same way [DotquayDependencyNode] and [DotquaySetupBanner] made their own
## panel-adjacent decisions testable without a scene tree.

const PANEL := preload("res://addons/dotquay/ui/dotquay_panel.gd")
const RESULT := preload("res://addons/dotquay/core/dotquay_result.gd")
const WIRE := preload("res://addons/dotquay/core/dotquay_wire.gd")

var runner: Object


func _graph_result(ticket: int) -> DotquayResult:
	var result := RESULT.new()
	result.command = WIRE.COMMAND_GRAPH
	result.ticket = ticket
	return result


func test_a_graph_result_matching_the_latest_ticket_should_apply() -> void:
	runner.check(PANEL.should_apply_graph_result(_graph_result(5), 5),
		"the newest graph read's own reply should always apply")


func test_a_stale_graph_result_should_not_apply() -> void:
	# Refresh-on-focus, Locate, a self-update, and the explain fallback can all trigger a `graph` read;
	# only the newest one issued is still worth trusting once several are in flight.
	runner.check(not PANEL.should_apply_graph_result(_graph_result(3), 5),
		"a reply from an older graph read that arrives after a newer one was issued should be dropped")


func test_a_non_graph_result_always_applies_regardless_of_the_graph_ticket() -> void:
	var result := RESULT.new()
	result.command = WIRE.COMMAND_INSTALL
	result.ticket = 1

	runner.check(PANEL.should_apply_graph_result(result, 999),
		"the staleness rule only ever concerns graph reads — every other command has nothing to race against")
