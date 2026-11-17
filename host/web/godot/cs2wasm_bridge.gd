# The engine's end of the bridge between a cs2wasm module (C# scripts compiled to WASM, running beside the engine in
# the page) and stock Godot's web export (SPEC section 10, proof 5). An autoload named Cs2Wasm in the exported project.
# The page's glue (host/web/godot-web.mjs) is window.cs2wasmEngine:
#   the module calls the engine: the glue calls the callback given to attach() with
#     ("call", object, method, argument codes, result code, values...)  a method of an object, by ptrcall's types
#     ("class", object)                                                 an object's native class
#     ("singleton", name)                                               an engine singleton
#   and the answer goes back through cs2wasmEngine.ret(values...) (a callback's own result does not reach JavaScript);
#   the engine calls the module: createInstance, setProperty, hasScriptMethod, callScriptMethod, shutdown of
#   cs2wasmEngine (names no Object method has: the interface is an Object here).
# Objects cross as numbers this script hands out (a JavaScript number cannot hold an instance id).
# Value codes: b bool, i int, f float, s String, n StringName, p NodePath, o Object, 2 Vector2, I Vector2i, c Color,
# r Rect2, v nothing.
extends Node

var _js: JavaScriptObject
var _callback: JavaScriptObject # (kept: the engine frees a callback nobody holds)
var _objects := {} # handle -> Object
var _handles := {} # instance id -> handle
var _next := 16
var _native := {} # class -> { property name: true }
var active := false


func _init() -> void:
	if not OS.has_feature("web"):
		return
	_js = JavaScriptBridge.get_interface("cs2wasmEngine")
	if _js == null:
		push_error("cs2wasm: the page has no cs2wasmEngine")
		return
	active = true
	_callback = JavaScriptBridge.create_callback(_on_call)
	_js.attach(_callback)


# the tree is going (the game quits): the module's shutdown, while the engine still answers
func _exit_tree() -> void:
	if active:
		active = false
		_js.shutdown()


func handle_of(o: Object) -> int:
	if o == null:
		return 0
	var id := o.get_instance_id()
	if _handles.has(id):
		return _handles[id]
	var h := _next
	_next += 16
	_handles[id] = h
	_objects[h] = o
	return h


func _object(h: int) -> Object:
	var o = _objects.get(h)
	if o == null or not is_instance_valid(o):
		return null
	return o


func _on_call(a: Array) -> void:
	match a[0]:
		"call":
			_call(a)
		"class":
			var o := _object(int(a[1]))
			_js.ret(o.get_class() if o != null else "")
		"singleton":
			_js.ret(handle_of(Engine.get_singleton(a[1])) if Engine.has_singleton(a[1]) else 0)
		_:
			push_error("cs2wasm: unknown request " + str(a[0]))


func _call(a: Array) -> void:
	var o := _object(int(a[1]))
	if o == null:
		push_error("cs2wasm: a call of %s on an object that is gone" % a[2])
		_js.ret()
		return
	var codes: String = a[3]
	var args := []
	var at := 5
	for code in codes:
		match code:
			"b":
				args.append(bool(a[at])); at += 1
			"i":
				args.append(int(a[at])); at += 1
			"f":
				args.append(float(a[at])); at += 1
			"s":
				args.append(str(a[at])); at += 1
			"n":
				args.append(StringName(str(a[at]))); at += 1
			"p":
				args.append(NodePath(str(a[at]))); at += 1
			"o":
				args.append(_object(int(a[at]))); at += 1
			"2":
				args.append(Vector2(a[at], a[at + 1])); at += 2
			"I":
				args.append(Vector2i(int(a[at]), int(a[at + 1]))); at += 2
			"c":
				args.append(Color(a[at], a[at + 1], a[at + 2], a[at + 3])); at += 4
			"r":
				args.append(Rect2(a[at], a[at + 1], a[at + 2], a[at + 3])); at += 4
	var r = o.callv(a[2], args)
	match a[4]:
		"v":
			_js.ret()
		"b":
			_js.ret(bool(r))
		"i":
			_js.ret(int(r))
		"f":
			_js.ret(float(r))
		"s", "n", "p":
			_js.ret(str(r))
		"o":
			_js.ret(handle_of(r))
		"2", "I":
			_js.ret(r.x, r.y)
		"c":
			_js.ret(r.r, r.g, r.b, r.a)
		"r":
			_js.ret(r.position.x, r.position.y, r.size.x, r.size.y)
		_:
			push_error("cs2wasm: a result of kind " + str(a[4]))
			_js.ret()


# ---- the engine calls the module (cs2wasm_node.gd) ----

func is_native_property(o: Object, property: StringName) -> bool:
	var cls := o.get_class()
	if not _native.has(cls):
		var names := {}
		for p in ClassDB.class_get_property_list(cls):
			names[StringName(p.name)] = true
		_native[cls] = names
	return _native[cls].has(property)


func create_instance(node: Node, script_path: String, properties: Dictionary) -> int:
	var h := handle_of(node)
	_js.createInstance(script_path, h)
	for name in properties:
		set_property(h, name, properties[name])
	return h


func set_property(h: int, name: StringName, value) -> bool:
	match typeof(value):
		TYPE_BOOL:
			return _js.setProperty(h, str(name), "b", value)
		TYPE_INT:
			return _js.setProperty(h, str(name), "i", value)
		TYPE_FLOAT:
			return _js.setProperty(h, str(name), "f", value)
		TYPE_STRING, TYPE_STRING_NAME, TYPE_NODE_PATH:
			return _js.setProperty(h, str(name), "s", str(value))
		TYPE_VECTOR2:
			return _js.setProperty(h, str(name), "2", value.x, value.y)
	push_error("cs2wasm: the scene's value of %s is of a type the bridge does not carry (%d)" % [name, typeof(value)])
	return false


func has_method_of(h: int, method: String) -> bool:
	return _js.hasScriptMethod(h, method)


func call_method(h: int, method: String) -> void:
	_js.callScriptMethod(h, method)


func call_method_float(h: int, method: String, x: float) -> void:
	_js.callScriptMethod(h, method, x)
