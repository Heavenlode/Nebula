@tool
extends EditorPlugin

const AUTOLOAD_RUNNER = "NetRunner"
const AUTOLOAD_PROTOCOL_REGISTRY = "ProtocolRegistry"
const AUTOLOAD_PROTOCOL_REGISTRY_BUILDER = "ProtocolRegistryBuilder"
const AUTOLOAD_ENV = "Env"
const AUTOLOAD_DEBUGGER = "Debugger"
const AUTOLOAD_DATA_TRANSFORMER = "BsonTransformer"

const DockNetScenes = preload("res://addons/Nebula/Tools/Dock/NetScenes/dock_net_scenes.tscn")
var dock_net_scenes_instance: Control

const ServerDebugClient = preload("res://addons/Nebula/Tools/Debugger/server_debug_client.tscn")
var server_debug_client_instance: Window

const NetSceneInspector = preload("res://addons/Nebula/Tools/Inspector/NetSceneInspector.cs")
# const AddonManager = preload("res://addons/Nebula/Tools/AddonManager/addon_manager.tscn")
const ToolMenu = preload("res://addons/Nebula/Tools/tool_menu.tscn")

var net_scene_inspector_instance: NetSceneInspector
var addon_manager_instance: Node
var tool_menu_instance: PopupMenu
var project_settings_controller: ProjectSettingsController

func _build():
    var build_result = get_node("/root/" + AUTOLOAD_PROTOCOL_REGISTRY_BUILDER).Build()
    if !build_result:
        printerr("Failed to build Nebula protocol")
        return false
    get_node("/root/" + AUTOLOAD_PROTOCOL_REGISTRY).Load()
    return true

func _get_plugin_name():
    return "Nebula"

func _enter_tree():

    # tool_menu_instance = ToolMenu.instantiate()

    # add_tool_submenu_item("Nebula", tool_menu_instance)

    add_autoload_singleton(AUTOLOAD_DEBUGGER, "res://addons/Nebula/Utils/Debugger/Debugger.cs")
    add_autoload_singleton(AUTOLOAD_ENV, "res://addons/Nebula/Utils/Env/Env.cs")
    add_autoload_singleton(AUTOLOAD_DATA_TRANSFORMER, "res://addons/Nebula/Core/Serialization/BsonTransformer.cs")
    add_autoload_singleton(AUTOLOAD_PROTOCOL_REGISTRY, "res://addons/Nebula/Core/Serialization/ProtocolRegistry.cs")
    add_autoload_singleton(AUTOLOAD_RUNNER, "res://addons/Nebula/Core/NetRunner.cs")
    add_autoload_singleton(AUTOLOAD_PROTOCOL_REGISTRY_BUILDER, "res://addons/Nebula/Core/Serialization/ProtocolRegistryBuilder.cs")

    var build_result = get_node("/root/" + AUTOLOAD_PROTOCOL_REGISTRY_BUILDER).Build()
    if build_result:
        get_node("/root/" + AUTOLOAD_PROTOCOL_REGISTRY).Load()

    # var network_transform_3d_icon = EditorInterface.get_editor_theme().get_icon("RemoteTransform3D", "EditorIcons")
    # add_custom_type("NetTransform3D", "Node", preload("res://addons/Nebula/Core/Nodes/NetTransform/NetTransform3D.cs"), network_transform_3d_icon)
    # var network_transform_2d_icon = EditorInterface.get_editor_theme().get_icon("RemoteTransform2D", "EditorIcons")
    # add_custom_type("NetTransform2D", "Node", preload("res://addons/Nebula/Core/Nodes/NetTransform/NetTransform2D.cs"), network_transform_2d_icon)

    project_settings_controller = ProjectSettingsController.new()
    add_child(project_settings_controller)

    dock_net_scenes_instance = DockNetScenes.instantiate()
    dock_net_scenes_instance.name = "Network Scenes"
    add_control_to_dock(DOCK_SLOT_LEFT_UR, dock_net_scenes_instance)

    server_debug_client_instance = ServerDebugClient.instantiate()
    add_child(server_debug_client_instance)
    _register_menu_item("Debugger", func():
        server_debug_client_instance.show()
    )

    net_scene_inspector_instance = NetSceneInspector.new()
    add_inspector_plugin(net_scene_inspector_instance)

    # addon_manager_instance = AddonManager.instantiate()
    # add_child(addon_manager_instance)
    # addon_manager_instance.call("SetPluginRoot", self)

func _exit_tree():
    # addon_manager_instance.queue_free()

    remove_inspector_plugin(net_scene_inspector_instance)

    # server_debug_client_instance.queue_free()

    remove_control_from_docks(dock_net_scenes_instance)
    dock_net_scenes_instance.queue_free()

    project_settings_controller.queue_free()

    # remove_custom_type("NetTransform3D")
    # remove_custom_type("NetTransform2D")

    remove_autoload_singleton(AUTOLOAD_PROTOCOL_REGISTRY_BUILDER)
    remove_autoload_singleton(AUTOLOAD_RUNNER)
    remove_autoload_singleton(AUTOLOAD_PROTOCOL_REGISTRY)
    remove_autoload_singleton(AUTOLOAD_DATA_TRANSFORMER)
    remove_autoload_singleton(AUTOLOAD_ENV)
    remove_autoload_singleton(AUTOLOAD_DEBUGGER)
    # remove_tool_menu_item("Nebula")

var menu_item_ids: int = 0
func _register_menu_item(label: String, on_click: Callable):
    var new_menu_item_id = menu_item_ids
    tool_menu_instance.add_item(label, new_menu_item_id)
    tool_menu_instance.id_pressed.connect(func (menu_item_id: int):
        if menu_item_id == new_menu_item_id:
            on_click.call()
    )
    menu_item_ids += 1

# func _has_main_screen():
#     return true

# func _make_visible(visible):
#     if main_panel_instance:
#         main_panel_instance.visible = visible

# func _get_plugin_icon():
#     # Must return some kind of Texture for the icon.
#     return get_editor_interface().get_base_control().get_theme_icon("Signals", "EditorIcons")
