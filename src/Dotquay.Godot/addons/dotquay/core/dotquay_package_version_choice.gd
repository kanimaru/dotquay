@tool
class_name DotquayPackageVersionChoice
extends RefCounted

## What the package version dialog says and accepts, for both of its jobs (DESIGN-v0.19 §7a.3):
## pinning a package an addon brought in, and changing one the project already declares.
##
## All static functions of plain data, the same split [DotquayNodeActions] uses, so the wording and
## the "can OK be pressed yet" rule are tests rather than something only visible in a window. The
## dialog scene just binds these to its labels and button.

## Loose on purpose: NuGet's own grammar is the CLI's to enforce, and it does, naming the version it
## couldn't find. This only has to tell a version-looking string from a typo or a range, so OK
## doesn't close the dialog into a guaranteed failure. It accepts 1 to 4 numeric parts, then an
## optional -prerelease and an optional +metadata.
const _VERSION_PATTERN := "^\\d+(\\.\\d+){0,3}(-[0-9A-Za-z][0-9A-Za-z.-]*)?(\\+[0-9A-Za-z][0-9A-Za-z.-]*)?$"


## True when [param model] is a package the dialog pins rather than changes: one the project doesn't
## declare yet.
static func is_pin(model: DotquayGraph.PackageNode) -> bool:
	return not model.is_root()


static func title(model: DotquayGraph.PackageNode) -> String:
	return ("Pin %s in your project" if is_pin(model) else "Change %s's version") % model.name


## One paragraph saying exactly what pressing OK will do, and how to undo it.
static func explanation(model: DotquayGraph.PackageNode) -> String:
	if is_pin(model):
		var addons := DotquayNodeActions.requirer_names_summary(model.addon_requirers())
		return (
			("Adds [dotnet.\"%s\"] to your dotquay.toml at the exact version below. Your entry wins over "
			+ "what %s asks for, so updating %s no longer moves this package. Remove it later to hand the "
			+ "choice back.") % [model.name, addons, addons])

	return (
		("Changes the version your dotquay.toml declares for %s to the exact version below, then updates "
		+ "the .csproj and restores. Its feed and asset settings stay as they are.") % model.name)


## The version the field starts with: what's locked now, so the common edit is a nudge of one digit.
static func initial_version(model: DotquayGraph.PackageNode) -> String:
	return model.resolved


static func ok_label(model: DotquayGraph.PackageNode) -> String:
	return "Pin" if is_pin(model) else "Change"


static func is_plausible_version(text: String) -> bool:
	return RegEx.create_from_string(_VERSION_PATTERN).search(text.strip_edges()) != null


## Why OK is disabled for [param text], or "" when it isn't. A change to the version already locked
## would do nothing, so it's refused here rather than reported as "unchanged" after a CLI round trip.
## A pin to the locked version is fine: it still moves the choice into the project.
static func blocking_reason(model: DotquayGraph.PackageNode, text: String) -> String:
	var version := text.strip_edges()

	if version.is_empty():
		return "Enter a version."
	if not is_plausible_version(version):
		return "'%s' doesn't look like a NuGet version. Use an exact version such as 1.4.2, not a range." % version
	if not is_pin(model) and version == model.resolved:
		return "%s is already at %s." % [model.name, version]
	return ""
