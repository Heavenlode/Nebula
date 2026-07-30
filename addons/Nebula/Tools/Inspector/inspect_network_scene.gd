@tool
class_name InspectNetScene extends Accordion

@export var properties_parent: Accordion
@export var properties_container: VBoxContainer

@export var functions_parent: Accordion
@export var functions_container: VBoxContainer

@export var static_nodes_parent: Accordion
@export var static_nodes_container: VBoxContainer

@export var title_label: Label
@export var path_label: Label
@export var property_count_label: Label
@export var function_count_label: Label

## Per-scene budget rows: hidden unless set_scene_stats() is called (i.e. only on
## a NetScene, since both limits are per-NetScene).
@export var scene_property_row: Control
@export var scene_property_label: Label
@export var static_node_count_row: Control
@export var static_node_count_label: Label

## Shown on a static NetNode instead: a link up to the NetScene that owns it.
@export var net_scene_link_row: Control
@export var net_scene_link_button: Button

var function_row_scene = preload("res://addons/Nebula/Tools/Inspector/property_row.tscn")
var property_row_scene = preload("res://addons/Nebula/Tools/Inspector/property_row.tscn")

var properties: Dictionary = {}
var _property_count := 0
var _function_count := 0

func _ready() -> void:
    _apply_bold_title()


## Bolds the heading. Prefers the editor's real bold face; falls back to a
## synthesised weight so this still works outside the editor (and if the theme
## ever stops exposing that font).
func _apply_bold_title() -> void:
    if title_label == null:
        return

    if Engine.is_editor_hint():
        var editor_theme := EditorInterface.get_editor_theme()
        if editor_theme != null and editor_theme.has_font("bold", "EditorFonts"):
            title_label.add_theme_font_override("font", editor_theme.get_font("bold", "EditorFonts"))
            return

    var base_font := title_label.get_theme_font("font")
    if base_font == null:
        return
    var bold := FontVariation.new()
    bold.base_font = base_font
    bold.variation_embolden = 0.6
    title_label.add_theme_font_override("font", bold)


func set_title(title: String) -> void:
    if title_label == null:
        return
    title_label.text = title

func set_path(path: String) -> void:
    if path_label == null:
        return
    path_label.text = path

func add_property(name: String, value: String) -> void:
    _property_count += 1
    property_count_label.text = str(_property_count)

    var property_row = property_row_scene.instantiate()
    property_row.get_node("Name").text = name
    property_row.get_node("Value").text = value
    properties_parent.visible = true
    properties_container.add_child(property_row)

    properties[name] = property_row

func set_property(name: String, value: String) -> void:
    # Guarded: the live debugger streams whatever properties a node reports, which
    # need not match the set this panel was built from.
    if not properties.has(name):
        return
    properties[name].get_node("Value").text = value

func add_function(name: String, type: String) -> void:
    _function_count += 1
    function_count_label.text = str(_function_count)

    var function_row = function_row_scene.instantiate()
    function_row.get_node("Name").text = name
    function_row.get_node("Value").text = type
    functions_container.add_child(function_row)
    functions_parent.visible = true


## Reports the two per-NetScene protocol budgets, so the headroom is visible
## before a build fails on it: properties are bound by the 64-bit dirty mask
## (NEBULA004) and static NetNodes by the byte-wide static child id.
##
## The property figure is scene-wide — it includes everything rolled up from
## static children and nested non-NetScene instances — which is deliberately a
## different number from the "Properties" row above (that one is just this node's).
func set_scene_stats(scene_properties: int, max_properties: int, static_nodes: int, max_static_nodes: int) -> void:
    var remaining := max_properties - scene_properties
    scene_property_label.text = "%d / %d  (%d left)" % [scene_properties, max_properties, remaining]
    _tint_budget(scene_property_label, scene_properties, max_properties)
    scene_property_row.visible = true

    static_node_count_label.text = "%d / %d  (%d left)" % [static_nodes, max_static_nodes, max_static_nodes - static_nodes]
    _tint_budget(static_node_count_label, static_nodes, max_static_nodes)
    static_node_count_row.visible = true


