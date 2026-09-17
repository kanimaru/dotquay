@tool
class_name DotquayPreviewSourceCard
extends VBoxContainer

## One NuGet feed an addon wants this project to take on (SPEC.md §11.3).
##
## The security-relevant control in the whole panel, so it states the three things a decision actually
## needs — who is asking, what the feed would be allowed to serve, and what breaks if you say no —
## rather than a URL and a yes/no.

signal approval_changed()

const STATUS_LABEL := preload("res://addons/dotquay/ui/items/status_label.tscn")

## Bound on first use rather than with `@onready` (matches [DotquayExplainDialog._bind]) — lets
## [method setup] be driven directly against a detached instance, which is how P1-16's plain-language
## explanation is asserted without a SceneTree.
var _approve: CheckBox
var _url: Label
var _detail: VBoxContainer

var _source: DotquayPreview.Source


func _ready() -> void:
	_bind()


func _bind() -> void:
	if _approve != null:
		return

	_approve = %Approve
	_url = %Url
	_detail = %Detail

	_approve.toggled.connect(func(_pressed: bool) -> void: approval_changed.emit())


## [param needs_approval] false means the project already trusts this host, so there is nothing to
## agree to — the card still appears, because a feed being adopted silently is exactly the thing a
## user should be able to see they agreed to earlier.
func setup(source: DotquayPreview.Source, needs_approval: bool) -> void:
	_bind()
	_source = source

	_url.text = source.url
	_url.tooltip_text = source.url

	_approve.visible = needs_approval
	_approve.button_pressed = false
	_approve.text = "Trust %s for this install" % source.host()
	_approve.tooltip_text = (
		"Adds this feed to dotquay.toml, attributed to '%s'. It applies to this install only — the "
		% source.declared_by_addon
		+ "host is not added to trusted_hosts, so the next addon that declares a feed will ask again.")

	DotquayNodes.clear_children(_detail)

	# What a "feed" even is, stated once in plain language rather than assumed (P1-16) — this card is
	# the one security-relevant decision in the whole panel, and "trusted_hosts" means nothing to
	# someone who has never had to know NuGet's own vocabulary for it.
	_add_detail(
		("A NuGet feed is a server that supplies C# packages — %s would be allowed to supply the "
			% source.host())
		+ "packages listed below to your project.",
		DotquayPalette.MUTED)

	if not needs_approval:
		_add_detail(
			"Trusted automatically: %s is already in your project's trusted hosts (dotquay.toml)." % source.host(),
			DotquayPalette.OK)

	_add_detail("Declared by addon '%s'." % source.declared_by_addon, DotquayPalette.MUTED)

	var owned := source.owned_patterns()
	if not owned.is_empty():
		_add_detail(
			"Would serve: %s — packages matching these come from this feed instead of nuget.org."
			% ", ".join(owned),
			DotquayPalette.MUTED)

	if not source.package_ids.is_empty():
		_add_detail("Needed for: %s." % ", ".join(source.package_ids), DotquayPalette.MUTED)


## True when this feed may be adopted: either the project already trusts the host, or the user just
## ticked the box.
func is_approved() -> bool:
	return not _approve.visible or _approve.button_pressed


## The URL to hand back as `--adopt-source`, or empty when this card contributes none — a host the
## project already trusts needs no flag, and an unticked box is a refusal.
func approved_url() -> String:
	return _source.url if _approve.visible and _approve.button_pressed else ""


func _add_detail(text: String, color: Color) -> void:
	var label: DotquayStatusLabel = STATUS_LABEL.instantiate()
	_detail.add_child(label)
	label.setup(text, color, "", true)
