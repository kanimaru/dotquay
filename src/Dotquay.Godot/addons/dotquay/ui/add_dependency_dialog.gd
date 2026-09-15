@tool
class_name DotquayAddDependencyDialog
extends ConfirmationDialog

## The "add a dependency" form — the GUI equivalent of `dotquay add <git-url | nuget-id>`.
##
## Two source kinds share one dialog because they share one command: dotquay routes a git URL and a
## NuGet package ID through the same `add`, and splitting them into two dialogs would make a user
## choose a menu item before they've decided what they're adding.
##
## The form's job is to make only combinations `add` accepts reachable (SPEC.md §6.2). Git-only
## fields are hidden for NuGet rather than disabled, branch and commit are absent from the pin
## dropdown for a package that has neither, and a URL that already names a ref locks the dropdown
## instead of letting the user state the source twice. Every one of those is a `WorkflowException`
## the CLI would otherwise raise after the dialog had closed.

signal add_requested(request: DotquayAddRequest)

enum SourceKind { GIT, NUGET }

@onready var _kind: OptionButton = %Kind
@onready var _source: LineEdit = %Source
@onready var _mode_row: HBoxContainer = %ModeRow
@onready var _mode: OptionButton = %SourceMode
@onready var _value: LineEdit = %SourceValue
@onready var _path_row: HBoxContainer = %PathRow
@onready var _path: LineEdit = %Path
@onready var _install_as_row: HBoxContainer = %InstallAsRow
@onready var _install_as: LineEdit = %InstallAs
@onready var _hint: Label = %Hint
@onready var _error: Label = %Error

## What [method DotquayAddRequest.diagnose_source_shape] made of the current [member _source] text.
## Recomputed only when that text changes, not on every [method _refresh] — a kind/mode change never
## alters the source string, so re-diagnosing it then would just repeat the same answer.
var _shape_fix := DotquayAddRequest.ShapeFix.new()

## Set while a diagnosed fix is being written back into the form's own fields, so that write's
## resulting `text_changed` (if Godot ever fires one for a script-driven change) is not re-diagnosed
## as if the user had pasted the corrected URL a second time.
var _applying_shape_fix := false


func _ready() -> void:
	_kind.item_selected.connect(func(_index: int) -> void: _refresh())
	_mode.item_selected.connect(func(_index: int) -> void: _refresh())
	_source.text_changed.connect(_on_source_changed)
	# The pin value decides whether OK is allowed at all (a "Version" with nothing typed is not a
	# request), so the OK state has to follow it keystroke by keystroke.
	_value.text_changed.connect(func(_text: String) -> void: _refresh())
	confirmed.connect(_on_confirmed)

	_rebuild_modes(true)
	_refresh()


func open() -> void:
	_source.text = ""
	_value.text = ""
	_path.text = ""
	_install_as.text = ""
	_kind.select(SourceKind.GIT)
	_shape_fix = DotquayAddRequest.ShapeFix.new()

	_rebuild_modes(true)
	_select_mode(DotquayAddRequest.Source.LATEST)
	_refresh()

	popup_centered()
	_source.grab_focus()


## Re-opens the dialog after a failed `add` or `add --preview` (P1-9), with the request that failed
## still filled in and the CLI's own explanation shown above the buttons. The user's typed values
## are the thing worth preserving here — retyping a git URL, a branch, and a subtree from memory
## because one flag was wrong is a worse experience than the error itself.
func reopen_after_failure(request: DotquayAddRequest, error_message: String) -> void:
	_kind.select(SourceKind.GIT if request.is_git else SourceKind.NUGET)
	_rebuild_modes(request.is_git)

	_source.text = request.source
	_select_mode(request.source_mode)
	_value.text = request.source_value
	_path.text = request.path
	_install_as.text = request.install_as
	_shape_fix = DotquayAddRequest.ShapeFix.new()

	# Runs after the fields are set, not before: _refresh() is what would otherwise clear _error the
	# instant this reopen happened, since it treats any recompute as evidence of an edit.
	_refresh()

	_error.text = error_message
	_error.visible = not error_message.is_empty()

	popup_centered()
	_source.grab_focus()


