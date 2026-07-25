@tool
extends VBoxContainer

func _ready():
    var mtu = ProjectSettings.get_setting("Nebula/config/mtu", 1400)
    get_node("MTUMax").text = str(mtu)
    get_node("MTUMed").text = str(mtu / 2)