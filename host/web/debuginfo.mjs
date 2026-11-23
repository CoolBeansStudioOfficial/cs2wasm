// cs2wasm's debug sections read in a browser: host/engine.mjs's debugInfo and its helpers, as they are there (that
// file imports Node's modules, so a page cannot load it; this copy is kept in step by tests/Godot/proof5_hello2d/
// check/browser.mjs, which fails when the two differ).
function readU32(bytes, pos) {
  let r = 0, shift = 0, b;
  do { b = bytes[pos.i++]; r |= (b & 0x7f) << shift; shift += 7; } while (b & 0x80);
  return r >>> 0;
}
function readS32(bytes, pos) {
  let r = 0, shift = 0, b;
  do { b = bytes[pos.i++]; r |= (b & 0x7f) << shift; shift += 7; } while (b & 0x80);
  if (shift < 32 && (b & 0x40)) r |= -1 << shift;
  return r | 0;
}
// a packed custom section: format byte 0 (plain) or 1 (LZ: plain length, then runs of
// (literal count, literals, match length - 4, distance), every number LEB128)
function unpackSection(b) {
  if (b[0] === 0) return b.subarray(1);
  const p = { i: 1 }, n = readU32(b, p), out = new Uint8Array(n);
  let o = 0;
  for (;;) {
    const l = readU32(b, p);
    out.set(b.subarray(p.i, p.i + l), o); p.i += l; o += l;
    if (o >= n) break;
    let m = readU32(b, p) + 4;
    const d = readU32(b, p);
    while (m--) { out[o] = out[o - d]; o++; }
  }
  return out;
}
function readName(bytes, pos) {
  const n = readU32(bytes, pos);
  const s = new TextDecoder().decode(bytes.subarray(pos.i, pos.i + n));
  pos.i += n;
  return s;
}

/** Reads cs2wasm's custom sections: frame names per function index, and the code-offset line table. What a stack
 * needs is found when it is asked for: a name is decoded when a frame with it is shown (a large module has a hundred
 * thousand, a stack some tens), and the tables are numbers in typed arrays. A program's first throw pays for this. */
