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
## Deliberately does not read the process's output pipes while it is still running. Measured against
## a real build on Windows (Godot 4.7.2): reading concurrently with the poll loop — even from a
## second thread dedicated to nothing else — silently returns an empty string, output and all,
## regardless of what the child actually wrote. Reading only after [method OS.is_process_running]
## has already gone false (whether by natural exit or by [method OS.kill], which closes the child's
## pipes and unblocks a pending read in single-digit milliseconds on that same platform) reliably
## returns everything the process wrote. This class exists specifically to keep that ordering
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

	while OS.is_process_running(pid):
		if Time.get_ticks_msec() - start >= timeout_ms:
			outcome.timed_out = true
			OS.kill(pid)
			break
		OS.delay_msec(DEFAULT_POLL_INTERVAL_MS)

	# Read only now that the process has stopped one way or another — see the class doc comment.
	var stdio: FileAccess = handle.get("stdio")
	var stderr: FileAccess = handle.get("stderr")
	var stdout_text := stdio.get_as_text() if stdio else ""
	var stderr_text := stderr.get_as_text() if stderr else ""

	outcome.output = stdout_text + stderr_text
	outcome.exit_code = -1 if outcome.timed_out else OS.get_process_exit_code(pid)
	outcome.elapsed_ms = Time.get_ticks_msec() - start
	return outcome


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
