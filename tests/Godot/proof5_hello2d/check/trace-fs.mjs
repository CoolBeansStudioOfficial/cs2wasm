// Preloaded into a Node run of a module (node --import): records every path the module's host file system is asked
// for (open, stat, access, opendir), to CS2WASM_FS_TRACE, without changing host/fs.mjs. One line per path:
// "<kind> <path>", kind o (a file that was opened), f (a file that exists and was only asked about), d (a
// directory), - (asked for, not there). Each thread of the run appends its own lines (a path opened after it was
// asked about has both lines).
import fs from 'node:fs';
import { syncBuiltinESMExports } from 'node:module';

const seen = new Map();
const stat = fs.statSync, append = fs.appendFileSync;
const note = (p, opened) => {
  if (typeof p !== 'string' || (seen.has(p) && !(opened && seen.get(p) === 'f'))) return;
  let kind = '-';
  try { kind = stat(p).isDirectory() ? 'd' : opened ? 'o' : 'f'; } catch { /* not there */ }
  seen.set(p, kind);
  append(process.env.CS2WASM_FS_TRACE, `${kind} ${p}\n`); // (at once: a worker thread ends with the process, unasked)
};
for (const name of ['openSync', 'statSync', 'lstatSync', 'accessSync', 'opendirSync', 'existsSync']) {
  const f = fs[name];
  fs[name] = function (p, ...rest) { note(p, name === 'openSync'); return f.call(this, p, ...rest); };
}
syncBuiltinESMExports();
