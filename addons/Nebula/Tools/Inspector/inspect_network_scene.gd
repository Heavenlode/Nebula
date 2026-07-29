@tool
class_name InspectNetScene extends Accordion

@export var properties_parent: Accordion
@export var properties_container: VBoxContainer

@export var functions_parent: Accordion
@export var functions_container: VBoxContainer

@export var children_parent: Accordion
@export var children_container: VBoxContainer

@export var title_label: Label
@export var path_label: Label
@export var property_count_label: Label
@export var function_count_label: Label

var function_row_scene = preload("res://addons/Nebula/Tools/Inspector/property_row.tscn")
var property_row_scene = preload("res://addons/Nebula/Tools/Inspector/property_row.tscn")
var net_node_details_scene = load("res://addons/Nebula/Tools/Inspector/net_node_details.tscn")

var child_details: Array[InspectNetScene] = []
var properties: Dictionary = {}

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

func set_path(path: String, child_detail_id: int = -1) -> void:
    if child_detail_id != -1:
        child_details[child_detail_id].set_path(path)
        return
    if path_label == null:
        return
    path_label.text = path

func add_property(name: String, value: String, child_detail_id: int = -1) -> void:
    property_count_label.text = str(int(property_count_label.text) + 1)

    if child_detail_id != -1:
        child_details[child_detail_id].add_property(name, value)
        return

    var property_row = property_row_scene.instantiate()
    property_row.get_node("Name").text = name
    property_row.get_node("Value").text = value
    properties_parent.visible = true
    properties_container.add_child(property_row)

    var property_key = name + ":" + str(child_detail_id)
    properties[property_key] = property_row

func set_property(name: String, value: String, child_detail_id: int = -1) -> void:
    var property_key = name + ":" + str(child_detail_id)
    properties[property_key].get_node("Value").text = value

func add_function(name: String, type: String, child_detail_id: int = -1) -> void:
    function_count_label.text = str(int(function_count_label.text) + 1)

    if child_detail_id != -1:
        child_details[child_detail_id].add_function(name, type)
        return

    var function_row = function_row_scene.instantiate()
    function_row.get_node("Name").text = name
    function_row.get_node("Value").text = type
    functions_container.add_child(function_row)
    functions_parent.visible = true

func add_child_detail(name: String) -> int:
    var child_detail = net_node_details_scene.instantiate()
    child_detail.get_node("Button").text = child_detail.get_node("Button").text + " " + name
    child_details.append(child_detail)
    children_container.add_child(child_detail)

    # split the name on slashes and use the last part as the title
    var title = name.split("/")[-1]
    child_detail.set_title(title)
    children_parent.visible = true
    return child_details.size() - 1
