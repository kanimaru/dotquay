extends RefCounted

## Covers [DotquayPalette.escape_bbcode] and its use inside [method DotquayPalette.bbcode] — the fix
## for P1-28: the log's [RichTextLabel] has BBCode parsing on, so an addon name, a NuGet range like
## `[1.0,2.0)`, or a `[addons.x]` TOML table the CLI quoted back in an error must not be interpreted
## as real markup.

const PALETTE := preload("res://addons/dotquay/core/dotquay_palette.gd")

var runner: Object


func test_a_bracket_is_neutralised() -> void:
	runner.equal(PALETTE.escape_bbcode("range [1.0,2.0)"), "range [lb]1.0,2.0)",
		"a lone '[' must not be read as the start of a tag")


func test_text_with_no_brackets_is_unchanged() -> void:
	runner.equal(PALETTE.escape_bbcode("nothing to escape here"), "nothing to escape here",
		"ordinary text should round-trip untouched")


func test_a_fake_closing_tag_cannot_end_the_real_colour_tag_early() -> void:
	# The defect this guards against: user/CLI text containing "[/color]" (or any other real tag)
	# must not be able to close the colour span this text is about to be wrapped in.
	var escaped := PALETTE.escape_bbcode("[/color]injected")
	var wrapped := PALETTE.bbcode(Color.WHITE, escaped)

	runner.equal(wrapped, "[color=#ffffff][lb]/color]injected[/color]",
		"the injected close tag should read as '[lb]/color]' — inert text — leaving only the wrapper's own real closing tag")


func test_escaping_runs_before_the_colour_wrapper_is_applied() -> void:
	# A caller escapes its own text first, then wraps it — bbcode() does not escape a second time,
	# because a caller building a line from several already-escaped, already-coloured pieces must not
	# have its own markup mangled by a second pass.
	var line := PALETTE.bbcode(PALETTE.WARN, PALETTE.escape_bbcode("[addons.x] table"))

	runner.check(line.contains("[lb]"), "the bracket from the original text should have been escaped")
	runner.equal(line.count("[color="), 1, "wrapping already-escaped text must add exactly one colour tag, not mangle it")
