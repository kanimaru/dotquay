@tool
class_name DotquayNodes
extends RefCounted

## Scene-tree helpers shared by every list-rendering script in the plugin.


## Removes and frees every child of [param container].
##
## Both steps, always: `queue_free()` alone leaves the child in the tree until the end of the frame,
## so a render that runs twice in one frame lays out stale rows beside new ones, and a lookup by name
## (the graph view's `connect_node`) can still find a node that is about to vanish. Four scripts had
## grown their own copies of this loop, two of them without the `remove_child` — this is the one that
## is right.
static func clear_children(container: Node) -> void:
	for child in container.get_children():
		container.remove_child(child)
		child.queue_free()
