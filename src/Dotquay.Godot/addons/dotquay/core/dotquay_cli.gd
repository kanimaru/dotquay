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

var _threads: Array[Thread] = []
var _running := 0

## pid -> true, for every child process a worker thread is currently waiting on. Written from a
## worker thread the moment [DotquayProcessRunner] reports one has started, read from the main thread by
## [method _exit_tree]; [member _pids_mutex] is what makes that safe. Existing rather than derived
## from [member _threads] because a [Thread] doesn't expose the pid of whatever it's blocked on.
var _running_pids: Dictionary = {}
var _pids_mutex := Mutex.new()

## Queued calls to [method run] made while another run is already executing, in FIFO order. Each
## entry is a Dictionary of the same arguments [method _start] needs (`executable`, `full_args`,
## `command_name`, `ticket`, `timeout_ms`).
##
## This serializes *every* run, not just the mutating ones (`install`/`update`/`link`) that actually
## race on `dotquay.lock`/`addons/`. Splitting mutating from read-only commands would need a table
## this file doesn't otherwise maintain, and would need to stay in sync with the CLI's own command
## list forever. dotquay itself is fast enough that full serialization costs nothing visible: a
## `graph` refresh queued behind an `update` is a sub-second delay, not a UX problem.
var _pending: Array = []

## Handed out by [method run], one per call, and echoed back on the result as
## [member DotquayResult.ticket]. It is the only reliable way to match a result to the call that
## produced it: the envelope's `command` is the CLI's name for what ran ("override" for both a pin and
## a `--clear`), and several calls can be in flight at once.
var _next_ticket := 1


func _exit_tree() -> void:
	# Joining rather than abandoning: a half-finished `dotquay update` is writing to addons/ and the
	# lockfile, and letting the editor tear the script out from under it is how a project ends up in
	# a state neither the lockfile nor disk describes. [DotquayProcessRunner] already bounds every worker
	# thread to the configured command timeout on its own, so this join can no longer hang forever —
	# but the configured timeout can legitimately be minutes, which is still an unacceptable wait for
	# someone who just clicked "close editor". Killing every still-running child directly, here,
	# first, is what makes the join fast instead of merely finite: [DotquayProcessRunner]'s poll loop
	# notices the kill within one poll interval and returns immediately afterwards (measured
	# single-digit milliseconds on Windows — see [DotquayProcessRunner]'s own doc comment).
	_pids_mutex.lock()
	var pids: Array = _running_pids.keys()
	_pids_mutex.unlock()

	for pid in pids:
		OS.kill(pid)

	for thread in _threads:
		if thread.is_started():
			thread.wait_to_finish()
	_threads.clear()
	_running = 0
	_running_pids.clear()
	# Never started, so there's nothing to join — they simply never happened.
	_pending.clear()


func is_busy() -> bool:
	return _running > 0 or not _pending.is_empty()


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
	var executable := DotquaySettings.cli_path(editor_settings)
	# Read here, on the main thread, alongside the executable path — EditorSettings is a main-thread
	# API like the rest of the editor, so the resolved millisecond value (not the EditorSettings
	# object itself) is what crosses into the worker thread.
	var timeout_ms := DotquaySettings.command_timeout_ms(editor_settings)

	if executable.is_empty():
		_emit_deferred(DotquayResult.failure(
			DotquayResult.KIND_NOT_CONFIGURED,
			("dotquay isn't installed, or this editor can't find it. Set %s, or use the Get dotquay / "
				+ "Browse buttons above.") % DotquaySettings.breadcrumb(DotquaySettings.CLI_PATH_SETTING),
			command_name), ticket)
		return ticket

	# A folder, a .dll, or nothing at all — the mistakes a newbie commonly ends up with before ever
	# spawning it (P1-2).
	var problem := DotquaySettings.path_problem(executable)
	if not problem.is_empty():
		_emit_deferred(DotquayResult.failure(DotquayResult.KIND_NOT_CONFIGURED, problem, command_name), ticket)
		return ticket

	var full_args := PackedStringArray(args)
	full_args.append("--json")
	full_args.append("--project")
	full_args.append(project_directory())

	# A run is already executing: queue this one instead of racing it against that thread over
	# dotquay.lock/addons/. The ticket is still handed back immediately either way — callers don't
	# need to know (or care) whether their call started right away or is waiting in line.
	var was_busy := is_busy()
	if _running > 0:
		_pending.append({
			"executable": executable,
			"full_args": full_args,
			"command_name": command_name,
			"ticket": ticket,
			"timeout_ms": timeout_ms,
		})
	else:
		_start(executable, full_args, command_name, ticket, timeout_ms)

	if not was_busy:
		busy_changed.emit(true)

	return ticket


