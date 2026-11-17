// Runs the self-hosted compiler under Node on the given cs2wasmc arguments (as tools/selfrun.sh does, from any
// shell): node tests/Godot/proof5_hello2d/check/self.mjs [--trace-files list.txt] <arguments>
// Paths are this machine's; they are given to the compiler as it sees them (common.mjs: selfPath).
// --trace-files: every file the compiler opened or asked about, one per line (what a browser host has to serve).
import { existsSync } from 'node:fs';
import { join, resolve } from 'node:path';
import { root, selfHosted, selfPath, selfEnv, run } from './common.mjs';

const args = process.argv.slice(2);
const env = selfEnv();
if (args[0] === '--trace-files') { env.CS2WASM_FS_TRACE = args[1]; args.splice(0, 2); }
const pre = env.CS2WASM_FS_TRACE ? ['--import', new URL('./trace-fs.mjs', import.meta.url).href] : [];
// (the module's working directory is its root: a file named relative to this process's is given in full)
const full = args.map((a, i) => (existsSync(a) || args[i - 1] === '-o' ? resolve(a) : a));
process.exit(run('node', [...pre, join(root, 'host', 'run.mjs'), selfHosted(), 'main', '--', ...full.map(selfPath)], { env }));
