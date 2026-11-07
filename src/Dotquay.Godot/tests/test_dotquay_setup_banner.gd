extends RefCounted

## Covers [DotquaySetupBanner] — pure banner-state decisions for the first-run / broken-CLI case
## (P0-2, P0-4). No Control, no EditorSettings, no CLI: every state here is reachable from a bare
## error_kind string, which is the whole point of pulling this logic out of the panel.

const SETUP_BANNER := preload("res://addons/dotquay/core/dotquay_setup_banner.gd")
const RESULT := preload("res://addons/dotquay/core/dotquay_result.gd")

var runner: Object


func test_not_configured_shows_get_dotquay_browse_and_locate() -> void:
	var decision: DotquaySetupBanner.Decision = SETUP_BANNER.decide(DotquayResult.KIND_NOT_CONFIGURED)

	runner.check(decision.visible, "an unconfigured CLI should show the banner")
	runner.check(decision.show_get_dotquay, "not configured is exactly the first-run case Get dotquay is for")
	runner.check(decision.show_browse, "browse should be offered alongside PATH discovery")
	runner.check(decision.show_locate, "Find on PATH should still be offered as a retry")
	runner.check(not decision.show_init, "not configured is not a missing-manifest problem")
	runner.check(not decision.message.is_empty(), "should carry the plain explanation, not an empty string")


func test_no_manifest_only_offers_init() -> void:
	var decision: DotquaySetupBanner.Decision = SETUP_BANNER.decide(DotquayWire.KIND_NO_MANIFEST)

	runner.check(decision.visible, "no manifest should show the banner")
	runner.check(decision.show_init, "no manifest should offer Create dotquay.toml")
	runner.check(not decision.show_get_dotquay, "dotquay is already working; there is nothing to download")
	runner.check(not decision.show_browse, "the CLI path is not the problem here")
	runner.check(not decision.show_locate, "the CLI path is not the problem here")


func test_no_manifest_message_names_the_file_in_plain_language() -> void:
	# P1-5: a first-time user has never heard the word "manifest" — the message has to say what
	# dotquay.toml actually is, by name, rather than describing it with jargon.
	var decision: DotquaySetupBanner.Decision = SETUP_BANNER.decide(DotquayWire.KIND_NO_MANIFEST)

	runner.check(decision.message.contains("dotquay.toml"), "the message should name the file: %s" % decision.message)
	runner.check(not decision.message.to_lower().contains("manifest"),
		"the message should not rely on the word 'manifest': %s" % decision.message)


func test_launch_failed_offers_browse_and_locate_but_not_get_dotquay() -> void:
	var decision: DotquaySetupBanner.Decision = SETUP_BANNER.decide(DotquayResult.KIND_LAUNCH_FAILED)

	runner.check(decision.visible, "a launch failure should show the banner")
	runner.check(decision.show_browse, "browsing to the right executable should fix a wrong path")
	runner.check(decision.show_locate, "PATH discovery might also find the right one")
	runner.check(not decision.show_get_dotquay,
		"something is already at the configured path; downloading a new copy is not obviously the fix")
	runner.equal(decision.message, "", "the panel supplies the real message from the CLI result for this kind")


func test_bad_output_offers_browse_and_locate() -> void:
	var decision: DotquaySetupBanner.Decision = SETUP_BANNER.decide(DotquayResult.KIND_BAD_OUTPUT)

	runner.check(decision.visible, "bad output should show the banner")
	runner.check(decision.show_browse, "browsing to a different executable should fix a wrong-tool path")
	runner.check(decision.show_locate, "PATH discovery might also find the right one")
	runner.check(not decision.show_get_dotquay, "something answered; it's just not dotquay or not JSON")


func test_schema_mismatch_offers_neither_retry_since_both_would_refind_the_same_binary() -> void:
	var decision: DotquaySetupBanner.Decision = SETUP_BANNER.decide(DotquayResult.KIND_SCHEMA_MISMATCH)

	runner.check(decision.visible, "a schema mismatch should show the banner")
	runner.check(not decision.show_locate, "re-discovering PATH would find the same differently-versioned binary")
	runner.check(not decision.show_browse, "browsing would land back on the same file the user already pointed at")
	runner.check(not decision.show_get_dotquay, "a real binary is already there; only its version is wrong")