# ---------------------------------------------------------------------------------------------
# Form state
# ---------------------------------------------------------------------------------------------

func _is_git() -> bool:
	return _kind.selected == SourceKind.GIT


## Rebuilt per source kind rather than disabling rows, because a NuGet package has no branch and no
## commit — offering them greyed out invites the question of what they would have done.
##
## Items carry explicit ids so the mapping to [enum DotquayAddRequest.Source] survives the rebuild;
## with a shorter git-less list, positions would silently mean something different.
func _rebuild_modes(is_git: bool) -> void:
	var previous := _selected_mode()

	_mode.clear()
	_mode.add_item("Latest release", DotquayAddRequest.Source.LATEST)
	_mode.add_item("Version", DotquayAddRequest.Source.VERSION)

	if is_git:
		_mode.add_item("Branch", DotquayAddRequest.Source.BRANCH)
		_mode.add_item("Commit", DotquayAddRequest.Source.REV)

	_select_mode(previous)


## Falls back to "latest release" when the previous choice no longer exists — which is what switching
## from a git branch to a NuGet package means.
func _select_mode(mode: DotquayAddRequest.Source) -> void:
	var index := _mode.get_item_index(mode)
	_mode.select(index if index >= 0 else _mode.get_item_index(DotquayAddRequest.Source.LATEST))


func _selected_mode() -> DotquayAddRequest.Source:
	if _mode.item_count == 0 or _mode.selected < 0:
		return DotquayAddRequest.Source.LATEST

	return _mode.get_selected_id() as DotquayAddRequest.Source


func _on_source_changed(text: String) -> void:
	if _applying_shape_fix:
		return

	# Pasting a git URL while "NuGet" is selected is a near-certain mis-click, and dotquay would
	# route it by shape anyway — switching the dropdown keeps the form honest about what will happen.
	if DotquayAddRequest.looks_like_git(text) and not _is_git():
		_kind.select(SourceKind.GIT)

	_shape_fix = DotquayAddRequest.diagnose_source_shape(text)

	# A tree URL is corrected in place rather than merely explained (P1-8): the branch and subtree it
	# names are exactly what the Pin-to and Subtree fields ask for, so filling them is the difference
	# between the dialog doing the parsing and the user doing it by hand from the explanation text.
	if _shape_fix.has_fix():
		_applying_shape_fix = true
		_kind.select(SourceKind.GIT)
		_source.text = _shape_fix.url
		_source.caret_column = _shape_fix.url.length()

		if not _shape_fix.branch.is_empty():
			_rebuild_modes(true)
			_select_mode(DotquayAddRequest.Source.BRANCH)
			_value.text = _shape_fix.branch

		if not _shape_fix.path.is_empty():
			_path.text = _shape_fix.path

		_applying_shape_fix = false

	_refresh()


