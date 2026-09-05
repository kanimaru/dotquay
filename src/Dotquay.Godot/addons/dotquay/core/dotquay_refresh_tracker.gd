@tool
class_name DotquayRefreshTracker
extends RefCounted

## Remembers which in-flight CLI runs must be followed by a graph refresh.
##
## The panel used to keep one boolean, "refresh after the current command". That is wrong the moment
## two commands overlap: switching back to the tab mid-`update` fires a `graph` read, that read
## finishes first, consumes the flag, and the `update` — the one that actually changed the project —
## completes without ever refreshing. The graph on screen is then stale until the next click.
##
## Keyed on the ticket [method DotquayCli.run] returns, not on the command name: the envelope reports
## "override" for both a pin and a `--clear`, and two `update <name>` runs can overlap.

var _tickets: Dictionary = {}


## Marks the run identified by [param ticket] as one whose completion must trigger a refresh.
func arm(ticket: int) -> void:
	_tickets[ticket] = true


## Reports that [param ticket] finished. Returns [code]true[/code] exactly once for a ticket that was
## armed, and [code]false[/code] for anything else — including a second call with the same ticket.
func settle(ticket: int) -> bool:
	if not _tickets.has(ticket):
		return false

	_tickets.erase(ticket)
	return true


func is_armed(ticket: int) -> bool:
	return _tickets.has(ticket)


func pending_count() -> int:
	return _tickets.size()
