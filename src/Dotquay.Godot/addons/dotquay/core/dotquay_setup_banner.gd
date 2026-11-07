@tool
class_name DotquaySetupBanner
extends RefCounted

## Decides what the first-run / broken-CLI banner shows, from a [DotquayResult]'s error kind alone
## (P0-2, P0-4).
##
## Pulled out of [DotquayPanel] as a pure function so the banner's five states — not configured, a
## wrong path or broken binary, a schema mismatch, no manifest, and a project file that can't be
## read — can be tested without
## instantiating the panel scene, an [EditorSettings], or the CLI: nothing here is a [Control].

const NOT_CONFIGURED_MESSAGE := (
	"dotquay is a separate program this tab talks to — it isn't installed, or this editor can't "
	+ "find it. Get it below, or point this tab at it if you already have it.")
const NO_MANIFEST_MESSAGE := (
	"dotquay keeps the list of your addons and packages in a file called dotquay.toml. This project "
	+ "doesn't have one yet.")
const INVALID_PROJECT_FILE_MESSAGE := (
	"One of this project's dotquay files can't be read, so nothing here can be shown or changed until "
	+ "it's fixed. Correct the file at the line named below (or restore it from version control), "
	+ "then press Refresh.")


## One banner state. Plain fields rather than a signal or a Dictionary: [DotquayPanel] reads every one
## of them straight into a Control property, so nothing is gained from a more dynamic shape.
class Decision extends RefCounted:
	var visible: bool = false
	## The message to show, or "" when the caller (which has the actual [DotquayResult]) needs to
	## supply it — a wrong-path/schema-mismatch banner shows the CLI's own real error message, which
	## this function, given only an error kind, has no way to know.
	var message: String = ""
	## "Get dotquay…" — opens the release page. Offered only when nothing is configured at all: a
	## fresh download is not obviously the fix when something is already sitting at the configured
	## path (a schema mismatch, a crash) — that binary needs replacing or its path needs correcting,
	## not a second copy fetched blind.
	var show_get_dotquay: bool = false
	## "Browse…" — lets the user point `cli_path` at an executable by hand. Offered everywhere
	## automatic PATH discovery ([member show_locate]) is, since both are ways to fix "the configured
	## path is wrong"; unlike discovery, browsing can also find dotquay somewhere no probe would guess.
	var show_browse: bool = false
	var show_locate: bool = false
	var show_init: bool = false
	## Whether the CLI's own error_message goes under [member message]. Set where the lead sentence
	## says what happened but only the CLI knows where: a broken project file, whose message names the
	## file and the parser's (line,column).
	var append_cli_message: bool = false


static func decide(error_kind: String) -> Decision:
	var decision := Decision.new()

	var not_configured := error_kind == DotquayResult.KIND_NOT_CONFIGURED
	var no_manifest := error_kind == DotquayWire.KIND_NO_MANIFEST
	# Every read the panel makes fails the same way until the file is fixed by hand, so this is a
	# project-level state for the banner, not one more red line in the log.
	var invalid_project_file := error_kind == DotquayWire.KIND_INVALID_PROJECT_FILE
	# A wrong or stale CLI path — pointed at nothing, at something that isn't dotquay, or at a
	# crashed process — produces one of these three kinds. Burying that in the 140px log leaves the
	# graph and sidebar blank with no visible explanation, same as an unconfigured path (P0-4).
	var cli_unusable := (
		error_kind == DotquayResult.KIND_LAUNCH_FAILED
		or error_kind == DotquayResult.KIND_BAD_OUTPUT
		or error_kind == DotquayResult.KIND_SCHEMA_MISMATCH)

	decision.visible = not_configured or no_manifest or invalid_project_file or cli_unusable
	if not decision.visible:
		return decision

	# A schema mismatch is a real, differently-versioned dotquay sitting at the right path —
	# re-running PATH discovery, or browsing back to that same file, would just re-offer a "fix" that
	# fixes nothing.
	var retry_applies := not_configured or (cli_unusable and error_kind != DotquayResult.KIND_SCHEMA_MISMATCH)

	decision.show_locate = retry_applies
	decision.show_browse = retry_applies
	decision.show_get_dotquay = not_configured
	decision.show_init = no_manifest

	if no_manifest:
		decision.message = NO_MANIFEST_MESSAGE
	elif invalid_project_file:
		decision.message = INVALID_PROJECT_FILE_MESSAGE
		decision.append_cli_message = true
	elif not_configured:
		decision.message = NOT_CONFIGURED_MESSAGE
	# else: cli_unusable — left "" for the caller to fill from the CLI's own error_message.

	return decision
