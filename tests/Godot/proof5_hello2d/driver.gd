# Proof 5's fixed input (a test harness, the same script under stock Godot .NET on the desktop and in the web
# export): presses the game's actions through Godot's Input at fixed physics ticks and quits after the last one.
# Off unless the command line has --proof5-drive (desktop: after "--"; web: in the engine's args).
extends Node

var tick := 0
var driving := false


func _ready() -> void:
	process_physics_priority = -100 # before the scripts, so a press is seen on the same tick
	driving = "--proof5-drive" in OS.get_cmdline_user_args() or "--proof5-drive" in OS.get_cmdline_args()
	set_physics_process(driving)


func _physics_process(_delta: float) -> void:
	tick += 1
	match tick:
		21:
			Input.action_press("move_right")
		71:
			Input.action_release("move_right")
		80:
			Input.action_press("jump")
		81:
			Input.action_release("jump")
		100:
			Input.action_press("move_left")
		130:
			Input.action_release("move_left")
		160:
			Input.action_press("debug_panic")
		161:
			Input.action_release("debug_panic")
		170:
			set_physics_process(false)
			if OS.has_feature("web"):
				JavaScriptBridge.eval("window.cs2wasmGameDone && window.cs2wasmGameDone()", true)
			else:
				get_tree().quit()
