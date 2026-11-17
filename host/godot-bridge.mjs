// The `godot.*` imports of a module built from Godot C# scripts (stock GodotSharp's NativeFuncs, as cs2wasm compiles
// them), over an engine that is not in the module's address space (SPEC section 10, proof 5). The engine's values
// that live on the module's side (strings, string names, node paths) are kept here; everything that is the engine's
// own (objects, method calls, singletons, printing) goes to a backend:
//
//   backend.call(obj, cls, method, args, sig) -> value   a method bind's ptrcall: args and the result are JavaScript
//                                                        values by their extension_api.json types (see read/write)
//   backend.classOf(obj) -> string                       an object's native class
//   backend.singleton(name) -> obj                       an engine singleton's handle
//   backend.print(text), backend.printError(text)
//
// An object is a number the backend chose (never 0). The backends: the real engine in a browser page, through
// JavaScriptBridge and a GDScript proxy (host/web/godot-web.mjs), and the oracle's log replayed (host/godot-replay.mjs).
// Each ptrcall and print is logged as the oracle's call log has it (tests/Godot/proof5_hello2d/Oracle/CallLog.cs).
// No Node or browser API is used here: the file loads in both.

const dv = (m) => new DataView(m.buffer);

// .NET's "R" formatting of a float and of a double: the shortest text that reads back to the same value
const fmtNum = (x) => { let s = String(x); if (s.includes('e')) s = s.replace('e', 'E'); return s; };
function fmtFloat(x) {
  if (Number.isInteger(x) && Math.abs(x) < 1e15) return Object.is(x, -0) ? '-0' : String(x);
  if (!Number.isFinite(x)) return Number.isNaN(x) ? 'NaN' : x > 0 ? 'Infinity' : '-Infinity';
  for (let p = 1; p <= 9; p++) { const s = Number(x.toPrecision(p)); if (Math.fround(s) === x) return fmtNum(s); }
  return fmtNum(x);
}
const fmtDouble = (x) => (Object.is(x, -0) ? '-0' : !Number.isFinite(x) ? (Number.isNaN(x) ? 'NaN' : x > 0 ? 'Infinity' : '-Infinity') : fmtNum(x));
export const escapeText = (s) => s.replaceAll('\\', '\\\\').replaceAll('\n', '\\n').replaceAll('\r', '\\r');
export const unescapeText = (s) => s.replace(/\\(.)/g, (_, c) => (c === 'n' ? '\n' : c === 'r' ? '\r' : c));

// the float vectors of ptrcall's encoding: how many 32-bit numbers each is
const FLOATS = { Vector2: 2, Vector3: 3, Vector4: 4, Color: 4, Rect2: 4, Quaternion: 4, Plane: 4, Transform2D: 6, AABB: 6, Basis: 9, Transform3D: 12, Projection: 16 };
const INTS = { Vector2i: 2, Vector3i: 3, Vector4i: 4, Rect2i: 4 };
const isEnum = (t) => t.startsWith('enum::') || t.startsWith('bitfield::');
// Variant.Type of the values a script property or a callback argument is given as
export const VARIANT = { NIL: 0, BOOL: 1, INT: 2, FLOAT: 3, STRING: 4, VECTOR2: 5 };

/** The signatures of the engine's methods ("Class.method" -> { args: [type], ret: type | null }) from an
 * extension_api.json's content. */
export function signatures(api) {
  const sigs = new Map();
  for (const c of api.classes) for (const m of c.methods ?? [])
    sigs.set(c.name + '.' + m.name, { args: (m.arguments ?? []).map((a) => a.type), ret: m.return_value?.type ?? null });
  return sigs;
}

export class GodotBoundary {
  constructor(module, sigs, backend, out) {
    this.module = module;
    this.sigs = sigs;        // Map or plain object of signatures
    this.backend = backend;
    this.out = out;          // { log(line) }
    this.names = ['']; this.nameIds = new Map([['', 0]]);   // string names: handle -> text
    this.paths = [''];                                       // node paths: handle -> text (0: the empty path)
    this.strings = new Map();                                // string data address -> text
    this.binds = [null];                                     // method binds: handle -> { cls, name, sig }
    this.objects = new Map();                                // object -> { handle, binding }
    this.nextOwn = 0x70000000;                               // handles of what only this side knows (scripts)
    this.logging = false;
    this.calls = 0;
  }

