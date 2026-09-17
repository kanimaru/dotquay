@tool
class_name DotquayText
extends RefCounted

## Small, generic text-formatting helpers that know nothing about the wire contract or the plugin's
## colours — kept apart from [DotquayWire] (CLI literals) and [DotquayPalette] (status colours) so
## none of the three has to depend on either of the other two just to format a sentence.


## Picks [param singular_format] for exactly one, [param plural_format] for anything else (zero
## included — "0 addons", never "0 addon"), and substitutes [param count] into whichever is chosen.
## Every call site that used to hand-roll `"%d addon(s)"` or a bare `"%d dependency/dependencies"`
## routes through here instead, so the one rule that actually varies by language ("what counts as
## one") lives in exactly one place rather than being re-decided at each label.
static func plural(count: int, singular_format: String, plural_format: String) -> String:
	return (singular_format if count == 1 else plural_format) % count

## Renders an empty string as `?`.
##
## Optional wire fields parse to `""` ([method DotquayWire.str_or_empty]) so nothing ever shows the
## literal `<null>` — right for a label that can legitimately be blank, wrong inside a line built from
## several fields at once: `"  gut  3.0.0 -> "` reads as a finished sentence with nothing missing,
## while `"  gut  3.0.0 -> ?"` shows exactly which field the CLI didn't send.
static func or_unknown(text: String) -> String:
	return "?" if text.is_empty() else text
