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

## Error kinds the CLI itself reports, mirroring JsonErrorKind in Dotquay.Core.
const KIND_WORKFLOW := "workflow"
const KIND_NO_MANIFEST := "no_manifest"
const KIND_CONFLICT := "conflict"
const KIND_GIT := "git"
const KIND_AUTH := "auth"
const KIND_UNEXPECTED := "unexpected"

## Error kinds this addon synthesises for failures that never reached the CLI.
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
## it, a .NET runtime or git warning after it. Trimming to the first `{` and the last `}` is a cheap
## recovery for both, and the JSON parser still gets the final say on whether what's left is valid.
## Anything without a brace pair is returned untouched so the caller reports the real bytes.
static func extract_json_object(buffer: String) -> String:
	var open := buffer.find("{")
	var close := buffer.rfind("}")
	if open < 0 or close < open:
		return buffer
	return buffer.substr(open, close - open + 1)


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
			"dotquay did not return JSON (exit code %d). Check that the CLI path points at the dotquay executable." % code,
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
			"dotquay %s speaks payload schema %d, but this plugin understands %d. Update whichever is older."
			% [result.cli_version, result.schema, SCHEMA_SUPPORTED])
		if not result.raw_output.is_empty():
			result.error_message += "\n%s" % _first_lines(result.raw_output, 10)
		return result

	var error_value: Variant = envelope.get("error")
	if typeof(error_value) == TYPE_DICTIONARY:
		var error_dict: Dictionary = error_value
		result.error_kind = String(error_dict.get("kind", KIND_UNEXPECTED))
		result.error_message = String(error_dict.get("message", ""))
		result.ok = false
	elif not result.ok:
		# A failure envelope with no `error` object at all, or one where it isn't a dictionary (a
		# bare string, say) — still a failure, so error_message must not be left empty.
		result.error_kind = KIND_UNEXPECTED
		result.error_message = "dotquay reported a failure (exit code %d) but gave no details." % code

	var data_value: Variant = envelope.get("data")
	if typeof(data_value) == TYPE_DICTIONARY:
		result.data = data_value

	return result
