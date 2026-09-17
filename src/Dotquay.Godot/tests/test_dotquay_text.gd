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