func test_ordinary_cli_errors_do_not_show_any_banner() -> void:
	# A conflict, an auth failure, a git error and so on are normal operational outcomes reported in
	# the log — not a "the CLI itself is unusable" state, and showing the setup banner for one of
	# these would hide the graph/sidebar for no reason.
	var decision: DotquaySetupBanner.Decision = SETUP_BANNER.decide(DotquayWire.KIND_WORKFLOW)

	runner.check(not decision.visible, "an ordinary CLI-reported error should not show the setup banner")
	runner.check(not decision.show_get_dotquay, "hidden state should not offer any of its buttons")
	runner.check(not decision.show_browse, "hidden state should not offer any of its buttons")
	runner.check(not decision.show_locate, "hidden state should not offer any of its buttons")
	runner.check(not decision.show_init, "hidden state should not offer any of its buttons")


func test_a_successful_result_with_no_error_kind_hides_the_banner() -> void:
	# _update_setup_banner runs for every result, success included, where error_kind defaults to "".
	var decision: DotquaySetupBanner.Decision = SETUP_BANNER.decide("")

	runner.check(not decision.visible, "an empty error_kind (a successful result) should not show any banner")


func test_invalid_project_file_explains_and_appends_the_cli_message() -> void:
	# Every read fails the same way until the file is fixed by hand, so it is the banner's job; the
	# CLI's message is what names the file and the parser's (line,column), so it goes under the lead.
	var decision: DotquaySetupBanner.Decision = SETUP_BANNER.decide(DotquayWire.KIND_INVALID_PROJECT_FILE)

	runner.check(decision.visible, "a project file that can't be read should show the banner")
	runner.check(decision.append_cli_message, "the CLI's message names the file and line, so it must be shown")
	runner.check(decision.message.contains("Refresh"), "the message should say how to retry: %s" % decision.message)
	runner.check(not decision.show_init, "the project has a dotquay.toml; creating one is not the fix")
	runner.check(not decision.show_get_dotquay, "dotquay is working; there is nothing to download")
	runner.check(not decision.show_browse, "the CLI path is not the problem here")
	runner.check(not decision.show_locate, "the CLI path is not the problem here")


func test_invalid_config_file_says_fix_or_delete_and_appends_the_cli_message() -> void:
	# A broken ~/.dotquay/credentials.toml used to arrive as `auth`, whose hint is `dotquay login` —
	# which reads the same file and fails the same way. The CLI's message names the file.
	var decision: DotquaySetupBanner.Decision = SETUP_BANNER.decide(DotquayWire.KIND_INVALID_CONFIG_FILE)

	runner.check(decision.visible, "a user-level file that can't be read should show the banner")
	runner.check(decision.append_cli_message, "the CLI's message names the file, so it must be shown")
	runner.check(decision.message.to_lower().contains("fix or delete"), "the message should say what to do: %s" % decision.message)
	runner.check(not decision.message.contains("dotquay login"), "logging in reads the same broken file")
	runner.check(not decision.show_init, "the project's own files are fine; creating one is not the fix")
	runner.check(not decision.show_get_dotquay, "dotquay is working; there is nothing to download")
	runner.check(not decision.show_browse, "the CLI path is not the problem here")
	runner.check(not decision.show_locate, "the CLI path is not the problem here")


func test_invalid_config_file_gets_no_login_hint() -> void:
	runner.equal(RESULT.auth_hint(DotquayWire.KIND_INVALID_CONFIG_FILE), "",
		"a broken credentials file is not fixed by logging in")


func test_only_unreadable_files_append_the_cli_message() -> void:
	for kind in [DotquayWire.KIND_NO_MANIFEST, DotquayResult.KIND_NOT_CONFIGURED, DotquayResult.KIND_LAUNCH_FAILED]:
		var decision: DotquaySetupBanner.Decision = SETUP_BANNER.decide(kind)
		runner.check(not decision.append_cli_message, "%s has its own complete message" % kind)