## One function recomputes the whole form, so there is no ordering between "kind changed" and "the
## URL grew a #ref" for the two to disagree about.
func _refresh() -> void:
	# Any recompute is evidence the user is editing again — the stale error from a previous failure
	# stops applying the moment they touch anything (P1-9). reopen_after_failure sets it back after
	# calling this, so the reopen itself doesn't immediately erase what it just showed.
	_error.visible = false

	var is_git := _is_git()

	if (_mode.item_count > 2) != is_git:
		_rebuild_modes(is_git)

	# The subtree and the install folder are facts about a git repo's layout (SPEC.md §7.1, OQ-2).
	# NuGet has no equivalent, and `add` rejects the flags outright rather than ignoring them.
	_path_row.visible = is_git
	_install_as_row.visible = is_git

	var request := _build_request()
	var inline_ref := request.inline_ref()
	var pinned := not inline_ref.is_empty()

	# A URL ending in #main already names the source. Letting the dropdown also name one produces a
	# command `add` refuses ("already names a ref, so --version/--branch/--rev would contradict it"),
	# so the dropdown steps aside and says why.
	_mode.disabled = pinned
	_value.editable = not pinned
	_value.visible = not pinned and _selected_mode() != DotquayAddRequest.Source.LATEST
	_value.placeholder_text = _value_placeholder()

	_source.placeholder_text = (
		"https://github.com/nathanhoad/godot_dialogue_manager" if is_git else "Newtonsoft.Json")

	# A shape diagnosis pre-empts the ordinary hint: it's about the exact text sitting in the field
	# right now, which is more specific than anything _hint_text can say from the mode alone.
	_hint.text = (
		_shape_fix.explanation if not _shape_fix.explanation.is_empty()
		else _hint_text(is_git, inline_ref))

	var incomplete := not request.is_complete() or _shape_fix.blocks_submit

	# Disabled, not silently ignored on confirm: a ConfirmationDialog hides itself before `confirmed`
	# fires, so a handler that bails out leaves the user looking at a closed dialog that did nothing.
	get_ok_button().disabled = incomplete

	# A disabled button that doesn't say why just reads as broken (P1-8). The tooltip is the one place
	# left to say it once the reason has already claimed the hint label for something else.
	get_ok_button().tooltip_text = _disabled_reason(request) if incomplete else ""


func _value_placeholder() -> String:
	match _selected_mode():
		DotquayAddRequest.Source.VERSION:
			return "~2.5 for a range, or 2.5.1 for one exact release" if _is_git() else "13.0.3"
		DotquayAddRequest.Source.BRANCH:
			return "main"
		DotquayAddRequest.Source.REV:
			return "a full commit SHA"
		_:
			return ""


func _hint_text(is_git: bool, inline_ref: String) -> String:
	if not is_git:
		return ("The latest stable release is written into your .csproj as a PackageReference and "
			+ "restored with dotnet.")

	if not inline_ref.is_empty():
		return ("The URL already names '%s', so dotquay will ask the remote whether that is a branch, "
			% inline_ref
			+ "a tag, or a commit. Remove it from the URL to pin from here instead.")

	match _selected_mode():
		DotquayAddRequest.Source.VERSION:
			return ("Matched against the repository's tags. A range like ~2.5 keeps up with patches; "
				+ "an exact tag pins to it.")
		DotquayAddRequest.Source.BRANCH:
			return ("Tracks a branch. The lockfile still pins the resolved commit, so teammates get "
				+ "exactly what you tested — 'Update' is what moves it forward.")
		DotquayAddRequest.Source.REV:
			return "Pinned to one commit, and never moved by 'Update'."
		_:
			return ("dotquay picks the newest release tag, or pins the default branch's tip if the "
				+ "repository has no tags. It resolves the graph and shows you what it would install "
				+ "before anything is written.")


## Explains why OK is disabled for [param request], as the button's tooltip (P1-8). Checked in the
## same order [method DotquayAddRequest.is_complete] would fail them, so the reason always names the
## thing actually missing rather than a generic "form incomplete".
func _disabled_reason(request: DotquayAddRequest) -> String:
	if _shape_fix.blocks_submit:
		return _shape_fix.explanation

	if request.source.strip_edges().is_empty():
		return "Type a git URL or NuGet package ID first."

	return "Enter a value to pin to, or set \"Pin to\" back to \"Latest release\"."


func _build_request() -> DotquayAddRequest:
	var request := DotquayAddRequest.new()
	request.source = _source.text.strip_edges()
	request.is_git = _is_git()
	request.source_mode = _selected_mode()
	request.source_value = _value.text
	request.path = _path.text
	request.install_as = _install_as.text
	return request


func _on_confirmed() -> void:
	var request := _build_request()

	# The OK button is disabled for an incomplete form; this is the belt to that braces (Enter in a
	# LineEdit can confirm a dialog regardless of the button's state).
	if not request.is_complete() or _shape_fix.blocks_submit:
		return

	add_requested.emit(request)
