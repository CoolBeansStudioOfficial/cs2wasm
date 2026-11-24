// node tools/build-corelib.mjs [--out DIR] [--check]
// Builds tools/BuildCoreLib, then il2wasm's framework directory (docs/CORELIB.md): System.Private.CoreLib.dll + .pdb
// built from dotnet/runtime v11.0.0 with the manifest, and the stock browser-wasm IL beside it.
// --check: builds a second time into a fresh directory and requires every file to be byte-identical.
import { execFileSync } from 'node:child_process';
import { readFileSync, rmSync, mkdtempSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { tmpdir } from 'node:os';
import { fileURLToPath } from 'node:url';

const root = join(dirname(fileURLToPath(import.meta.url)), '..');
const args = process.argv.slice(2);
const outAt = args.indexOf('--out');
const out = outAt >= 0 ? args[outAt + 1] : join(root, 'artifacts', 'framework');
const run = (cmd, a) => execFileSync(cmd, a, { stdio: 'inherit', cwd: root });

run('dotnet', ['build', join(root, 'tools', 'BuildCoreLib'), '-c', 'Release', '-v', 'q', '-nologo']);
const tool = join(root, 'tools', 'BuildCoreLib', 'bin', 'Release', 'net11.0', 'BuildCoreLib.dll');
run('dotnet', [tool, 'build', '--out', out]);

if (args.includes('--check')) {
  const again = mkdtempSync(join(tmpdir(), 'il2wasm-framework-'));
  try {
    run('dotnet', [tool, 'build', '--out', again]);
    const list = d => readFileSync(join(d, 'framework.txt'), 'utf8').split('\n').filter(l => l && !l.startsWith('#'));
    const a = list(out), b = list(again);
    const diff = a.filter((l, i) => l !== b[i]);
    if (a.length !== b.length || diff.length) { console.error('not reproducible:\n' + diff.join('\n')); process.exit(1); }
    console.log(`reproducible: ${a.length} files byte-identical`);
  } finally { rmSync(again, { recursive: true, force: true }); }
}
