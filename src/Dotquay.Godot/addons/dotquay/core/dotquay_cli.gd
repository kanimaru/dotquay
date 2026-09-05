@tool
class_name DotquayCli
extends Node

## Runs the dotquay CLI and hands back a parsed [DotquayResult].
##
## A [Node] rather than a [RefCounted] so it can own worker threads with a real lifetime: the editor
## can disable the plugin mid-fetch, and `_exit_tree` is the only reliable place to join a thread
## before the script is unloaded.
##
## Every call runs on its own thread. `OS.execute` blocks until the process exits, and a `dotquay
## update` that fetches half a dozen repositories takes seconds — doing that on the main thread
## freezes the whole editor, which is exactly the CLI experience this panel exists to improve on.

## Emitted on the main thread when a run finishes, successfully or not.
signal finished(result: DotquayResult)

## Emitted when a run starts and when the queue empties, so the panel can show a spinner without
## tracking counts itself.
signal busy_changed(is_busy: bool)

const RESULT := preload("res://addons/dotquay/core/dotquay_result.gd")
const SETTINGS := preload("res://addons/dotquay/core/dotquay_settings.gd")

var _threads: Array[Thread] = []
var _running := 0

## Handed out by [method run], one per call, and echoed back on the result as
## [member DotquayResult.ticket]. It is the only reliable way to match a result to the call that
## produced it: the envelope's `command` is the CLI's name for what ran ("override" for both a pin and
## a `--clear`), and several calls can be in flight at once.
var _next_ticket := 1


func _exit_tree() -> void:
	# Joining rather than abandoning: a half-finished `dotquay update` is writing to addons/ and the
	# lockfile, and letting the editor tear the script out from under it is how a project ends up in
	# a state neither the lockfile nor disk describes.
	for thread in _threads:
		if thread.is_started():
			thread.wait_to_finish()
	_threads.clear()
	_running = 0


func is_busy() -> bool:
	return _running > 0


## Runs `dotquay <args> --json --project <project_dir>` in the background.
##
## [param command_name] is only used to label a failure that never reached the CLI (it can't report
## its own command name if it didn't run).
##
## Returns the ticket the eventual [signal finished] result will carry, so a caller can stash
## per-call state (a pending request, a "refresh afterwards" flag) against exactly this run.
func run(command_name: String, args: PackedStringArray) -> int:
	var ticket := _next_ticket
	_next_ticket += 1

	var editor_settings := EditorInterface.get_editor_settings()
	var executable := SETTINGS.cli_path(editor_settings)

	if executable.is_empty():
		_emit_deferred(RESULT.failure(
			RESULT.KIND_NOT_CONFIGURED,
			"No dotquay executable configured or found on PATH. Set Editor Settings -> Dotquay -> CLI Path.",
			command_name), ticket)
		return ticket

	if not FileAccess.file_exists(executable):
		_emit_deferred(RESULT.failure(
			RESULT.KIND_NOT_CONFIGURED,
			"The configured dotquay path does not exist: %s" % executable,
			command_name), ticket)
		return ticket

	var full_args := PackedStringArray(args)
	full_args.append("--json")
	full_args.append("--project")
	full_args.append(project_directory())

	_running += 1
	if _running == 1:
		busy_changed.emit(true)

	var thread := Thread.new()
	_threads.append(thread)
	thread.start(_run_blocking.bind(executable, full_args, command_name, ticket, thread))
	return ticket


## The Godot project's directory on the real filesystem. The CLI has no idea what `res://` means,
## and the editor's own working directory is not the project — this is why the CLI grew `--project`
## (SPEC.md §17).
func project_directory() -> String:
	return ProjectSettings.globalize_path("res://").rstrip("/")


func _run_blocking(executable: String, args: PackedStringArray, command_name: String, ticket: int, thread: Thread) -> void:
	var output: Array = []
	# read_stderr: true. dotquay's own errors arrive as a JSON envelope on stdout, but a *launch*
	# failure (missing .NET runtime, a wrong architecture) only ever writes to stderr — folding both
	# in means the panel can show the real reason instead of "no output".
	var code := OS.execute(executable, args, output, true, false)

	var joined := ""
	for line in output:
		joined += String(line)

	var result: DotquayResult
	if code < 0:
		result = RESULT.failure(
			RESULT.KIND_LAUNCH_FAILED,
			"Could not start dotquay at %s." % executable,
			command_name)
	else:
		result = RESULT.parse(joined, code, command_name)

	result.ticket = ticket
	_finish.bind(result, thread).call_deferred()


## Runs on the main thread. Godot forbids touching most of the scene tree from a worker, and the
## panel this notifies is all scene tree.
func _finish(result: DotquayResult, thread: Thread) -> void:
	if thread.is_started():
		thread.wait_to_finish()
	_threads.erase(thread)

	_running = max(0, _running - 1)
	finished.emit(result)

	if _running == 0:
		busy_changed.emit(false)


func _emit_deferred(result: DotquayResult, ticket: int) -> void:
	# Deferred even on the "didn't run at all" path, so a caller's signal handling is identical for
	# every outcome — a synchronous emit here would fire before the caller had finished wiring up
	# (and before it has the ticket this call returns).
	result.ticket = ticket
	finished.emit.bind(result).call_deferred()
