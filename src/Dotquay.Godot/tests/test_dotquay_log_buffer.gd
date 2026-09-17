extends RefCounted

## Covers [DotquayLogBuffer] — the panel log's bounded store.
##
## What this suite asserts hardest: the entry that trips the limit must survive, and the limit must
## count messages rather than whatever a label thinks a line is.

const BUFFER := preload("res://addons/dotquay/core/dotquay_log_buffer.gd")

var runner: Object


func _filled(capacity: int, count: int) -> DotquayLogBuffer:
	var buffer: DotquayLogBuffer = BUFFER.new(capacity)
	for i in range(count):
		buffer.append("line %d" % i)
	return buffer


func test_renders_entries_in_order_with_trailing_newline() -> void:
	var buffer := _filled(10, 3)
	runner.equal(buffer.render(), "line 0\nline 1\nline 2\n", "entries render oldest first, one per line")


func test_empty_buffer_renders_nothing() -> void:
	runner.equal(BUFFER.new().render(), "", "an empty log is an empty string, not a stray newline")


func test_keeps_the_entry_that_crossed_the_limit() -> void:
	var buffer := _filled(3, 4)

	runner.equal(buffer.size(), 3, "the buffer never holds more than its capacity")
	runner.check(buffer.entries().has("line 3"), "the newest entry — the one that tripped the trim — must survive")
	runner.check(not buffer.entries().has("line 0"), "the oldest entry is what goes")


func test_reports_how_many_were_dropped() -> void:
	runner.equal(_filled(2, 5).dropped(), 3, "dropped count is total minus capacity")
	runner.equal(_filled(5, 5).dropped(), 0, "exactly at capacity drops nothing")


func test_marks_the_render_once_anything_was_dropped() -> void:
	var full := _filled(2, 2)
	runner.check(not full.render().begins_with(BUFFER.TRIMMED_MARKER), "no marker while nothing has been dropped")

	full.append("one more")
	runner.check(full.render().begins_with(BUFFER.TRIMMED_MARKER), "the marker appears once something has gone")
	runner.equal(full.render("MARK"), "MARK\nline 1\none more\n", "a caller-supplied marker replaces the default")


func test_a_long_entry_still_counts_as_one() -> void:
	# The label-based log trimmed on *visual* lines, so a wrapped paragraph ate the budget of a dozen
	# messages. The buffer counts messages.
	var buffer: DotquayLogBuffer = BUFFER.new(2)
	buffer.append("a\nmulti\nline\nentry")
	buffer.append("second")
	runner.equal(buffer.size(), 2, "an entry with embedded newlines is one entry")
	runner.equal(buffer.dropped(), 0, "and it drops nothing")


func test_capacity_floor_is_one() -> void:
	var buffer: DotquayLogBuffer = BUFFER.new(0)
	buffer.append("x")
	buffer.append("y")
	runner.equal(buffer.entries(), PackedStringArray(["y"]), "a nonsense capacity still keeps the latest entry")


func test_clear_forgets_everything_including_drops() -> void:
	var buffer := _filled(1, 3)
	buffer.clear()
	runner.equal(buffer.size(), 0, "clear empties the entries")
	runner.equal(buffer.dropped(), 0, "clear resets the dropped count")
	runner.equal(buffer.render(), "", "a cleared log renders empty, without a stale marker")


func test_changed_fires_on_append_and_clear() -> void:
	var buffer: DotquayLogBuffer = BUFFER.new()
	var fired := [0]
	buffer.changed.connect(func() -> void: fired[0] += 1)

	buffer.append("a")
	buffer.clear()
	runner.equal(fired[0], 2, "the view is told about every mutation")


func test_entries_is_a_copy() -> void:
	var buffer := _filled(5, 1)
	var copy := buffer.entries()
	copy.append("smuggled")
	runner.equal(buffer.size(), 1, "mutating the returned array must not mutate the buffer")
