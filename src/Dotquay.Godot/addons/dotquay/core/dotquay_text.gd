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
