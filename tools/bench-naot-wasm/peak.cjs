// Preloaded into the Node process that runs NativeAOT's WebAssembly build (node -r peak.cjs bench-naot.js): prints the
// module's linear memory at exit, the same measure tools/bench.mjs takes of il2wasm's module (a memory never shrinks).
let memory = null;
const note = (r, imports) => { const i = r.instance ?? r; memory = i.exports?.memory ?? imports?.env?.memory ?? memory; return r; };
const inst = WebAssembly.instantiate;
WebAssembly.instantiate = (m, imports) => inst.call(WebAssembly, m, imports).then((r) => note(r, imports));
process.on('exit', () => { if (memory) console.log('peak_bytes=' + memory.buffer.byteLength); });
