// A stub Godot engine for modules built from Godot scripts (SPEC section 10, proof 3): it supplies the `godot.*`
// imports (stock GodotSharp's NativeFuncs, as cs2wasm compiles them) with just enough of the engine for a Node2D
// script, logs each engine call (method-bind ptrcalls and prints) in the format of the oracle's call log
// (tests/Godot/proof3_node2d/Oracle/CallLog.cs), and drives the script as the engine does: load the script, create
// the instance on a native Node2D, _ready, then _process every frame until the tree quits, then shut down.
import { readFileSync } from 'node:fs';

const u8 = (m) => new Uint8Array(m.buffer);
const dv = (m) => new DataView(m.buffer);

// .NET's "R" formatting: the shortest text that reads back to the same float or double
function fmtFloat(x) {
  if (Number.isInteger(x) && Math.abs(x) < 1e15) return String(x);
  for (let p = 1; p <= 9; p++) { const s = Number(x.toPrecision(p)); if (Math.fround(s) === x) return fmtNum(s); }
  return fmtNum(x);
}
const fmtNum = (x) => { let s = String(x); if (s.includes('e')) s = s.replace('e', 'E').replace('E+', 'E+'); return s; };

export class GodotStub {
  constructor(module, apiPath, out) {
    this.module = module;
    this.out = out;          // { log(line), write(text) }
    this.api = new Map();    // "Class.method" -> { args: [type], ret: type | null }
    const api = JSON.parse(readFileSync(apiPath, 'utf8'));
    for (const c of api.classes) for (const m of c.methods ?? [])
      this.api.set(c.name + '.' + m.name, { args: (m.arguments ?? []).map(a => a.type), ret: m.return_value?.type ?? null });
    this.names = ['']; this.nameIds = new Map([['', 0]]);   // string names: handle -> text
    this.strings = new Map();                                // string data address -> text
    this.binds = [null];                                     // method binds: handle -> { cls, name }
    this.objects = new Map();                                // object address -> { cls, state }
    this.nextObject = 0x1000;
    this.quit = false;
  }

  attach(instance, memory) { this.x = instance.exports; this.memory = memory; }

  // ---- engine values in the module's memory ----
  name(text) {
    let id = this.nameIds.get(text);
    if (id === undefined) { id = this.names.length; this.names.push(text); this.nameIds.set(text, id); }
    return id;
  }
  readName(p) { return this.names[dv(this.memory).getUint32(p, true)]; }
  newString(text, dest) {
    const cps = [...text].map(c => c.codePointAt(0));
    const p = this.x.godot_alloc(4 * (cps.length + 1));
    const d = dv(this.memory);
    cps.forEach((c, i) => d.setUint32(p + 4 * i, c, true));
    d.setUint32(p + 4 * cps.length, 0, true);
    this.strings.set(p, text);
    d.setUint32(dest, p, true);
  }
  readString(p) { const a = dv(this.memory).getUint32(p, true); return a === 0 ? '' : this.strings.get(a); }
  newObject(cls) { const p = this.nextObject; this.nextObject += 16; this.objects.set(p, { cls, state: { position: [0, 0], rotation: 0, scale: [1, 1] } }); return p; }

