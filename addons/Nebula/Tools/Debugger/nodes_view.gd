@tool
extends VBoxContainer

@onready var tree: Tree = $Tree
@export var world_debug: Control
@export var world_inspector: Control

# Track items that need color transition
var transitioning_items: Dictionary = {}
const TRANSITION_SPEED: float = 3.0  # Adjust this to control transition speed
const WHITE_COLOR = Color(1, 1, 1)
const CHANGED_COLOR = Color(1, 0.5, 0)

signal network_node_inspected(node_data: Dictionary)
signal network_nodes_changed(state: bool)

## Last rendered world state, kept so the frame-to-frame diff doesn't have to
## re-read frame N-1 from the database and re-parse its JSON on every update.
var _previous_world_state: Dictionary = {}
## Frame that arrived while this view was hidden, rendered when it reappears.
var _pending_frame_id: int = -1


func _ready() -> void:
    visibility_changed.connect(_on_visibility_changed)


func _on_visibility_changed() -> void:
    if not is_visible_in_tree() or _pending_frame_id < 0:
        return
    var id := _pending_frame_id
    _pending_frame_id = -1
    update_tree(id, world_debug.call("GetFrame", id))

func _process(delta: float) -> void:
    if not is_visible_in_tree():
        return

    var items_to_remove = []
    
    for item in transitioning_items:
        if !is_instance_valid(item) or item.is_queued_for_deletion():
            items_to_remove.append(item)
            continue
        var current_color: Color = item.get_custom_color(0)
        var new_color = current_color.lerp(WHITE_COLOR, delta * TRANSITION_SPEED)
        
        item.set_custom_color(0, new_color)
        
        # If we're very close to white, remove from tracking
        if new_color.is_equal_approx(WHITE_COLOR):
            item.set_custom_color(0, WHITE_COLOR)
            items_to_remove.append(item)
    
    # Remove completed items
    for item in items_to_remove:
        transitioning_items.erase(item)

func _set_item_color(item: TreeItem, is_changed: bool) -> void:
    if is_changed:
        transitioning_items.erase(item)
        item.set_custom_color(0, CHANGED_COLOR)
    else:
        # Add to transitioning items instead of setting white directly
        transitioning_items[item] = true

func update_tree(frame_id: int, frame_data: Dictionary) -> void:
    var world_state: Dictionary = frame_data.get("world_state")
    if world_state.is_empty():
        return

    # Diff against what we last drew rather than re-fetching frame N-1: that was
    # a second database read plus a full JSON parse of the whole world, on every
    # single update.
    var previous_world_state := _previous_world_state
    
    # Create root node if it doesn't exist
    var root = tree.get_root()
    if not root:
        root = tree.create_item()
    
    # Update root node text
    root.set_text(0, world_state.get("nodeName", "Root"))
    var root_metadata = root.get_metadata(0)
    var changed = not previous_world_state.is_empty() and previous_world_state.hash() != world_state.hash()
    network_nodes_changed.emit(changed)
    _set_item_color(root, changed)
    root.set_metadata(0, world_state)

    _reconcile_children(root, world_state.get("children", {}), previous_world_state.get("children", {}))
    _previous_world_state = world_state

    if tree.get_selected() != null:
        network_node_inspected.emit(tree.get_selected().get_metadata(0))

## The exported world state groups children by their parent's relative path:
##   { "<relative parent path>": [childDoc, ...] }
## (see NetNodeCommon.ToBSONDocument — FromBSON requires that shape, so it is
## the persistence format and can't be reshaped server-side for us). The tree
## reconciles by name, so flatten it into { name: childDoc } first.
func _flatten_children(children: Dictionary) -> Dictionary:
    var out := {}
    for parent_path in children:
        var bucket = children[parent_path]
        if not (bucket is Array):
            continue
        for child_doc in bucket:
            if not (child_doc is Dictionary):
                continue
            var key: String = str(child_doc.get("nodeName", parent_path))
            # Keys must match the TreeItem text for reconciliation to reuse
            # items, so siblings sharing a name get disambiguated rather than
            # overwriting each other.
            while out.has(key):
                key += "'"
            out[key] = child_doc
    return out


func _reconcile_children(parent_item: TreeItem, raw_children: Dictionary, raw_previous_children: Dictionary) -> void:
    var children := _flatten_children(raw_children)
    var previous_children := _flatten_children(raw_previous_children)

    var existing_children = {}
    var child = parent_item.get_first_child()

    while child:
        existing_children[child.get_text(0)] = child
        child = child.get_next()

    for child_name in children:
        var child_data = children[child_name]
        var child_item: TreeItem
        
        if existing_children.has(child_name):
            child_item = existing_children[child_name]
            existing_children.erase(child_name)
        else:
            child_item = tree.create_item(parent_item)
        
        child_item.set_text(0, child_data.get("nodeName", child_name))
        var child_metadata = child_item.get_metadata(0)
        var changed = previous_children != null and previous_children.get(child_name, {}).hash() != child_data.hash()
        network_nodes_changed.emit(changed)
        _set_item_color(child_item, changed)
        child_item.set_metadata(0, child_data)
        if child_data.has("children"):
            _reconcile_children(child_item, child_data["children"], previous_children.get(child_name, {}).get("children", {}))
    
    for child_item in existing_children.values():
        child_item.free()

func _on_world_debug_tick_frame_selected(tickFrame: TickFrameUI) -> void:
    var frame_data = world_debug.call("GetFrame", tickFrame.tick_frame_id)
    update_tree(tickFrame.tick_frame_id, frame_data)

func _on_world_debug_tick_frame_updated(id:int) -> void:
    if not world_debug.get("IsLive"):
        return
    # Rebuilding a tree nobody can see (another world selected, or another tab
    # open) cost exactly as much as a visible one; defer it until it matters.
    if not is_visible_in_tree():
        _pending_frame_id = id
        return
    update_tree(id, world_debug.call("GetFrame", id))

func _on_tree_item_selected() -> void:
    network_node_inspected.emit(tree.get_selected().get_metadata(0))
