// Writes a copy of a module whose imported memory may grow further: node raise-memory.mjs in.wasm out.wasm [pages=65536]
// The self-hosted compiler at tests/out/self2 was built with the default greatest memory (16,384 pages, 1 GiB), and
// compiling a program that uses GodotSharp needs more (it ends with OutOfMemoryException at the limit). The limit is
// a number in the module's import of env.memory; this rewrites that number (il2wasm --max-pages sets it at build
// time: rebuilding the toolchain with it is the proper way, and this copy stands in until then).
import { readFileSync, writeFileSync } from 'node:fs';

export function raiseMemory(bytes, pages = 65536) {
  const leb = (p) => { let r = 0, s = 0, x; do { x = bytes[p.i++]; r += (x & 0x7f) * 2 ** s; s += 7; } while (x & 0x80); return r; };
  const enc = (v) => { const o = []; do { let b = v & 0x7f; v = Math.floor(v / 128); if (v) b |= 0x80; o.push(b); } while (v); return o; };
  const p = { i: 8 };
  while (p.i < bytes.length) {
    const id = bytes[p.i++], sizeAt = p.i, size = leb(p), start = p.i, end = p.i + size;
    if (id === 2) {
      for (let n = leb(p); n > 0; n--) {
        const name = () => { const l = leb(p); const s = Buffer.from(bytes.subarray(p.i, p.i + l)).toString('utf8'); p.i += l; return s; };
        const mod = name(), field = name(), kind = bytes[p.i++];
        if (kind === 0) leb(p);
        else if (kind === 1) { p.i++; const f = leb(p); leb(p); if (f & 1) leb(p); }
        else if (kind === 2) {
          const f = leb(p); leb(p);
          const at = p.i, old = f & 1 ? leb(p) : null;
          if (mod === 'env' && field === 'memory') {
            if (old === null) throw new Error('the memory has no greatest size to raise');
            const now = enc(pages), delta = now.length - (p.i - at);
            const body = Buffer.concat([bytes.subarray(start, at), Buffer.from(now), bytes.subarray(p.i, end)]);
            return { bytes: Buffer.concat([bytes.subarray(0, sizeAt), Buffer.from(enc(size + delta)), body, bytes.subarray(end)]), old };
          }
        }
        else if (kind === 3) p.i += 2;
        else if (kind === 4) { p.i++; leb(p); }
      }
      break;
    }
    p.i = end;
  }
  throw new Error('the module imports no env.memory');
}

if (process.argv[1]?.endsWith('raise-memory.mjs')) {
  const [input, output, pages] = process.argv.slice(2);
  const r = raiseMemory(readFileSync(input), pages ? +pages : 65536);
  writeFileSync(output, r.bytes);
  console.log(`${output}: the memory's greatest size ${r.old} -> ${pages ?? 65536} pages`);
}
