@tool
class_name DotquayResult
extends RefCounted

## One parsed `--json` envelope from the dotquay CLI (SPEC.md §17).
##
## Every CLI call produces exactly one of these, success or failure, so callers write a single
## branch on [member ok] instead of guessing whether stdout held a result or a message. A launch
## failure (CLI not found, not executable, crashed before printing) is folded into the same shape —
## from a caller's point of view "dotquay could not run" and "dotquay ran and refused" both mean
## "no data, here is why", and giving them two shapes would only duplicate every error path.

const SCHEMA_SUPPORTED := 1

## Error kinds this addon synthesises for failures that never reached the CLI. The kinds the CLI
## itself reports (SPEC.md §17) live on [DotquayWire] instead (C-1) — those are wire literals like
## every other constant there, and these six are specifically the ones that aren't.
const KIND_NOT_CONFIGURED := "cli_not_configured"
const KIND_LAUNCH_FAILED := "cli_launch_failed"
const KIND_BAD_OUTPUT := "cli_bad_output"
const KIND_SCHEMA_MISMATCH := "cli_schema_mismatch"
## The process started but did not exit within the configured timeout and was killed (P0-7).
const KIND_TIMEOUT := "cli_timeout"

var ok: bool = false
var command: String = ""
var cli_version: String = ""
var schema: int = 0
var data: Dictionary = {}
var error_kind: String = ""
var error_message: String = ""
var warnings: PackedStringArray = PackedStringArray()

## Raw stdout, kept for diagnostics. The panel shows it when parsing failed, because a truncated or
## non-JSON buffer is usually a wrong CLI path pointed at some other executable — and seeing the
## actual bytes is what makes that obvious.
var raw_output: String = ""

var exit_code: int = 0

## The [method DotquayCli.run] call this result answers, or 0 when it was parsed outside the runner
## (tests, the contract suite). Callers keyed per-call state on it; see [DotquayRefreshTracker].
var ticket: int = 0


## Builds a synthesised failure (a launch that never reached the CLI, or a parse that could not make
## sense of what it returned). `raw_output`, when given, is folded into [member error_message] as
## well as stored on [member raw_output] — the caller supplies the lead sentence, this appends the
## evidence.
static func failure(kind: String, message: String, command_name: String, raw_output: String = "") -> DotquayResult:
	var result := DotquayResult.new()
	result.ok = false
	result.command = command_name
	result.error_kind = kind
	result.error_message = message
	result.raw_output = raw_output
	if not raw_output.is_empty():
		# Folded in here, not left for each call site to remember: a shell "Permission denied", a
		# .NET crash trace, or a Gatekeeper block is the actual cause, and it is sitting unread in
		# raw_output unless something surfaces it in the message a person actually looks at.
		result.error_message += "\n%s" % _first_lines(raw_output, 10)
	return result


## A one-line nudge for [constant DotquayWire.KIND_AUTH], blank for every other kind so a caller can
## append it unconditionally. The CLI's own [member error_message] for an auth failure names what git
## or the registry rejected (SPEC.md §17, `GitErrorClassifier`) — not what to do about it, which is
## exactly what the login flow it's paired with (SPEC.md §17: "lets a UI offer a login prompt") is for
## (P1-33).
static func auth_hint(error_kind: String) -> String:
	return "Run 'dotquay login <host>' at a terminal, then retry." if error_kind == DotquayWire.KIND_AUTH else ""


## Cuts diagnostic output down to its first few lines, for folding into an error message without
## drowning the actual cause (a shell "Permission denied", a .NET crash trace, a Gatekeeper block)
## under everything printed after it.
static func _first_lines(text: String, max_lines: int) -> String:
	var lines := text.split("\n")
	if lines.size() <= max_lines:
		return text
	return "\n".join(lines.slice(0, max_lines)) + "\n… (truncated)"


