@tool
extends VBoxContainer
## Live per-peer sync status for the selected world.
##
## Unlike the Nodes/Calls/Logs tabs this is not tied to a tick frame: PEERS
## frames are emitted at ~1Hz and are not persisted, so this always shows the
## present state rather than the state at the selected tick.

@onready var item_list: ItemList = $ItemList


func _on_world_debug_peers_updated(peers: Array) -> void:
    if item_list == null:
        return

    item_list.clear()
    for peer in peers:
        item_list.add_item("%s\n    tick %d · %s · %d owned node(s)" % [
            peer.get("id", "?"),
            peer.get("tick", 0),
            peer.get("status", "?"),
            peer.get("owned_nodes", 0),
        ])