  attach(instance, memory) { this.x = instance.exports; this.memory = memory; }
  sig(key) { return (this.sigs instanceof Map ? this.sigs.get(key) : this.sigs[key]) ?? null; }
  object(obj) { let o = this.objects.get(obj); if (!o) this.objects.set(obj, o = {}); return o; }

  // ---- engine values in the module's memory ----
  name(text) {
    let id = this.nameIds.get(text);
    if (id === undefined) { id = this.names.length; this.names.push(text); this.nameIds.set(text, id); }
    return id;
  }
  readName(p) { return this.names[dv(this.memory).getUint32(p, true)]; }
  path(text) { if (text === '') return 0; this.paths.push(text); return this.paths.length - 1; }
  readPath(p) { return this.paths[dv(this.memory).getUint32(p, true)] ?? ''; }
  newString(text, dest) {
    const cps = [...text].map((c) => c.codePointAt(0));
    const p = this.x.godot_alloc(4 * (cps.length + 1));
    const d = dv(this.memory);
    cps.forEach((c, i) => d.setUint32(p + 4 * i, c, true));
    d.setUint32(p + 4 * cps.length, 0, true);
    this.strings.set(p, text);
    d.setUint32(dest, p, true);
  }
  freeString(p) { const a = dv(this.memory).getUint32(p, true); if (a) { this.strings.delete(a); this.x.godot_free(a); } }
  readString(p) { const a = dv(this.memory).getUint32(p, true); return a === 0 ? '' : this.strings.get(a); }

  /** A value in ptrcall's encoding at p, as a JavaScript value: bool a boolean; int and enums a number; float a
   * number; the float and int vectors arrays of numbers; String, StringName and NodePath a string; an object its
   * handle (0: null). */
  read(type, p) {
    const m = dv(this.memory);
    switch (type) {
      case 'bool': return m.getUint8(p) !== 0;
      case 'int': return Number(m.getBigInt64(p, true));
      case 'float': return m.getFloat64(p, true);
      case 'String': return this.readString(p);
      case 'StringName': return this.readName(p);
      case 'NodePath': return this.readPath(p);
    }
    if (FLOATS[type]) return Array.from({ length: FLOATS[type] }, (_, i) => m.getFloat32(p + 4 * i, true));
    if (INTS[type]) return Array.from({ length: INTS[type] }, (_, i) => m.getInt32(p + 4 * i, true));
    if (isEnum(type)) return Number(m.getBigInt64(p, true));
    if (this.isObjectType(type)) return m.getUint32(p, true);
    throw new Error(`godot bridge: a ${type} argument is not carried yet`);
  }
  write(type, p, v) {
    const m = dv(this.memory);
    switch (type) {
      case 'bool': m.setUint8(p, v ? 1 : 0); return;
      case 'int': m.setBigInt64(p, BigInt(Math.trunc(v)), true); return;
      case 'float': m.setFloat64(p, v, true); return;
      case 'String': this.newString(v, p); return;
      case 'StringName': m.setUint32(p, this.name(v), true); return;
      case 'NodePath': m.setUint32(p, this.path(v), true); return;
    }
    if (FLOATS[type]) { for (let i = 0; i < FLOATS[type]; i++) m.setFloat32(p + 4 * i, v[i], true); return; }
    if (INTS[type]) { for (let i = 0; i < INTS[type]; i++) m.setInt32(p + 4 * i, v[i], true); return; }
    if (isEnum(type)) { m.setBigInt64(p, BigInt(Math.trunc(v)), true); return; }
    if (this.isObjectType(type)) { m.setUint32(p, v >>> 0, true); return; }
    throw new Error(`godot bridge: a ${type} result is not carried yet`);
  }
  // (a class of the engine; the variant types that are not carried are named so the error says so)
  isObjectType(type) {
    return !['Variant', 'Array', 'Dictionary', 'Callable', 'Signal', 'RID'].includes(type) && !type.startsWith('Packed') && !type.startsWith('typedarray::') && !type.startsWith('typeddictionary::');
  }
  /** The oracle's text of a value (Oracle/CallLog.cs: Format). */
  format(type, p) {
    const m = dv(this.memory);
    switch (type) {
      case 'bool': return m.getUint8(p) ? 'true' : 'false';
      case 'int': return m.getBigInt64(p, true).toString();
      case 'float': return fmtDouble(m.getFloat64(p, true));
      case 'Vector2': return `(${fmtFloat(m.getFloat32(p, true))}, ${fmtFloat(m.getFloat32(p + 4, true))})`;
      case 'Vector2i': return `(${m.getInt32(p, true)}, ${m.getInt32(p + 4, true)})`;
      case 'Color': return `(${[0, 4, 8, 12].map((o) => fmtFloat(m.getFloat32(p + o, true))).join(', ')})`;
      case 'String': return `"${escapeText(this.readString(p))}"`;
      case 'StringName': return `&"${this.readName(p)}"`;
      case 'NodePath': return `^"${this.readPath(p)}"`;
    }
    if (isEnum(type)) return m.getBigInt64(p, true).toString();
    return m.getUint32(p, true) === 0 ? 'null' : `<${type}>`;
  }