export function debugInfo(module) {
  const decoder = new TextDecoder();
  const skipName = (b, p) => { const n = readU32(b, p); p.i += n; };
  // function names: the name section's (where each is, by function index), then the frames section's own
  let nameBytes = null;
  const nameStart = [], nameLen = [];
  for (const sec of WebAssembly.Module.customSections(module, 'name')) {
    const b = new Uint8Array(sec), p = { i: 0 };
    nameBytes = b;
    while (p.i < b.length) {
      const id = b[p.i++], len = readU32(b, p), end = p.i + len;
      if (id === 1) { const n = readU32(b, p); for (let k = 0; k < n; k++) { const idx = readU32(b, p), l = readU32(b, p); nameStart[idx] = p.i; nameLen[idx] = l; p.i += l; } }
      p.i = end;
    }
  }
  // frames: a bit per function index, set for .NET frames; strings; a name made of strings for some functions
  let fb = null, visibleAt = 0, count = 0;
  const strStart = [], strs = [], shapeAt = new Map(), lenders = new Set();
  for (const sec of WebAssembly.Module.customSections(module, 'cs2wasm.frames')) {
    const b = unpackSection(new Uint8Array(sec)), p = { i: 0 };
    fb = b;
    count = readU32(b, p);
    visibleAt = p.i;
    p.i += (count + 7) >> 3;
    const ns = readU32(b, p);
    for (let k = 0; k < ns; k++) { strStart.push(p.i); skipName(b, p); }
    const nl = readU32(b, p);
    for (let k = 0; k < nl; k++) {
      const idx = readU32(b, p);
      shapeAt.set(idx, p.i);
      const shape = readU32(b, p);
      for (let q = shape === 0 ? 1 : 2 * shape; q > 0; q--) readU32(b, p);
    }
    // (then the functions that lend their lines to the frame that calls them: a body shared by methods whose frame
    // names differ, each method's frame a stub that calls it; absent in older modules)
    if (p.i < b.length) { let at = 0; for (let k = readU32(b, p); k > 0; k--) lenders.add(at += readU32(b, p)); }
  }
  const str = (k) => strs[k] ??= readName(fb, { i: strStart[k] });
  const made = new Map();
  /** A function's frame name (undefined: it has none). */
  const nameOf = (idx) => {
    if (made.has(idx)) return made.get(idx);
    let name;
    const at = shapeAt.get(idx);
    if (at !== undefined) {
      const p = { i: at }, shape = readU32(fb, p);
      if (shape === 0) name = str(readU32(fb, p));
      else {
        const type = str(readU32(fb, p)), method = str(readU32(fb, p)), ps = [];
        for (let q = 1; q < shape; q++) ps.push(str(readU32(fb, p)) + ' ' + str(readU32(fb, p)));
        name = type + '.' + method + '(' + ps.join(', ') + ')';
      }
    } else if (nameStart[idx] !== undefined) name = decoder.decode(nameBytes.subarray(nameStart[idx], nameStart[idx] + nameLen[idx]));
    made.set(idx, name);
    return name;
  };
  const isVisible = (idx) => idx < count && ((fb[visibleAt + (idx >> 3)] >> (idx & 7)) & 1) === 1;
  /** A function's name when it is a .NET frame, else null. */
  const frameOf = (idx) => (isVisible(idx) ? nameOf(idx) : null);
  // lines: files, then (offset delta, line delta << 1 | file changes[, file]); read when a place is first asked for
  let lineTable = null;
  const readLines = () => {
    const files = [];
    let offs = new Int32Array(0), fileOf = new Int32Array(0), lineOf = new Int32Array(0), n = 0;
    for (const sec of WebAssembly.Module.customSections(module, 'cs2wasm.lines')) {
      const b = new Uint8Array(sec), p = { i: 0 };
      const nf = readU32(b, p);
      for (let k = 0; k < nf; k++) files.push(readName(b, p));
      n = readU32(b, p);
      offs = new Int32Array(n); fileOf = new Int32Array(n); lineOf = new Int32Array(n);
      let off = 0, file = 0, line = 0;
      for (let k = 0; k < n; k++) {
        off += readU32(b, p);
        const v = readS32(b, p);
        line += v >> 1;
        if (v & 1) file = readU32(b, p);
        offs[k] = off; fileOf[k] = file; lineOf[k] = line;
      }
    }
    return { files, offs, fileOf, lineOf, n };
  };
  const lineAt = (offset) => {
    const t = (lineTable ??= readLines());
    let lo = 0, hi = t.n - 1, best = -1;
    while (lo <= hi) { const m = (lo + hi) >> 1; if (t.offs[m] <= offset) { best = m; lo = m + 1; } else hi = m - 1; }
    return best < 0 || t.files[t.fileOf[best]] === undefined ? null : { file: t.files[t.fileOf[best]], line: t.lineOf[best] };
  };
  // inlined calls: the scopes (parent, name, where the call is), and the scope of each call or throw in inlined code
  // (the offsets ascending: found by halving)
  let ib = null, scopeParent = new Int32Array(0), scopeName = new Int32Array(0), scopeFile = new Int32Array(0), scopeLine = new Int32Array(0);
  let inlineOff = new Int32Array(0), inlineScope = new Int32Array(0);
  const inNameStart = [], inNames = [], inFileStart = [], inFiles = [];
  for (const sec of WebAssembly.Module.customSections(module, 'cs2wasm.inline')) {
    const b = unpackSection(new Uint8Array(sec)), p = { i: 0 };
    ib = b;
    for (let k = readU32(b, p); k > 0; k--) { inNameStart.push(p.i); skipName(b, p); }
    for (let k = readU32(b, p); k > 0; k--) { inFileStart.push(p.i); skipName(b, p); }
    const nsc = readU32(b, p);
    scopeParent = new Int32Array(nsc); scopeName = new Int32Array(nsc); scopeFile = new Int32Array(nsc); scopeLine = new Int32Array(nsc);
    for (let k = 0; k < nsc; k++) { scopeParent[k] = readU32(b, p) - 1; scopeName[k] = readU32(b, p); scopeFile[k] = readU32(b, p) - 1; scopeLine[k] = readU32(b, p); }
    const ni = readU32(b, p);
    inlineOff = new Int32Array(ni); inlineScope = new Int32Array(ni);
    let off = 0;
    for (let k = 0; k < ni; k++) { off += readU32(b, p); inlineOff[k] = off; inlineScope[k] = readU32(b, p) - 1; }
  }
  const inlineAt = (offset) => {
    let lo = 0, hi = inlineOff.length - 1;
    while (lo <= hi) { const m = (lo + hi) >> 1; if (inlineOff[m] === offset) return inlineScope[m]; if (inlineOff[m] < offset) lo = m + 1; else hi = m - 1; }
    return -1;
  };
  const scopeNameOf = (s) => inNames[scopeName[s]] ??= readName(ib, { i: inNameStart[scopeName[s]] });
  const scopeCall = (s) => (scopeFile[s] < 0 ? null : { file: inFiles[scopeFile[s]] ??= readName(ib, { i: inFileStart[scopeFile[s]] }), line: scopeLine[s] });
  // The frames a stack trace shows for one frame of the engine's (a function and a code offset in it), innermost
  // first: the methods inlined at that offset, each where the code or its inlined call is, then the function itself
  // (if it is a .NET frame) at the outermost inlined call, or at the offset when nothing is inlined there. `walk`
  // carries a lender's place to the frame that called it (one object per stack walked, innermost frame first).
  // `places` false: the names alone (a count of frames needs no line table).
  const framesAt = (index, offset, walk, places = true) => {
    const out = [];
    let loc = !places ? null : walk?.lent !== undefined ? walk.lent : lineAt(offset);
    if (walk) walk.lent = undefined;
    for (let s = inlineAt(offset); s >= 0; s = scopeParent[s]) { out.push({ name: scopeNameOf(s), loc }); loc = places ? scopeCall(s) : null; }
    if (lenders.has(index) && walk) walk.lent = loc;
    else if (isVisible(index)) { const name = nameOf(index); if (name) out.push({ name, loc }); }
    return out;
  };
  // (whether a function's name is one, and not empty, without making it)
  const hasName = (idx) => {
    const at = shapeAt.get(idx);
    if (at === undefined) return nameStart[idx] !== undefined && nameLen[idx] > 0;
    const p = { i: at };
    return readU32(fb, p) !== 0 || readU32(fb, { i: strStart[readU32(fb, p)] }) > 0;
  };
  /** How many frames framesAt gives (no name is decoded, no line looked up). */
  const countAt = (index, offset) => {
    let n = 0;
    for (let s = inlineAt(offset); s >= 0; s = scopeParent[s]) n++;
    if (!lenders.has(index) && isVisible(index) && hasName(index)) n++;
    return n;
  };
  return {
    get frames() { const all = []; for (let k = 0; k < count; k++) all.push(frameOf(k)); return all; },
    get names() { const all = []; for (let k = 0; k < Math.max(count, nameStart.length); k++) { const v = nameOf(k); if (v !== undefined) all[k] = v; } return all; },
    lineAt, framesAt, countAt, frameOf, nameOf,
  };
}

