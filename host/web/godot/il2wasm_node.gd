# A node whose script is C# in the il2wasm module beside the engine (SPEC section 10, proof 5): in the web variant of
# a project this script stands where the C# script stood, with the C# script's path in the node's metadata
# (il2wasm_script, and the scene's values of the script's properties in il2wasm_properties). It makes the script's instance in the module, hands it the scene's values of the script's
# properties, and passes the engine's callbacks on. The bridge is the autoload Il2Wasm (il2wasm_bridge.gd).
extends Node

var _cs_handle := 0
var _cs_pending := {}
var _cs_process := false
var _cs_physics := false


# the scene's value of a property the node's class does not have: the script's
func _set(property: StringName, value) -> bool:
	if String(property).begins_with("metadata/") or property == &"script" or Il2Wasm.is_native_property(self, property):
		return false
	if _cs_handle != 0:
		return Il2Wasm.set_property(_cs_handle, property, value)
	_cs_pending[property] = value
	return true


func _enter_tree() -> void:
	if _cs_handle != 0 or not Il2Wasm.active:
		return
	# the scene's values of the script's properties: in metadata (an export leaves out of a scene what neither the
	# node's class nor the script standing here declares), and whatever was set since
	var properties: Dictionary = get_meta("il2wasm_properties", {}).duplicate()
	properties.merge(_cs_pending, true)
	_cs_handle = Il2Wasm.create_instance(self, get_meta("il2wasm_script"), properties)
	_cs_pending = {}
	# the engine calls a script's _process only if it has one
	_cs_process = Il2Wasm.has_method_of(_cs_handle, "_process")
	_cs_physics = Il2Wasm.has_method_of(_cs_handle, "_physics_process")
	set_process(_cs_process)
	set_physics_process(_cs_physics)


func _ready() -> void:
	if _cs_handle != 0:
		Il2Wasm.call_method(_cs_handle, "_ready")
		set_process(_cs_process)
		set_physics_process(_cs_physics)


func _process(delta: float) -> void:
	if _cs_handle != 0:
		Il2Wasm.call_method_float(_cs_handle, "_process", delta)


func _physics_process(delta: float) -> void:
	if _cs_handle != 0:
		Il2Wasm.call_method_float(_cs_handle, "_physics_process", delta)