  // ---- the imports ----
  imports() {
    const g = {};
    const self = this;
    const d = () => dv(this.memory);
    const u32 = (p) => d().getUint32(p, true);
    g.godotsharp_string_new_with_utf16_chars = (dest, chars) => {
      const m = d(); let s = '';
      for (let i = chars; ; i += 2) { const c = m.getUint16(i, true); if (c === 0) break; s += String.fromCharCode(c); }
      self.newString(s, dest);
    };
    g.godotsharp_string_size = (s) => { const t = self.readString(s); return BigInt(t === '' && u32(s) === 0 ? 0 : [...t].length + 1); };
    g.godotsharp_string_destroy = (s) => { const a = u32(s); if (a) { self.strings.delete(a); self.x.godot_free(a); } };
    g.godotsharp_string_name_new_from_string = (dest, s) => d().setUint32(dest, self.name(self.readString(s)), true);
    g.godotsharp_string_name_new_copy = (dest, src) => d().setUint32(dest, u32(src), true);
    g.godotsharp_string_name_as_string = (dest, n) => self.newString(self.readName(n), dest);
    g.godotsharp_string_name_destroy = () => {};
    g.godotsharp_node_path_new_from_string = (dest, s) => d().setUint32(dest, self.path(self.readString(s)), true);
    g.godotsharp_node_path_new_copy = (dest, src) => d().setUint32(dest, u32(src), true);
    g.godotsharp_node_path_as_string = (dest, n) => self.newString(self.readPath(n), dest);
    g.godotsharp_node_path_equals = (a, b) => (self.readPath(a) === self.readPath(b) ? 1 : 0);
    g.godotsharp_node_path_destroy = () => {};
    g.godotsharp_method_bind_get_method_with_compatibility = (cls, method, hash) => {
      const c = self.readName(cls), n = self.readName(method);
      self.binds.push({ cls: c, name: n, sig: self.sig(c + '.' + n) ?? { args: [], ret: null } });
      return self.binds.length - 1;
    };
    g.godotsharp_get_class_constructor = (cls) => self.name(self.readName(cls)); // (non-zero: a script never calls it)
    g.godotsharp_engine_get_singleton = (n) => self.backend.singleton(self.readString(n));
    g.godotsharp_print = (s) => { const t = self.readString(s); if (self.logging) self.out.log('print ' + escapeText(t)); self.backend.print(t); };
    g.godotsharp_method_bind_ptrcall = (bind, obj, args, ret) => self.ptrcall(bind, obj, args, ret);
    // instances and their managed counterparts
    g.godotsharp_internal_tie_user_managed_to_unmanaged = (gch, obj, script, rc) => { self.object(obj).handle = gch; };
    g.godotsharp_internal_tie_native_managed_to_unmanaged = (gch, obj, nativeName, rc) => { self.object(obj).binding = gch; };
    g.godotsharp_internal_tie_managed_to_unmanaged_with_pre_setup = (gch, obj) => { self.object(obj).handle = gch; };
    g.godotsharp_internal_unmanaged_get_script_instance_managed = (obj, hasScript) => {
      const o = self.objects.get(obj); d().setUint8(hasScript, o?.handle ? 1 : 0); return o?.handle ?? 0;
    };
    g.godotsharp_internal_unmanaged_get_instance_binding_managed = (obj) => self.objects.get(obj)?.binding ?? 0;
    g.godotsharp_internal_unmanaged_instance_binding_create_managed = (obj, old) => {
      const o = self.object(obj);
      if (!o.binding) {
        const p = self.x.godot_alloc(4); d().setUint32(p, self.name(self.backend.classOf(obj)), true);
        o.binding = self.x.ScriptManagerBridge_CreateManagedForGodotObjectBinding(p, obj);
        self.x.godot_free(p);
      }
      return o.binding;
    };
    g.godotsharp_internal_object_disposed = () => {};
    g.godotsharp_internal_refcounted_disposed = () => {};
    g.godotsharp_internal_object_get_associated_gchandle = (obj) => self.objects.get(obj)?.handle ?? self.objects.get(obj)?.binding ?? 0;
    g.godotsharp_internal_script_debugger_is_active = () => 0;
    g.godotsharp_dotnet_module_is_initialized = () => 1;
    g.godotsharp_err_print_error = (fn, file, line, err, descr, editorNotify, type) => {
      const e = self.readString(err), ds = self.readString(descr);
      self.backend.printError(`${e}${ds ? ' ' + ds : ''}\n   at: ${self.readString(fn)} (${self.readString(file)}:${line})`);
    };
    // anything else: not carried by this bridge yet
    for (const imp of WebAssembly.Module.imports(this.module))
      if (imp.module === 'godot' && !g[imp.name]) g[imp.name] = () => { throw new Error(`godot bridge: godot.${imp.name} is not implemented`); };
    return g;
  }

