@tool
class_name DotquayInstallPreviewDialog
extends ConfirmationDialog

## The confirmation step between "add this addon" and actually adding it (SPEC.md §6, §11.3).
##
## Two consequences of an `add` are invisible from the URL a user typed: the addons it pulls in
## transitively, and the NuGet feeds those addons want the project to trust. This dialog shows both,
## from a real `add --preview` resolve, and turns the second into a decision the user makes before
## anything is written rather than a prompt the plugin cannot answer.
##
## It decides nothing itself. Confirming emits the source and the feed URLs the user ticked, and the
## panel runs `dotquay add --adopt-source ...` — so an install agreed to here is byte-for-byte the
## install the same flags would have produced at a terminal.

## [param request] is the same request the preview was resolved from, carried through untouched.
## [param adopt_source_urls] are the feeds the user approved, passed through as `--adopt-source`.
signal install_confirmed(request: DotquayAddRequest, adopt_source_urls: PackedStringArray)

const ADDON_ROW := preload("res://addons/dotquay/ui/items/preview_addon_row.tscn")
const SOURCE_CARD := preload("res://addons/dotquay/ui/items/preview_source_card.tscn")
const STATUS_LABEL := preload("res://addons/dotquay/ui/items/status_label.tscn")

## Bound on first use rather than with `@onready` (matches [DotquayExplainDialog._bind]) — this is
## what lets [method show_preview] be driven directly against a detached instance, the way P1-15's
## no-op-preview OK-disable is asserted.
var _summary: Label
var _addon_list: VBoxContainer
var _dotnet_section: VBoxContainer
var _dotnet_list: VBoxContainer
var _source_section: VBoxContainer
var _source_list: VBoxContainer
var _blocker: Label

var _preview: DotquayPreview
var _request: DotquayAddRequest

## The cards the user can still say no to. Kept as a list rather than re-walked from the container so
## the enable/disable check reads as one question — "is every feed approved?" — instead of a scan.
var _pending_cards: Array = []


func _ready() -> void:
	_bind()


func _bind() -> void:
	if _summary != null:
		return

	_summary = %Summary
	_addon_list = %AddonList
	_dotnet_section = %DotnetSection
	_dotnet_list = %DotnetList
	_source_section = %SourceSection
	_source_list = %SourceList
	_blocker = %Blocker

	confirmed.connect(_on_confirmed)
	_summary.add_theme_color_override("font_color", DotquayPalette.MUTED)
	_blocker.add_theme_color_override("font_color", DotquayPalette.WARN)

	for label in [%AddonsHeading, %MarkerLegend, %DotnetHeading, %SourcesHeading]:
		(label as Label).add_theme_color_override("font_color", DotquayPalette.MUTED)


## [param request] is carried through untouched so the confirmed add uses exactly the arguments the
## preview was resolved from — re-deriving them from the payload would be a second chance to get them
## wrong, and the payload does not carry every argument anyway (`--path` and `--install-as` are inputs
## the CLI has no reason to echo back).
func show_preview(preview: DotquayPreview, request: DotquayAddRequest) -> void:
	_bind()
	_preview = preview
	_request = request
	_pending_cards.clear()

	# "Add", not "Install": the toolbar already has an unrelated Install button (re-runs the lockfile),
	# and this dialog is the confirmation step for Add, not for that (P1-14).
	title = "Add '%s'?" % preview.name
	_summary.text = preview.summarize()

	_render_addons()
	_render_dotnet()
	_render_sources()
	_update_ok_state()

	# Rendering is the contract; popping up is what being in a tree adds on top of it — guarded so the
	# same call is valid on a detached instance, which is how P1-15's no-op-preview OK-disable is
	# asserted without a SceneTree.
	if is_inside_tree():
		popup_centered()


func _render_addons() -> void:
	_clear(_addon_list)

	# Every addon in the resolved graph, not only the new ones: a user agreeing to an install wants
	# the shape of the graph, and omitting the diamond's shared corner misrepresents it. The rows say
	# which are actually changing.
	for addon in _preview.addons:
		var row: DotquayPreviewAddonRow = ADDON_ROW.instantiate()
		_addon_list.add_child(row)
		row.setup(addon)


func _render_dotnet() -> void:
	_clear(_dotnet_list)

	var packages := _preview.new_packages()
	_dotnet_section.visible = not packages.is_empty()

	for package in packages:
		var label: DotquayStatusLabel = STATUS_LABEL.instantiate()
		_dotnet_list.add_child(label)
		label.setup(
			"+ %s  %s  (%s)" % [package.name, package.constraint, package.describe_origin()],
			DotquayPalette.OK,
			"Written into your .csproj as a PackageReference and restored with dotnet.",
			true)


func _render_sources() -> void:
	_clear(_source_list)

	var automatic := _preview.sources_to_adopt
	var pending := _preview.sources_needing_approval
	_source_section.visible = not (automatic.is_empty() and pending.is_empty())

	# Feeds needing a decision come first. A card the user must act on must not be below one they
	# can't.
	for source in pending:
		_pending_cards.append(_add_source_card(source, true))

	for source in automatic:
		_add_source_card(source, false)


func _add_source_card(source: DotquayPreview.Source, needs_approval: bool) -> DotquayPreviewSourceCard:
	var card: DotquayPreviewSourceCard = SOURCE_CARD.instantiate()
	_source_list.add_child(card)
	card.setup(source, needs_approval)

	if needs_approval:
		card.approval_changed.connect(_update_ok_state)

	return card


## The OK button is the enforcement point: a refused feed makes the add fail on the CLI side anyway,
## and letting the user press "Add" into a guaranteed failure teaches them the dialog is noise.
##
## A no-op preview (P1-15) gets the same treatment for the opposite reason — not a failure waiting to
## happen, but nothing at all for "Add" to do. [method DotquayPreview.summarize] already says so in
## the headline; disabling OK is what stops that sentence being decoration a user can click past.
func _update_ok_state() -> void:
	var unapproved := _pending_cards.filter(
		func(card: DotquayPreviewSourceCard) -> bool: return not card.is_approved())
	var no_op := _preview != null and _preview.is_no_op

	get_ok_button().disabled = no_op or not unapproved.is_empty()
	get_ok_button().tooltip_text = (
		"Nothing on disk would change, so there is nothing to add." if no_op else "")

	_blocker.visible = not unapproved.is_empty()
	var feed_phrase := DotquayText.plural(unapproved.size(), "%d feed above still needs", "%d feeds above still need")
	_blocker.text = ("%s your approval. Without them, dotquay cannot find the NuGet packages this "
		% feed_phrase + "addon requires, and the install would fail.")


func _on_confirmed() -> void:
	var urls := PackedStringArray()

	for card in _pending_cards:
		var url: String = card.approved_url()
		if not url.is_empty():
			urls.append(url)

	install_confirmed.emit(_request, urls)


func _clear(container: Node) -> void:
	DotquayNodes.clear_children(container)
