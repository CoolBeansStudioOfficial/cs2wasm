// The page's end of the bridge between an il2wasm module and stock Godot's web export (SPEC section 10, proof 5): the
// backend of host/godot-bridge.mjs on the real engine. The module's `godot.*` imports reach the engine through the
// callback the exported project's autoload hands over (host/web/godot/il2wasm_bridge.gd, by JavaScriptBridge); the
// engine's callbacks reach the module through window.il2wasmEngine. Everything is synchronous on the page's thread
// (the export's build without threads), so a script's call of the engine returns its answer as under stock Godot.
import { GodotBoundary, VARIANT } from '../godot-bridge.mjs';

// extension_api.json types as the proxy's value codes (il2wasm_bridge.gd)
const CODES = { bool: 'b', int: 'i', float: 'f', String: 's', StringName: 'n', NodePath: 'p', Vector2: '2', Vector2i: 'I', Color: 'c', Rect2: 'r' };
const notCarried = ['Variant', 'Array', 'Dictionary', 'Callable', 'Signal', 'RID'];
function code(type) {
  if (CODES[type]) return CODES[type];
  if (type.startsWith('enum::') || type.startsWith('bitfield::')) return 'i';
  if (notCarried.includes(type) || type.startsWith('Packed') || type.startsWith('typed') || /^(Vector|Transform|Basis|Quaternion|Plane|AABB|Projection|Rect2i)/.test(type))
    throw new Error(`godot bridge: a ${type} is not carried to the engine yet`);
  return 'o';
}

export class WebEngineBackend {
  constructor(out) {
    this.out = out;       // { print(text), printError(text) }
    this.proxy = null;    // the engine's callback
    this.answer = [];
    this.round = 0;       // engine calls made (each a round trip through JavaScriptBridge)
  }
  ask(...a) {
    if (!this.proxy) throw new Error('godot bridge: the engine has not attached');
    this.answer = [];
    this.round++;
    this.proxy(...a);
    return this.answer;
  }
  call(obj, cls, method, args, sig) {
    const codes = sig.args.map(code).join(''), rc = sig.ret ? code(sig.ret) : 'v';
    const r = this.ask('call', obj, method, codes, rc, ...args.flat());
    if (rc === 'v') return undefined;
    if (rc === '2' || rc === 'I' || rc === 'c' || rc === 'r') return r;
    if (rc === 'b') return !!r[0];
    return r[0];
  }
  classOf(obj) { return this.ask('class', obj)[0]; }
  singleton(name) { return this.ask('singleton', name)[0]; }
  print(text) { this.out.print(text); }
  printError(text) { this.out.printError(text); }
}

/** Sets up window.il2wasmEngine for a module (instantiated, init run) before the engine starts. Returns
 * { bridge, backend, log }: log is the engine-call log's lines. makeInstance(godot imports) resolves with
 * { instance, memory }. */
export async function attachGame(module, sigs, out, makeInstance) {
  const log = [];
  const backend = new WebEngineBackend(out);
  const bridge = new GodotBoundary(module, sigs, backend, { log: (l) => log.push(l) });
  const { instance, memory } = await makeInstance(bridge.imports());
  bridge.attach(instance, memory);
  instance.exports.init();
  const variantOf = (kind, a, b) => (kind === 'b' ? { type: VARIANT.BOOL, value: !!a } : kind === 'i' ? { type: VARIANT.INT, value: a }
    : kind === 'f' ? { type: VARIANT.FLOAT, value: a } : kind === 's' ? { type: VARIANT.STRING, value: String(a) } : { type: VARIANT.VECTOR2, value: [a, b] });
  const guard = (f) => (...a) => { try { return f(...a); } catch (e) { out.printError('il2wasm bridge: ' + (e?.stack ?? e)); out.failed?.(e); return 0; } };
  globalThis.il2wasmEngine = {
    // the engine's autoload is up: the module's start (it asks the engine already)
    attach: guard((callback) => { backend.proxy = callback; bridge.initialize(); }),
    ret: (...values) => { backend.answer = values; },
    createInstance: guard((scriptPath, obj) => bridge.createInstance(scriptPath, obj)),
    setProperty: guard((obj, name, kind, a, b) => bridge.setProperty(obj, name, variantOf(kind, a, b))),
    hasScriptMethod: guard((obj, method) => bridge.hasMethod(obj, method)),
    // the game quits: the module's shutdown, then the host is told (out.ended) with the log complete
    shutdown: guard(() => { bridge.shutdown(); bridge.logging = false; out.ended?.(); }),
    // (a callback's argument is a float: _process and _physics_process's delta)
    callScriptMethod: guard((obj, method, ...args) => bridge.callMethod(obj, method, args.map((x) => ({ type: VARIANT.FLOAT, value: x })))),
  };
  return { bridge, backend, log, instance, memory };
}
