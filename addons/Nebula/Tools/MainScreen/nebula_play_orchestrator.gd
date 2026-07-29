@tool
extends Node
## Play orchestrator for the Nebula editor tab.
##
## MUST STAY GDSCRIPT — do not port to C#. Starting an editor play session
## (play_custom_scene) runs the C# build, and any pending .NET assembly reload
## happens inside that call. If C# frames are on the stack the old assembly
## cannot unload (godot#78513 symptoms) and the whole C# plugin zombifies:
## dead signal connections, inert buttons. This node is parented to the editor
## base control — outside the plugin's teardown scope — and being GDScript it
## survives assembly reloads untouched, so the play call runs on a pure engine
## stack and the C# plugin reloads cleanly. It also owns instance pids and the
## log buffer so they survive reloads; the C# tab is just a view over it.

signal state_changed
signal log_line(line: String)

const MAX_LOG_LINES := 200

var _pids: Array[int] = []
var _config_name: String = ""
var _log_lines: PackedStringArray = PackedStringArray()
## Nebula debug-channel ports baked into the launch arguments, in spawn order
## (server, client). Kept here rather than on the C# side so the debugger view
## can reconnect to a live session after a .NET assembly reload recreates it.
var _debug_ports: PackedInt32Array = PackedInt32Array()
var _was_running := false


## Entry point for the C# tab. Deferred so the caller's C# frame is off the
## stack before the editor build + possible assembly reload runs.
## client_args_list holds one PackedStringArray per client instance, so a
## configuration's client count drives how many are spawned.
func launch(dummy_scene: String, exe: String, server_args: PackedStringArray, client_args_list: Array, config_name: String, debug_ports: PackedInt32Array) -> void:
	call_deferred("_do_launch", dummy_scene, exe, server_args, client_args_list, config_name, debug_ports)

func _do_launch(dummy_scene: String, exe: String, server_args: PackedStringArray, client_args_list: Array, config_name: String, debug_ports: PackedInt32Array) -> void:
	if is_running():
		_log("[play] instances already running — stop them first")
		return

	if not EditorInterface.is_playing_scene():
		EditorInterface.play_custom_scene(dummy_scene)

	# Spawn after the build above so the instances load fresh assemblies.
	var failed := false

	var server_pid := OS.create_process(exe, server_args)
	_log("[play] server pid %d: %s" % [server_pid, " ".join(server_args)])
	if server_pid > 0:
		_pids.append(server_pid)
	else:
		failed = true

	for client_args in client_args_list:
		var client_pid := OS.create_process(exe, client_args)
		_log("[play] client pid %d: %s" % [client_pid, " ".join(client_args)])
		if client_pid > 0:
			_pids.append(client_pid)
		else:
			failed = true

	if failed:
		_log("[play] ERROR: one or more instances failed to launch.")

	_config_name = config_name
	_debug_ports = debug_ports
	_was_running = is_running()
	state_changed.emit()

func stop() -> void:
	for pid in _pids:
		var err := OS.kill(pid)
		if err == OK:
			_log("[stop] killed pid %d" % pid)
		else:
			_log("[stop] pid %d: %s (already exited?)" % [pid, error_string(err)])
	_pids.clear()

	# Also end the dummy session (closes the debug server). Note this stops
	# whatever editor play session is active, dummy or not.
	if EditorInterface.is_playing_scene():
		EditorInterface.stop_playing_scene()
		_log("[stop] stopped editor play session (debug server closed)")

	_config_name = ""
	_debug_ports = PackedInt32Array()
	_was_running = false
	state_changed.emit()


## Detects instances exiting on their own. Without this, state_changed only
## fires on launch/stop, so the toolbar button would stay "Stop" forever after
## the user closes a client window.
func _process(_delta: float) -> void:
	var running := is_running()
	if running == _was_running:
		return
	_was_running = running
	if not running:
		_pids.clear()
		_config_name = ""
		_debug_ports = PackedInt32Array()
		_log("[play] all instances exited")
	state_changed.emit()


func is_running() -> bool:
	for pid in _pids:
		if OS.is_process_running(pid):
			return true
	return false


func get_config_name() -> String:
	return _config_name


func get_debug_ports() -> PackedInt32Array:
	return _debug_ports


func get_log_lines() -> PackedStringArray:
	return _log_lines


func _log(line: String) -> void:
	_log_lines.append(line)
	if _log_lines.size() > MAX_LOG_LINES:
		_log_lines = _log_lines.slice(_log_lines.size() - MAX_LOG_LINES)
	log_line.emit(line)
