extends RefCounted

## Covers [DotquayProcessRunner] — the wall-clock timeout wrapper around a real child process
## (P0-7).
##
## Every test here spawns an actual OS process; there is no fixture for "a process that hangs". The
## timeout test genuinely waits out its (short) timeout, so this suite takes a few hundred
## milliseconds longer than the rest of the plugin's tests combined — a cost worth paying, because a
## fixture-only suite could never have caught the concurrent-pipe-read bug this class's own doc
## comment describes; only running a real process against a real Godot build did.
##
## Cross-platform by construction (`cmd.exe` / `sh -c` per [method OS.get_name]), but only the
## Windows path has actually been run as of this writing — see docs/REVIEW-godot-plugin-public-
## release.md's P0-7 entry for exactly what that does and doesn't cover.

const PROCESS_RUNNER := preload("res://addons/dotquay/core/dotquay_process_runner.gd")

var runner: Object


## `cmd /c "<script>"` on Windows, `sh -c "<script>"` elsewhere — the one shell every supported
## platform already has, with no extra dependency for the test suite to assume.
static func _shell(script: String) -> Array:
	if OS.get_name() == "Windows":
		return ["cmd.exe", PackedStringArray(["/c", script])]
	return ["/bin/sh", PackedStringArray(["-c", script])]


func test_launch_failed_for_a_missing_executable() -> void:
	var missing := "C:\\definitely\\not\\a\\real\\path\\dotquay_test_missing.exe" if OS.get_name() == "Windows" \
		else "/definitely/not/a/real/path/dotquay_test_missing"

	var outcome: DotquayProcessRunner.Outcome = PROCESS_RUNNER.run(missing, PackedStringArray(), 2000)

	runner.check(outcome.launch_failed, "a nonexistent executable should report launch_failed")
	runner.check(not outcome.timed_out, "a launch failure is not a timeout")


func test_captures_output_and_the_real_exit_code_for_a_fast_process() -> void:
	var shell: Array = _shell("echo hello&exit 3" if OS.get_name() == "Windows" else "echo hello; exit 3")

	var outcome: DotquayProcessRunner.Outcome = PROCESS_RUNNER.run(shell[0], shell[1], 5000)

	runner.check(not outcome.launch_failed, "a real shell should start")
	runner.check(not outcome.timed_out, "a process that exits well inside the budget did not time out")
	runner.equal(outcome.exit_code, 3, "the real exit code should be reported, not a sentinel")
	runner.check(outcome.output.contains("hello"), "stdout written before exit should be captured")
	runner.check(outcome.elapsed_ms < 5000, "a fast process should not wait out the full timeout")


## The one test that actually proves the timeout, not just the plumbing around it: a process that
## would otherwise run for 6 real seconds is cut short at a fraction of that.
func test_kills_a_hanging_process_after_the_timeout() -> void:
	var shell: Array
	if OS.get_name() == "Windows":
		# `timeout /t` refuses to run without a real console attached to stdin; Start-Sleep has no
		# such requirement and is exactly as available on every supported Windows version.
		shell = ["powershell.exe", PackedStringArray(["-NoProfile", "-Command", "Start-Sleep -Seconds 6"])]
	else:
		shell = ["/bin/sh", PackedStringArray(["-c", "sleep 6"])]

	var outcome: DotquayProcessRunner.Outcome = PROCESS_RUNNER.run(shell[0], shell[1], 500)

	runner.check(outcome.timed_out, "a process that outlives its budget should be reported as timed out")
	runner.check(not outcome.launch_failed, "a process that started and then hung is not a launch failure")
	# Generous upper bound — this only needs to distinguish "the timeout fired" from "we waited out
	# the child's full 6-second sleep", not pin down exact scheduler timing on a loaded CI box.
	runner.check(outcome.elapsed_ms < 3000,
		"the timeout should end the wait well before the child's own 6s runtime (took %dms)" % outcome.elapsed_ms)


func test_on_started_callback_receives_a_real_pid() -> void:
	var shell: Array = _shell("exit 0")
	var seen_pid := [-1]

	PROCESS_RUNNER.run(shell[0], shell[1], 2000, func(pid: int) -> void: seen_pid[0] = pid)

	runner.check(seen_pid[0] > 0, "on_started should be called with a positive pid before the process exits")


## P1-3: a Linux/macOS binary missing its execute bit fails exactly like a bad path or a nonexistent
## file — there is nothing else to go on, so the hint has to live in this message.
func test_launch_failed_message_adds_a_chmod_hint_on_non_windows() -> void:
	var message := PROCESS_RUNNER.launch_failed_message("/opt/dotquay/dotquay", "Linux")

	runner.check(message.contains("chmod +x"), "non-Windows should hint at the execute bit")
	runner.check(message.contains("/opt/dotquay/dotquay"), "the message should still name the configured path")


func test_launch_failed_message_has_no_chmod_hint_on_windows() -> void:
	var message := PROCESS_RUNNER.launch_failed_message("C:\\dotquay\\dotquay.exe", "Windows")

	runner.check(not message.contains("chmod"), "chmod is not a Windows concept")
	runner.check(message.contains("C:\\dotquay\\dotquay.exe"), "the message should still name the configured path")