  // ---- the imports ----
  imports() {
    const g = {};
    const self = this;
    const d = () => dv(this.memory);
    g.godotsharp_string_new_with_utf16_chars = (dest, chars) => {
      const m = d(); let s = '';
      for (let i = chars; ; i += 2) { const c = m.getUint16(i, true); if (c === 0) break; s += String.fromCharCode(c); }
      self.newString(s, dest);
    };
    g.godotsharp_string_size = (s) => { const t = self.readString(s); return BigInt(t === '' && d().getUint32(s, true) === 0 ? 0 : [...t].length + 1); };
    g.godotsharp_string_destroy = (s) => { const a = d().getUint32(s, true); if (a) { self.strings.delete(a); self.x.godot_free(a); } };
    g.godotsharp_string_name_new_from_string = (dest, s) => d().setUint32(dest, self.name(self.readString(s)), true);
    g.godotsharp_string_name_new_copy = (dest, src) => d().setUint32(dest, d().getUint32(src, true), true);
    g.godotsharp_string_name_as_string = (dest, n) => self.newString(self.readName(n), dest);
    g.godotsharp_string_name_destroy = () => {};
    g.godotsharp_method_bind_get_method_with_compatibility = (cls, method, hash) => {
      self.binds.push({ cls: self.readName(cls), name: self.readName(method) });
      return self.binds.length - 1;
    };
    g.godotsharp_get_class_constructor = (cls) => self.name(self.readName(cls)); // (non-zero: the script path never calls it)
    g.godotsharp_engine_get_singleton = (n) => self.singleton(self.readString(n));
    g.godotsharp_print = (s) => { const t = self.readString(s); if (self.logging) self.out.log('print ' + t); self.out.write(t + '\n'); };
    g.godotsharp_method_bind_ptrcall = (bind, obj, args, ret) => self.ptrcall(bind, obj, args, ret);
    // instances and their managed counterparts
    g.godotsharp_internal_tie_user_managed_to_unmanaged = (gch, obj, script, rc) => { self.objects.get(obj).handle = gch; };
    g.godotsharp_internal_tie_native_managed_to_unmanaged = (gch, obj, nativeName, rc) => { self.objects.get(obj).binding = gch; };
    g.godotsharp_internal_tie_managed_to_unmanaged_with_pre_setup = (gch, obj) => { self.objects.get(obj).handle = gch; };
    g.godotsharp_internal_unmanaged_get_script_instance_managed = (obj, hasScript) => {
      const o = self.objects.get(obj); u8(self.memory)[hasScript] = o?.handle ? 1 : 0; return o?.handle ?? 0;
    };
    g.godotsharp_internal_unmanaged_get_instance_binding_managed = (obj) => self.objects.get(obj)?.binding ?? 0;
    g.godotsharp_internal_unmanaged_instance_binding_create_managed = (obj, old) => {
      const o = self.objects.get(obj);
      if (!o.binding) {
        const p = self.x.godot_alloc(4); d().setUint32(p, self.name(o.cls), true);
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
      self.out.write(`ERROR: ${self.readString(err)} ${self.readString(descr)}\n`);
    };
    // anything else: not part of this stub engine yet
    for (const imp of WebAssembly.Module.imports(this.module))
      if (imp.module === 'godot' && !g[imp.name]) g[imp.name] = (...a) => { throw new Error(`stub engine: godot.${imp.name} is not implemented`); };
    // GODOT_TRACE=1: every import call, to stderr
    if (process.env.GODOT_TRACE === '1')
      for (const [k, f] of Object.entries(g)) g[k] = (...a) => { process.stderr.write(`[godot] ${k}(${a.join(', ')})\n`); return f(...a); };
    return g;
  }

  singleton(name) {
    this.singletons ??= new Map();
    if (!this.singletons.has(name)) this.singletons.set(name, this.newObject(name));
    return this.singletons.get(name);
  }

  // ---- method binds: the few the proof's Node2D uses, with Godot's own arithmetic (real_t is float) ----
  ptrcall(bind, obj, args, ret) {
    const { cls, name } = this.binds[bind];
    const sig = this.api.get(cls + '.' + name) ?? { args: [], ret: null };
    const m = dv(this.memory);
    const argAt = (i) => m.getUint32(args + 4 * i, true);
    const st = this.objects.get(obj).state;
    let line = `${cls}.${name}(${sig.args.map((t, i) => this.format(t, argAt(i))).join(', ')})`;
    const v2 = (p) => [m.getFloat32(p, true), m.getFloat32(p + 4, true)];
    const setV2 = (p, v) => { m.setFloat32(p, v[0], true); m.setFloat32(p + 4, v[1], true); };
    switch (cls + '.' + name) {
      case 'Node2D.set_position': st.position = v2(argAt(0)); break;
      case 'Node2D.get_position': setV2(ret, st.position); break;
      case 'Node2D.set_scale': st.scale = v2(argAt(0)); break;
      case 'Node2D.get_scale': setV2(ret, st.scale); break;
      case 'Node2D.set_rotation': st.rotation = Math.fround(m.getFloat64(argAt(0), true)); break;
      case 'Node2D.get_rotation': m.setFloat64(ret, st.rotation, true); break;
      case 'Node2D.set_rotation_degrees': {
        const deg = Math.fround(m.getFloat64(argAt(0), true));
        st.rotation = Math.fround(deg * Math.fround(Math.PI / 180)); break;
      }
      case 'Node.get_tree': m.setUint32(ret, this.singleton('SceneTree'), true); break;
      case 'SceneTree.quit': this.quit = true; break;
      case 'OS.is_stdout_verbose': m.setUint8(ret, 0); break;
      case 'Engine.is_editor_hint': m.setUint8(ret, 0); break;
      default: throw new Error(`stub engine: ${cls}.${name} is not implemented`);
    }
    if (sig.ret && ret) line += ' -> ' + this.format(sig.ret, ret);
    // (the log starts with the scene: stock Godot makes its start-up calls before the oracle's logger is installed)
    if (this.logging) this.out.log(line);
  }

  format(type, p) {
    const m = dv(this.memory);
    switch (type) {
      case 'bool': return m.getUint8(p) ? 'true' : 'false';
      case 'int': return m.getBigInt64(p, true).toString();
      case 'float': return fmtNum(m.getFloat64(p, true));
      case 'Vector2': return `(${fmtFloat(m.getFloat32(p, true))}, ${fmtFloat(m.getFloat32(p + 4, true))})`;
      case 'String': return `"${this.readString(p)}"`;
      case 'StringName': return `&"${this.readName(p)}"`;
    }
    if (type.startsWith('enum::') || type.startsWith('bitfield::')) return m.getBigInt64(p, true).toString();
    return m.getUint32(p, true) === 0 ? 'null' : `<${type}>`;
  }

  // ---- the engine's side of a scene with one scripted Node2D ----
  run(scriptPath, nodeClass, frames = 1000) {
    const x = this.x, m = () => dv(this.memory);
    if (!x.godot_initialize()) throw new Error('godot_initialize failed');
    const script = this.newObject('CSharpScript');
    const path = x.godot_alloc(4); this.newString(scriptPath, path);
    if (!x.ScriptManagerBridge_AddScriptBridge(script, path)) throw new Error('no script at ' + scriptPath);
    const node = this.newObject(nodeClass);
    if (!x.ScriptManagerBridge_CreateManagedForGodotObjectScriptInstance(script, node, 0, 0)) throw new Error('script instance not created');
    const handle = this.objects.get(node).handle;
    this.logging = true;
    const callError = x.godot_alloc(12), retVariant = x.godot_alloc(24), nameP = x.godot_alloc(4);
    const call = (method, argv) => {
      m().setUint32(nameP, this.name(method), true);
      x.CSharpInstanceBridge_Call(handle, nameP, argv, argv ? 1 : 0, callError, retVariant);
    };
    call('_ready', 0);
    // _process(double delta): one Variant argument (type FLOAT = 3, the double at offset 8)
    const variant = x.godot_alloc(24), argv = x.godot_alloc(4);
    m().setUint32(variant, 3, true); m().setFloat64(variant + 8, 1 / 60, true); m().setUint32(argv, variant, true);
    for (let f = 0; f < frames && !this.quit; f++) call('_process', argv);
    x.DisposablesTracker_OnGodotShuttingDown();
  }
}
