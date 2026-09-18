@tool
class_name DotquayProcessRunner
extends RefCounted

## Runs a child process with a wall-clock timeout, killing it if it overruns (P0-7).
##
## [code]OS.execute()[/code] blocks the calling thread until the process exits, with no way to time
## it out — an interactive ssh host-key prompt on a first `git@host:` clone, or a half-open
## connection to a feed, hangs forever. That used to mean the whole editor, including at shutdown:
## [DotquayCli]'s worker thread never returned, so [code]_exit_tree[/code]'s join never returned
## either.
##
## This wraps [code]OS.execute_with_pipe()[/code] instead: it hands back a pid immediately, so the
## caller polls wall-clock time on its own thread and calls [method OS.kill] once the budget runs
## out, rather than being stuck inside a single blocking call with no way to interrupt it.
##
## Drains the child's pipes as it runs, rather than once it has exited. This is not an optimisation:
## an OS pipe buffer is small (4 KiB on Windows, measured on this build) and a child that fills it
## blocks inside its own `write` until somebody reads. Waiting for exit before reading therefore
## deadlocks outright on any command whose output crosses that threshold — the process never
## exits, the wall-clock budget below kills it, and the only output ever recovered is the first
## 4 KiB, which then fails to parse as JSON. `doctor` on a project with a few dozen findings is
## already past it.
##
## An earlier version of this class read only after [method OS.is_process_running] went false, on
## the measurement that a concurrent read "returns an empty string, output and all". That reading
## was wrong: on Windows a pipe's [method FileAccess.get_length] is a `PeekNamedPipe` of the bytes
## buffered *at that instant*, so an early read returns "" simply because the child has not written
## yet — not because reading broke it. Accumulating across polls returns everything, and keeps the
## buffer empty enough that the child never blocks. This class exists to keep that ordering
## impossible to get wrong at the call site.


const DEFAULT_POLL_INTERVAL_MS := 20


## One run's outcome. [member exit_code] is only meaningful when neither [member launch_failed] nor
## [member timed_out] is true.
class Outcome extends RefCounted:
	## True when the process never started at all (bad path, not executable, OS refused to spawn
	## it). Distinct from [member timed_out]: a caller that treats the two the same reports "could
	## not start dotquay" for a process that started fine and just ran too long.
	var launch_failed: bool = false
	## True when the wall-clock budget ran out before the process exited on its own, and it was
	## killed as a result.
	var timed_out: bool = false
	var exit_code: int = -1
	## Combined stdout + stderr, in that order. Whatever the process managed to write before it
	## exited or was killed — a killed process commonly has nothing at all, which is not itself an
	## error; it just means there is no extra detail to show beyond "timed out".
	var output: String = ""
	var elapsed_ms: int = 0


## Runs [param executable] with [param args] and returns once it exits or [param timeout_ms]
## elapses, whichever comes first. Blocks the calling thread for up to [param timeout_ms] — call
## this from a background [Thread], never from the main thread.
static func run(executable: String, args: PackedStringArray, timeout_ms: int,
		on_started: Callable = Callable()) -> Outcome:
	var start := Time.get_ticks_msec()
	var outcome := Outcome.new()

	# Godot 4.7 signals a launch failure by returning an empty Dictionary — no "pid" key, nothing to
	# poll or kill. Confirmed against this build: a bad path prints an engine-level error and hands
	# back {}.
	var handle: Dictionary = OS.execute_with_pipe(executable, args)
	if handle.is_empty() or not handle.has("pid"):
		outcome.launch_failed = true
		outcome.elapsed_ms = Time.get_ticks_msec() - start
		return outcome

	var pid: int = handle["pid"]
	if on_started.is_valid():
		on_started.call(pid)

	var stdio: FileAccess = handle.get("stdio")
	var stderr: FileAccess = handle.get("stderr")
	# Bytes, not strings: a multi-byte character can straddle two drains, so only the joined bytes are
	# safe to decode as UTF-8.
	var stdout_bytes := PackedByteArray()
	var stderr_bytes := PackedByteArray()

	while OS.is_process_running(pid):
		if Time.get_ticks_msec() - start >= timeout_ms:
			outcome.timed_out = true
			OS.kill(pid)
			break

		# Both pipes, every poll — see the class doc comment. Draining only stdout would leave a
		# command that writes its detail to stderr deadlocked in exactly the same way.
		stdout_bytes.append_array(_drain(stdio))
		stderr_bytes.append_array(_drain(stderr))

		OS.delay_msec(DEFAULT_POLL_INTERVAL_MS)

	# Whatever is still buffered now the process has stopped, which for a process that exited inside
	# the first poll interval is all of it.
	stdout_bytes.append_array(_drain(stdio))
	stderr_bytes.append_array(_drain(stderr))

	outcome.output = stdout_bytes.get_string_from_utf8() + stderr_bytes.get_string_from_utf8()
	outcome.exit_code = -1 if outcome.timed_out else OS.get_process_exit_code(pid)
	outcome.elapsed_ms = Time.get_ticks_msec() - start
	return outcome


## The bytes buffered in [param pipe] right now, empty when there are none (or there is no pipe).
##
## [method FileAccess.get_length] first, and never [method FileAccess.get_as_text]: on Windows that
## length is a `PeekNamedPipe` of what is available at this instant, so asking for exactly that many
## returns immediately, while `get_as_text` reads towards EOF and blocks until the child closes its
## end. Called from inside the poll loop the latter never returns at all, which takes the wall-clock
## timeout down with it — a worse failure than the deadlock this drain exists to avoid. Measured on
## Godot 4.7.2 (Windows): this also still returns what a process left buffered after it exited, so
## the fast path that reads only once, after the loop, loses nothing.
static func _drain(pipe: FileAccess) -> PackedByteArray:
	if pipe == null:
		return PackedByteArray()

	var available := pipe.get_length()
	return pipe.get_buffer(available) if available > 0 else PackedByteArray()


## Builds the message for a launch that produced no process at all — a bad path, something not
## executable, or, on Linux/macOS, a real binary just missing its execute bit. [method run] cannot
## tell these apart ([code]OS.execute_with_pipe[/code] fails the same way for all three), and
## [FileAccess.file_exists] cannot see the execute bit at all — so a `chmod +x` binary silently fails
## right here with nothing before this point ever having flagged it (P1-3).
##
## Takes [param platform] rather than reading [method OS.get_name] itself so both branches — with and
## without the chmod hint — are directly testable without depending on the platform actually running
## the test.
static func launch_failed_message(executable: String, platform: String) -> String:
	var message := "Could not start dotquay at %s." % executable
	if platform != "Windows":
		message += " If this is a binary you downloaded, make sure it's executable: chmod +x \"%s\"" % executable
	return message
