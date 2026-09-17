extends RefCounted

## Covers [DotquayRefreshTracker] — which finished CLI runs owe the panel a graph refresh.
##
## The scenario that matters is the interleaving a single shared flag gets wrong: a read-only `graph`
## finishing while a mutating `update` is still running must not steal the update's refresh.

const TRACKER := preload("res://addons/dotquay/core/dotquay_refresh_tracker.gd")

var runner: Object


func test_an_unarmed_ticket_settles_to_false() -> void:
	var tracker: DotquayRefreshTracker = TRACKER.new()
	runner.check(not tracker.settle(7), "a run nobody armed owes nothing")


func test_an_armed_ticket_settles_to_true_exactly_once() -> void:
	var tracker: DotquayRefreshTracker = TRACKER.new()
	tracker.arm(1)

	runner.check(tracker.settle(1), "an armed run owes a refresh when it finishes")
	runner.check(not tracker.settle(1), "but only once — a duplicate reply must not refresh twice")
	runner.check(not tracker.is_armed(1), "and it is forgotten afterwards")


func test_a_read_finishing_first_does_not_steal_a_mutations_refresh() -> void:
	var tracker: DotquayRefreshTracker = TRACKER.new()

	var update_ticket := 1
	var graph_ticket := 2
	tracker.arm(update_ticket)

	# Tab switch mid-update: the cheap graph read comes back first.
	runner.check(not tracker.settle(graph_ticket), "the graph read owes nothing")
	runner.check(tracker.is_armed(update_ticket), "and the update is still owed its refresh")

	runner.check(tracker.settle(update_ticket), "the update refreshes when *it* finishes")


func test_several_mutations_each_get_their_own_refresh() -> void:
	var tracker: DotquayRefreshTracker = TRACKER.new()
	tracker.arm(1)
	tracker.arm(2)
	runner.equal(tracker.pending_count(), 2, "two armed runs are two pending refreshes")

	runner.check(tracker.settle(2), "out-of-order completion is fine")
	runner.check(tracker.settle(1), "each armed run settles independently")
	runner.equal(tracker.pending_count(), 0, "nothing is left pending")


func test_arming_twice_is_idempotent() -> void:
	var tracker: DotquayRefreshTracker = TRACKER.new()
	tracker.arm(3)
	tracker.arm(3)
	runner.equal(tracker.pending_count(), 1, "the same ticket is one pending refresh, not two")
