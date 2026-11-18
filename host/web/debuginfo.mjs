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

/** Parses cs2wasm's custom sections: frame names per function index, and the code-offset line table. */
export function debugInfo(module) {
  // function names: the name section's function names (a .NET frame name for each frame)
  const names = [];
  for (const sec of WebAssembly.Module.customSections(module, 'name')) {
    const b = new Uint8Array(sec), p = { i: 0 };
    while (p.i < b.length) {
      const id = b[p.i++], len = readU32(b, p), end = p.i + len;
      if (id === 1) { const n = readU32(b, p); for (let k = 0; k < n; k++) { const idx = readU32(b, p); names[idx] = readName(b, p); } }
      p.i = end;
    }
  }
  // frames: a bit per function index, set for .NET frames
  const frames = [];
  for (const sec of WebAssembly.Module.customSections(module, 'cs2wasm.frames')) {
    const b = unpackSection(new Uint8Array(sec)), p = { i: 0 };
    const n = readU32(b, p);
    const visible = [];
    for (let k = 0; k < n; k++) visible.push((b[p.i + (k >> 3)] >> (k & 7)) & 1);
    p.i += (n + 7) >> 3;
    const ns = readU32(b, p), strs = [];
    for (let k = 0; k < ns; k++) strs.push(readName(b, p));
    const nl = readU32(b, p);
    for (let k = 0; k < nl; k++) {
      const idx = readU32(b, p), shape = readU32(b, p);
      if (shape === 0) { names[idx] = strs[readU32(b, p)]; continue; }
      const type = strs[readU32(b, p)], method = strs[readU32(b, p)], ps = [];
      for (let q = 1; q < shape; q++) ps.push(strs[readU32(b, p)] + ' ' + strs[readU32(b, p)]);
      names[idx] = type + '.' + method + '(' + ps.join(', ') + ')';
    }
    for (let k = 0; k < n; k++) frames.push(visible[k] ? names[k] : null);
  }
  // lines: files, then (offset delta, line delta << 1 | file changes[, file])
  const files = [], lines = [];
  for (const sec of WebAssembly.Module.customSections(module, 'cs2wasm.lines')) {
    const b = new Uint8Array(sec), p = { i: 0 };
    const nf = readU32(b, p);
    for (let k = 0; k < nf; k++) files.push(readName(b, p));
    const n = readU32(b, p);
    let off = 0, file = 0, line = 0;
    for (let k = 0; k < n; k++) {
      off += readU32(b, p);
      const v = readS32(b, p);
      line += v >> 1;
      if (v & 1) file = readU32(b, p);
      lines.push([off, file, line]);
    }
  }
  const lineAt = (offset) => {
    let lo = 0, hi = lines.length - 1, best = -1;
    while (lo <= hi) { const m = (lo + hi) >> 1; if (lines[m][0] <= offset) { best = m; lo = m + 1; } else hi = m - 1; }
    return best < 0 || files[lines[best][1]] === undefined ? null : { file: files[lines[best][1]], line: lines[best][2] };
  };
  // inlined calls: the scopes (parent, name, where the call is), and the scope of each call or throw in inlined code
  const scopes = [], inlineOf = new Map();
  for (const sec of WebAssembly.Module.customSections(module, 'cs2wasm.inline')) {
    const b = unpackSection(new Uint8Array(sec)), p = { i: 0 };
    const inNames = [], inFiles = [];
    for (let k = readU32(b, p); k > 0; k--) inNames.push(readName(b, p));
    for (let k = readU32(b, p); k > 0; k--) inFiles.push(readName(b, p));
    for (let k = readU32(b, p); k > 0; k--) {
      const parent = readU32(b, p) - 1, name = inNames[readU32(b, p)], file = readU32(b, p) - 1, line = readU32(b, p);
      scopes.push({ parent, name, call: file < 0 ? null : { file: inFiles[file], line } });
    }
    let off = 0;
    for (let k = readU32(b, p); k > 0; k--) { off += readU32(b, p); inlineOf.set(off, readU32(b, p) - 1); }
  }
  // The frames a stack trace shows for one frame of the engine's (a function and a code offset in it), innermost
  // first: the methods inlined at that offset, each where the code or its inlined call is, then the function itself
  // (if it is a .NET frame) at the outermost inlined call, or at the offset when nothing is inlined there.
  const framesAt = (index, offset) => {
    const out = [];
    let loc = lineAt(offset);
    for (let s = inlineOf.get(offset) ?? -1; s >= 0; s = scopes[s].parent) { out.push({ name: scopes[s].name, loc }); loc = scopes[s].call; }
    if (frames[index]) out.push({ name: frames[index], loc });
    return out;
  };
  return { frames, lineAt, names, framesAt };
}

