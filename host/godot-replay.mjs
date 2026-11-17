// A backend of host/godot-bridge.mjs with no engine: the oracle's call log replayed (SPEC section 10, proof 5, the
// step before the browser). The module's calls must be the oracle's, one by one; each call's answer is the oracle's
// (what stock Godot returned to the same script). So this checks what the compiled scripts ask of the engine given the
// engine's answers, and isolates compiler and library gaps from the browser's part; it does not check an engine.
// Also here: reading a .tscn (the nodes, their scripts and properties), for this backend and for the web variant.
import { unescapeText, comparable, VARIANT } from './godot-bridge.mjs';

/** The nodes of a scene file, in file order: { name, type, parent (path, '' for the root's children, null for the
 * root), script (res:// path or null), props: [[name, text]] }, and its external resources by id. */
export function parseScene(text) {
  const ext = new Map(), nodes = [];
  let node = null;
  for (const raw of text.split('\n')) {
    const line = raw.replace(/\r$/, '');
    let m;
    if ((m = /^\[ext_resource (.*)\]$/.exec(line))) {
      const a = Object.fromEntries([...m[1].matchAll(/(\w+)="([^"]*)"/g)].map((x) => [x[1], x[2]]));
      ext.set(a.id, a); node = null;
    } else if ((m = /^\[node (.*)\]$/.exec(line))) {
      const a = Object.fromEntries([...m[1].matchAll(/(\w+)="([^"]*)"/g)].map((x) => [x[1], x[2]]));
      node = { name: a.name, type: a.type, parent: a.parent === undefined ? null : a.parent === '.' ? '' : a.parent, script: null, props: [] };
      nodes.push(node);
    } else if (line.startsWith('[')) node = null;
    else if (node && (m = /^([\w/]+) = (.*)$/.exec(line))) {
      if (m[1] === 'script') { const id = /ExtResource\("([^"]+)"\)/.exec(m[2])?.[1]; node.script = ext.get(id)?.path ?? null; }
      else node.props.push([m[1], m[2]]);
    }
  }
  for (const n of nodes) n.path = n.parent === null ? '.' : n.parent === '' ? n.name : n.parent + '/' + n.name;
  return { ext, nodes };
}

/** A scene file's property value as a Variant for the bridge, or undefined when it is not a kind a script property
 * is given as here. */
export function sceneValue(text) {
  let m;
  if (text === 'true' || text === 'false') return { type: VARIANT.BOOL, value: text === 'true' };
  if (/^-?\d+$/.test(text)) return { type: VARIANT.INT, value: Number(text) };
  if (/^-?(\d+\.\d*|\.\d+|\d+)(e[-+]?\d+)?$/i.test(text)) return { type: VARIANT.FLOAT, value: Number(text) };
  if ((m = /^"((?:[^"\\]|\\.)*)"$/.exec(text))) return { type: VARIANT.STRING, value: unescapeText(m[1]) };
  if ((m = /^Vector2\((.*),(.*)\)$/.exec(text))) return { type: VARIANT.VECTOR2, value: [Number(m[1]), Number(m[2])] };
  return undefined;
}

export class ReplayMismatch extends Error {}

export class ReplayBackend {
  /** expected: the oracle's lines; scene: parseScene's result. */
  constructor(expected, scene, out) {
    this.expected = expected;
    this.at = 0;
    this.out = out; // { write(text) }
    this.nodes = scene.nodes.map((n, i) => ({ ...n, obj: 0x1000 + 16 * i, children: [] }));
    this.byPath = new Map(this.nodes.map((n) => [n.path, n]));
    for (const n of this.nodes) if (n.parent !== null) { n.up = this.byPath.get(n.parent === '' ? '.' : n.parent); n.up.children.push(n); }
    this.byObj = new Map(this.nodes.map((n) => [n.obj, n]));
    this.others = new Map(); // objects that are not nodes of the scene: handle -> class
    this.next = 0x100000;
    this.singletons = new Map();
  }
  newObject(cls) { const o = this.next; this.next += 16; this.others.set(o, cls); return o; }
  classOf(obj) { return this.byObj.get(obj)?.type ?? this.others.get(obj); }
  singleton(name) { if (!this.singletons.has(name)) this.singletons.set(name, this.newObject(name)); return this.singletons.get(name); }
  print(text) { this.out.write(text + '\n'); }
  printError(text) { this.out.write('ERROR: ' + text + '\n'); }

  // a node path from a node of the scene (as Node.get_node resolves it; the scene's root has no parent here)
  resolve(from, path) {
    let n = from;
    for (const part of path.split('/')) {
      if (part === '' || part === '.') continue;
      n = part === '..' ? n?.up : n?.children.find((c) => c.name === part);
      if (!n) return 0;
    }
    return n.obj;
  }

  call(obj, cls, method, args, sig, line) {
    // before the log starts (the module's start-up, which stock Godot makes before the oracle's logger is there):
    // the answers a game outside the editor gets
    if (line === null) {
      if (cls + '.' + method === 'Engine.is_editor_hint' || cls + '.' + method === 'OS.is_stdout_verbose') return false;
      throw new Error(`replay: ${cls}.${method} at start-up has no answer here`);
    }
    while (this.at < this.expected.length && this.expected[this.at].startsWith('print ')) this.at++;
    const want = this.expected[this.at];
    if (want === undefined) throw new ReplayMismatch(`a call after the oracle's last: ${line}`);
    const arrow = sig.ret ? want.lastIndexOf(' -> ') : -1;
    const wantCall = arrow < 0 ? want : want.slice(0, arrow);
    if (comparable(wantCall) !== comparable(line)) throw new ReplayMismatch(`line ${this.at + 1}: stock «${wantCall}»\n           module «${line}»`);
    this.at++;
    if (!sig.ret) return undefined;
    return this.parse(sig.ret, want.slice(arrow + 4), obj, method, args);
  }

  // the oracle's text of a result as the bridge's value
  parse(type, text, obj, method, args) {
    switch (type) {
      case 'bool': return text === 'true';
      case 'int': return Number(text);
      case 'float': return Number(text);
      case 'String': return unescapeText(text.slice(1, -1));
      case 'StringName': return text.slice(2, -1);
      case 'NodePath': return text.slice(2, -1);
      case 'Vector2': case 'Vector2i': case 'Color': return text.slice(1, -1).split(', ').map(Number);
    }
    if (type.startsWith('enum::') || type.startsWith('bitfield::')) return Number(text);
    if (text === 'null') return 0;
    // an object: the log says only that there is one; a node asked for by path is the scene's
    if ((method === 'get_node' || method === 'get_node_or_null') && this.byObj.has(obj)) {
      const found = this.resolve(this.byObj.get(obj), args[0]);
      if (found) return found;
    }
    this.made ??= new Map();
    const key = obj + '.' + method;
    if (!this.made.has(key)) this.made.set(key, this.newObject(type));
    return this.made.get(key);
  }
}

/** Plays the scene as the engine does, for a number of frames at one physics tick a frame: the scripts' instances
 * and the scene's values of their properties, _ready (children first), then each frame _physics_process and
 * _process in tree order, then the shutdown. */
export function playScene(bridge, backend, frames, fps = 60) {
  bridge.initialize();
  const scripted = backend.nodes.filter((n) => n.script);
  for (const n of scripted) {
    bridge.createInstance(n.script, n.obj);
    for (const [name, text] of n.props) { const v = sceneValue(text); if (v !== undefined) bridge.setProperty(n.obj, name, v); }
  }
  const ready = (n) => { n.children.forEach(ready); if (n.script) bridge.callMethod(n.obj, '_ready'); };
  ready(backend.nodes.find((n) => n.parent === null));
  const delta = { type: VARIANT.FLOAT, value: 1 / fps };
  for (let f = 0; f < frames; f++) {
    for (const n of scripted) bridge.callMethod(n.obj, '_physics_process', [delta]);
    for (const n of scripted) bridge.callMethod(n.obj, '_process', [delta]);
  }
  bridge.shutdown();
}