## One row of the "Static NetNodes" list. Selectable rows are buttons that
## reveal the node in the scene tree; a node that isn't reachable from the scene
## being edited (a NetScene instanced into another scene keeps its children
## non-editable) is shown as a plain label instead of a button that would do
## nothing.
func add_static_node(node_path: String, property_count: int, selectable: bool) -> void:
    var row := HBoxContainer.new()

    if selectable:
        var button := Button.new()
        button.text = node_path
        button.flat = true
        button.alignment = HORIZONTAL_ALIGNMENT_LEFT
        button.size_flags_horizontal = Control.SIZE_EXPAND_FILL
        button.tooltip_text = "Select \"%s\" in the scene tree." % node_path
        button.pressed.connect(_select_in_scene_tree.bind(node_path))
        _tint_link(button)
        row.add_child(button)
    else:
        var label := Label.new()
        label.text = node_path
        label.size_flags_horizontal = Control.SIZE_EXPAND_FILL
        label.tooltip_text = "Not part of the scene being edited — open the NetScene itself to select this node."
        label.modulate = Color(1, 1, 1, 0.6)
        row.add_child(label)

    var count := Label.new()
    count.text = "1 prop" if property_count == 1 else "%d props" % property_count
    count.modulate = Color(1, 1, 1, 0.6)
    row.add_child(count)

    static_nodes_container.add_child(row)
    static_nodes_parent.visible = true


## Links a static NetNode back to the NetScene that owns its network state — the
## scene whose 64-property / 255-node budgets this node spends, and the node the
## serializer actually lives on.
func set_net_scene_link(scene_root_name: String, scene_path: String) -> void:
    net_scene_link_button.text = scene_root_name
    net_scene_link_button.tooltip_text = "Select the owning NetScene root in the scene tree.\n%s" % scene_path
    _tint_link(net_scene_link_button)
    net_scene_link_button.pressed.connect(_select_in_scene_tree.bind("."))
    net_scene_link_row.visible = true


## Reveals a node of the edited scene in the scene tree dock (and the inspector).
## Paths come from the generated protocol, so they describe the last successful
## build — a node renamed since then no longer resolves.
func _select_in_scene_tree(node_path: String) -> void:
    if not Engine.is_editor_hint():
        return

    var root := EditorInterface.get_edited_scene_root()
    if root == null:
        return

    var target: Node = root if node_path == "." else root.get_node_or_null(node_path)
    if target == null:
        push_warning("Nebula: \"%s\" is not in the edited scene. The protocol reflects the last C# build — rebuild to refresh it." % node_path)
        return

    var selection := EditorInterface.get_selection()
    selection.clear()
    selection.add_node(target)
    EditorInterface.edit_node(target)


## Warns as a budget fills up, so a scene near a hard protocol limit reads as
## such without the developer doing the arithmetic.
func _tint_budget(label: Label, used: int, maximum: int) -> void:
    if not Engine.is_editor_hint():
        return
    var editor_theme := EditorInterface.get_editor_theme()
    if editor_theme == null:
        return

    var color_name := ""
    if used >= maximum:
        color_name = "error_color"
    elif used >= maximum * 0.85:
        color_name = "warning_color"
    if color_name.is_empty():
        return
    if editor_theme.has_color(color_name, "Editor"):
        label.add_theme_color_override("font_color", editor_theme.get_color(color_name, "Editor"))


## Paints a clickable row in the editor's accent color so it reads as a link.
func _tint_link(button: Button) -> void:
    if not Engine.is_editor_hint():
        return
    var editor_theme := EditorInterface.get_editor_theme()
    if editor_theme == null or not editor_theme.has_color("accent_color", "Editor"):
        return
    var accent := editor_theme.get_color("accent_color", "Editor")
    button.add_theme_color_override("font_color", accent)
    button.add_theme_color_override("font_hover_color", accent.lightened(0.2))
    button.add_theme_color_override("font_pressed_color", accent)
