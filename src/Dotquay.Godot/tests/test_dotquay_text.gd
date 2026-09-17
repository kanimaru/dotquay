extends RefCounted

## Covers [DotquayText.plural] — the one place the P2 wording pass's pluralisation rule lives, so
## every "%d addon(s)"-style label in the panel can be checked once here instead of by re-reading
## every call site that used to hand-roll its own singular/plural string.

const TEXT := preload("res://addons/dotquay/core/dotquay_text.gd")

var runner: Object


func test_exactly_one_uses_the_singular_form() -> void:
	runner.equal(TEXT.plural(1, "%d addon", "%d addons"), "1 addon",
		"a count of exactly one should pick the singular template")


func test_zero_uses_the_plural_form() -> void:
	# English pluralises zero ("0 addons"), not "0 addon" — the common off-by-one a hand-rolled
	# singular/plural check gets wrong.
	runner.equal(TEXT.plural(0, "%d addon", "%d addons"), "0 addons",
		"zero should read as plural, not singular")


func test_several_uses_the_plural_form() -> void:
	runner.equal(TEXT.plural(3, "%d addon", "%d addons"), "3 addons",
		"more than one should pick the plural template")


func test_the_count_is_substituted_into_whichever_template_is_chosen() -> void:
	runner.equal(TEXT.plural(1, "%d file has real edits", "%d files have real edits"), "1 file has real edits",
		"templates that differ by more than a trailing 's' still get the right count")
	runner.equal(TEXT.plural(5, "%d file has real edits", "%d files have real edits"), "5 files have real edits",
		"and the plural template gets it too")

func test_or_unknown_marks_an_empty_field_rather_than_leaving_a_gap() -> void:
	# A null optional field parses to "", which inside a multi-field log line would read as a
	# finished sentence with nothing wrong with it.
	runner.equal(TEXT.or_unknown(""), "?", "an empty field renders as a visible placeholder")


func test_or_unknown_leaves_a_real_value_alone() -> void:
	runner.equal(TEXT.or_unknown("v1.2.0"), "v1.2.0", "a value the CLI did send is passed through")
	runner.equal(TEXT.or_unknown("0"), "0", "and so is a falsy-looking but real one")