## Starts a thread for one call, whether it's running immediately from [method run] or being pulled
## off [member _pending] by [method _finish].
func _start(executable: String, full_args: PackedStringArray, command_name: String, ticket: int,
		timeout_ms: int) -> void:
	_running += 1
	var thread := Thread.new()
	_threads.append(thread)
	thread.start(_run_blocking.bind(executable, full_args, command_name, ticket, timeout_ms, thread))


## The Godot project's directory on the real filesystem. The CLI has no idea what `res://` means, and
## the editor's own working directory is not the project, so every invocation passes `--project`
## explicitly (SPEC.md §17) rather than relying on the CLI's cwd.
func project_directory() -> String:
	return ProjectSettings.globalize_path("res://").rstrip("/")


func _run_blocking(executable: String, args: PackedStringArray, command_name: String, ticket: int,
		timeout_ms: int, thread: Thread) -> void:
	# Registered the moment the child actually has a pid, so [method _exit_tree] can kill it even
	# while this thread is still inside [method DotquayProcessRunner.run]'s own wait loop. Unregistered
	# again right below, once that loop returns for any reason — a pid [method _exit_tree] no longer
	# needs to know about, whether it exited on its own, timed out, or was already killed.
	#
	# `started_pid_box` is a one-element Array rather than a plain local so the value written inside
	# the lambda is visible after it returns regardless of GDScript's closure-capture rules for a bare
	# int — an Array is a reference type, so both sides are looking at the same one.
	var started_pid_box := [-1]
	var on_started := func(pid: int) -> void:
		started_pid_box[0] = pid
		_pids_mutex.lock()
		_running_pids[pid] = true
		_pids_mutex.unlock()

	var outcome: DotquayProcessRunner.Outcome = DotquayProcessRunner.run(executable, args, timeout_ms, on_started)

	var started_pid: int = started_pid_box[0]
	if started_pid != -1:
		_pids_mutex.lock()
		_running_pids.erase(started_pid)
		_pids_mutex.unlock()

	var result: DotquayResult
	if outcome.launch_failed:
		result = DotquayResult.failure(
			DotquayResult.KIND_LAUNCH_FAILED,
			DotquayProcessRunner.launch_failed_message(executable, OS.get_name()),
			command_name,
			outcome.output)
	elif outcome.timed_out:
		result = DotquayResult.failure(
			DotquayResult.KIND_TIMEOUT,
			"dotquay did not finish within %ds and was stopped. If this keeps happening on a slow " % (timeout_ms / 1000)
				+ "connection, raise the timeout in %s." % DotquaySettings.breadcrumb(DotquaySettings.COMMAND_TIMEOUT_SETTING),
			command_name,
			outcome.output)
	else:
		result = DotquayResult.parse(outcome.output, outcome.exit_code, command_name)

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

	if not _pending.is_empty():
		var next: Dictionary = _pending.pop_front()
		_start(next["executable"], next["full_args"], next["command_name"], next["ticket"], next["timeout_ms"])
	elif _running == 0:
		busy_changed.emit(false)


func _emit_deferred(result: DotquayResult, ticket: int) -> void:
	# Deferred even on the "didn't run at all" path, so a caller's signal handling is identical for
	# every outcome — a synchronous emit here would fire before the caller had finished wiring up
	# (and before it has the ticket this call returns).
	result.ticket = ticket
	finished.emit.bind(result).call_deferred()