## Cuts a captured buffer down to the outermost `{ ... }` it contains.
##
## The runner captures stderr into the same buffer as stdout (a launch failure only ever writes
## there), so the envelope can be wrapped in noise on *both* sides: a shell profile's banner before
## it, a .NET runtime or git warning after it. Scanning forward from the first `{` and tracking brace
## depth finds the matching close for *that* `{` and stops there, rather than naively pairing the
## first `{` with the buffer's *last* `}` (C-15) — a trailing stderr line that itself contains a `}`
## (a NuGet registry's own error JSON body, say) would otherwise be swallowed into what should have
## been just the CLI's own envelope. Depth-tracking skips over string literals (respecting `\"`
## escapes), so a quoted `}` inside an error message embedded in the JSON itself can't miscount either.
## Anything without a balanced brace pair is returned untouched so the caller reports the real bytes;
## the JSON parser still gets the final say on whether what's left is valid.
static func extract_json_object(buffer: String) -> String:
	var open := buffer.find("{")
	if open < 0:
		return buffer

	var depth := 0
	var in_string := false
	var escaped := false

	for i in range(open, buffer.length()):
		var character := buffer[i]

		if in_string:
			if escaped:
				escaped = false
			elif character == "\\":
				escaped = true
			elif character == "\"":
				in_string = false
			continue

		if character == "\"":
			in_string = true
		elif character == "{":
			depth += 1
		elif character == "}":
			depth -= 1
			if depth == 0:
				return buffer.substr(open, i - open + 1)

	return buffer


## Parses a captured stdout buffer. Never throws: a caller that got here already has a problem and
## needs a description of it, not a second failure mode to handle.
static func parse(output: String, code: int, for_command: String) -> DotquayResult:
	var trimmed := output.strip_edges()

	if trimmed.is_empty():
		var empty := DotquayResult.failure(
			KIND_BAD_OUTPUT,
			"dotquay produced no output (exit code %d). Is the CLI path correct?" % code,
			for_command)
		empty.exit_code = code
		return empty

	# The CLI writes exactly one JSON document on stdout under --json, but a wrapper script or a
	# shell profile can prepend noise. Recovering from the first '{' is cheap and turns an
	# unexplained failure into a working session.
	trimmed = extract_json_object(trimmed)

	# An instance, not the static JSON.parse_string: the static helper pushes a hard error into the
	# editor log on malformed input, and malformed input is an expected outcome here (a wrong CLI
	# path). Reporting it once, as a message, beats reporting it twice with a red stack trace.
	var json := JSON.new()
	var parse_error := json.parse(trimmed)
	var parsed: Variant = json.data if parse_error == OK else null

	if typeof(parsed) != TYPE_DICTIONARY:
		var bad := DotquayResult.failure(
			KIND_BAD_OUTPUT,
			"dotquay didn't answer as expected (exit code %d). Its output was:" % code,
			for_command,
			output)
		bad.exit_code = code
		return bad

	var envelope: Dictionary = parsed

	if String(envelope.get("tool", "")) != "dotquay":
		var wrong := DotquayResult.failure(
			KIND_BAD_OUTPUT,
			"That executable is not dotquay — it returned JSON, but not dotquay's.",
			for_command,
			output)
		wrong.exit_code = code
		return wrong

	var result := DotquayResult.new()
	result.command = String(envelope.get("command", for_command))
	result.cli_version = String(envelope.get("version", ""))
	result.schema = int(envelope.get("schema", 0))
	result.exit_code = code
	result.raw_output = output
	result.ok = bool(envelope.get("ok", false))

	for warning in DotquayWire.array_or_empty(envelope.get("warnings")):
		result.warnings.append(String(warning))

	# Refuse a payload shape this addon wasn't written against rather than half-reading it: silently
	# mis-parsing a graph is worse than saying "update one side of this pair".
	if result.schema != SCHEMA_SUPPORTED:
		result.ok = false
		result.error_kind = KIND_SCHEMA_MISMATCH
		result.error_message = (
			("This plugin and dotquay %s are incompatible versions (schema %d vs %d). Update dotquay "
				+ "(the \"Update dotquay\" button, if shown) or update this plugin.")
			% [result.cli_version, result.schema, SCHEMA_SUPPORTED])
		if not result.raw_output.is_empty():
			result.error_message += "\n%s" % _first_lines(result.raw_output, 10)
		return result

	var error_value: Variant = envelope.get("error")
	if typeof(error_value) == TYPE_DICTIONARY:
		var error_dict: Dictionary = error_value
		result.error_kind = String(error_dict.get("kind", DotquayWire.KIND_UNEXPECTED))
		result.error_message = String(error_dict.get("message", ""))
		result.ok = false
	elif not result.ok:
		# A failure envelope with no `error` object at all, or one where it isn't a dictionary (a
		# bare string, say) — still a failure, so error_message must not be left empty.
		result.error_kind = DotquayWire.KIND_UNEXPECTED
		result.error_message = "dotquay reported a failure (exit code %d) but gave no details." % code

	var data_value: Variant = envelope.get("data")
	if typeof(data_value) == TYPE_DICTIONARY:
		result.data = data_value

	return result