  ptrcall(bind, obj, args, ret) {
    const { cls, name, sig } = this.binds[bind];
    const m = dv(this.memory);
    const argAt = (i) => m.getUint32(args + 4 * i, true);
    let line = this.logging ? `${cls}.${name}(${sig.args.map((t, i) => this.format(t, argAt(i))).join(', ')})` : null;
    this.calls++;
    const value = this.backend.call(obj, cls, name, sig.args.map((t, i) => this.read(t, argAt(i))), sig, line);
    if (sig.ret && ret) {
      this.write(sig.ret, ret, value);
      if (line !== null) line += ' -> ' + this.format(sig.ret, ret);
    }
    if (line !== null) this.out.log(line);
  }

  // ---- the engine's side: what a host tells the module ----
  /** Stock GodotPlugins' start. The log starts here: stock Godot makes its start-up calls before the oracle's
   * logger (in the game's assembly) is installed. */
  initialize() {
    if (!this.x.godot_initialize()) throw new Error('godot_initialize failed');
    this.logging = true;
  }
  /** The script at a res:// path: its handle. */
  script(path) {
    this.scripts ??= new Map();
    if (this.scripts.has(path)) return this.scripts.get(path);
    const script = this.nextOwn; this.nextOwn += 16;
    const p = this.x.godot_alloc(4); this.newString(path, p);
    const ok = this.x.ScriptManagerBridge_AddScriptBridge(script, p);
    this.freeString(p); this.x.godot_free(p);
    if (!ok) throw new Error('no script at ' + path);
    this.scripts.set(path, script);
    return script;
  }
  /** A script's instance on an engine object. */
  createInstance(scriptPath, obj) {
    if (!this.x.ScriptManagerBridge_CreateManagedForGodotObjectScriptInstance(this.script(scriptPath), obj, 0, 0)) throw new Error('script instance not created: ' + scriptPath);
    return this.objects.get(obj)?.handle ?? 0;
  }
  // a Variant in the module's memory from a JavaScript value: { type, value } or a boolean, a number (a float) or a string
  variant(v) {
    const p = this.x.godot_alloc(24), m = dv(this.memory);
    m.setBigUint64(p, 0n, true); m.setBigUint64(p + 8, 0n, true); m.setBigUint64(p + 16, 0n, true);
    const t = v?.type ?? (typeof v === 'boolean' ? VARIANT.BOOL : typeof v === 'number' ? VARIANT.FLOAT : typeof v === 'string' ? VARIANT.STRING : VARIANT.NIL);
    const val = v?.type !== undefined ? v.value : v;
    m.setUint32(p, t, true);
    if (t === VARIANT.BOOL) m.setUint8(p + 8, val ? 1 : 0);
    else if (t === VARIANT.INT) m.setBigInt64(p + 8, BigInt(val), true);
    else if (t === VARIANT.FLOAT) m.setFloat64(p + 8, val, true);
    else if (t === VARIANT.STRING) this.newString(val, p + 8);
    else if (t === VARIANT.VECTOR2) { m.setFloat32(p + 8, val[0], true); m.setFloat32(p + 12, val[1], true); }
    else if (t !== VARIANT.NIL) throw new Error('godot bridge: a Variant of type ' + t + ' is not carried yet');
    return p;
  }
  freeVariant(p) {
    const m = dv(this.memory);
    if (m.getUint32(p, true) === VARIANT.STRING) { const a = m.getUint32(p + 8, true); if (a) { this.strings.delete(a); this.x.godot_free(a); } }
    this.x.godot_free(p);
  }
  /** Sets a script property of the instance on obj (the scene's value of an exported field): whether it has it. */
  setProperty(obj, name, value) {
    const handle = this.objects.get(obj)?.handle;
    if (!handle) return false;
    const n = this.x.godot_alloc(4), v = this.variant(value);
    dv(this.memory).setUint32(n, this.name(name), true);
    const r = this.x.CSharpInstanceBridge_Set(handle, n, v);
    this.freeVariant(v); this.x.godot_free(n);
    return r !== 0;
  }
  /** Calls a method of the script instance on obj (_ready, _process...): whether the script has it. */
  callMethod(obj, method, args = []) {
    const handle = this.objects.get(obj)?.handle;
    if (!handle) return false;
    const x = this.x;
    this.scratch ??= { callError: x.godot_alloc(12), ret: x.godot_alloc(24), name: x.godot_alloc(4), argv: x.godot_alloc(4 * 8) };
    const s = this.scratch, vs = args.map((a) => this.variant(a));
    let m = dv(this.memory);
    m.setUint32(s.name, this.name(method), true);
    vs.forEach((v, i) => m.setUint32(s.argv + 4 * i, v, true));
    m.setBigUint64(s.ret, 0n, true);
    const r = x.CSharpInstanceBridge_Call(handle, s.name, vs.length ? s.argv : 0, vs.length, s.callError, s.ret);
    vs.forEach((v) => this.freeVariant(v));
    return r !== 0;
  }
  hasMethod(obj, method) {
    const handle = this.objects.get(obj)?.handle;
    if (!handle) return false;
    const n = this.x.godot_alloc(4);
    dv(this.memory).setUint32(n, this.name(method), true);
    const r = this.x.CSharpInstanceBridge_HasMethodUnknownParams(handle, n);
    this.x.godot_free(n);
    return r !== 0;
  }
  shutdown() { this.x.DisposablesTracker_OnGodotShuttingDown(); }
}

/** A call-log line as it is compared: what depends on where the runtime's sources were built is left out (the
 * file an error is reported at: stock GodotSharp's is its build machine's path of ExceptionUtils.cs). */
export function comparable(line) {
  return line.startsWith('ProjectSettings.localize_path(') ? 'ProjectSettings.localize_path(<a source file>)' : line;
}
