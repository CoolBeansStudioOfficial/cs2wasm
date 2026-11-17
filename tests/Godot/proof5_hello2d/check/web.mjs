// Proof 5, what the page serves (node tools/proof5.mjs web [--retrace] [--no-export] [--no-toolchain] [--no-compress]), in
// tests/out/proof5/site:
//   godot/        the web variant of the project (no C# scripts: each scripted node has cs2wasm_node.gd and its C#
//                 script's path in metadata; the autoload Cs2Wasm is the bridge's engine end), exported for the web
//                 with the stock templates (the build without threads): the stock editor writes the pack, and the
//                 template's files are put beside it here (see the note at that step)
//   toolchain/    the self-hosted compiler (cs2wasm.wasm), the files it reads to build a Godot script (library.pack:
//                 the .NET library's sources and GodotSharp's from the cache, the reference assemblies, GodotSharp.dll),
//                 and the engine's method signatures for the bridge (signatures.json); each also Brotli-compressed
//   game/         the game's C# sources, and the sources Godot's source generators write for them (see below)
//   index.html, main.mjs, host/   the page and the browser host
// The self-hosted compiler cannot load Godot.SourceGenerators.dll (it is a .NET assembly, and the toolchain module has
// no assembly loader: SPEC 7.1 has such code compiled into the toolchain, which this step does not rebuild). So the
// generators' output for the game's scripts is written here by the .NET-hosted compiler (CS2WASM_GENERATED) and served
// as sources: what the browser compiles is the game's two scripts plus those files, with no generator run.
import { cpSync, existsSync, mkdirSync, readFileSync, readdirSync, rmSync, statSync, writeFileSync } from 'node:fs';
import { join, relative, resolve, sep } from 'node:path';
import { brotliCompressSync, constants, inflateRawSync } from 'node:zlib';
import { root, project, outDir, version, api, godotSharp, cache, editor, run, selfHosted, referenceDir, selfEnv, selfPath } from './common.mjs';
import { buildArgs, compilerExe, scripts } from './stub.mjs';
import { parseScene } from '../../../../host/godot-replay.mjs';
import { makePack } from '../../../../host/web/vfs.mjs';
import { raiseMemory } from './raise-memory.mjs';
import { signatures } from '../../../../host/godot-bridge.mjs';

export const site = join(outDir, 'site');
const walk = (d) => readdirSync(d, { withFileTypes: true }).flatMap((e) => (e.isDirectory() ? walk(join(d, e.name)) : [join(d, e.name)]));
const kb = (n) => `${n.toLocaleString('en-US')} bytes`;

/** The stock export templates' directory (CS2WASM_GODOT_TEMPLATES, or the editor's own place for them). */
function templates() {
  if (process.env.CS2WASM_GODOT_TEMPLATES) return process.env.CS2WASM_GODOT_TEMPLATES;
  const data = process.platform === 'win32' ? join(process.env.APPDATA ?? '', 'Godot') : process.platform === 'darwin' ? join(process.env.HOME ?? '', 'Library', 'Application Support', 'Godot') : join(process.env.HOME ?? '', '.local', 'share', 'godot');
  return join(data, 'export_templates', version + '.stable.mono');
}

/** A zip archive's files: [[name, bytes]]. */
function unzip(zip) {
  const out = [];
  let end = zip.length - 22;
  while (end >= 0 && zip.readUInt32LE(end) !== 0x06054b50) end--;
  if (end < 0) throw new Error('not a zip archive');
  let at = zip.readUInt32LE(end + 16);
  for (let n = zip.readUInt16LE(end + 10); n > 0; n--) {
    const method = zip.readUInt16LE(at + 10), size = zip.readUInt32LE(at + 20), nameLen = zip.readUInt16LE(at + 28), extra = zip.readUInt16LE(at + 30), comment = zip.readUInt16LE(at + 32), local = zip.readUInt32LE(at + 42);
    const name = zip.subarray(at + 46, at + 46 + nameLen).toString('utf8');
    const data = local + 30 + zip.readUInt16LE(local + 26) + zip.readUInt16LE(local + 28);
    if (!name.endsWith('/')) out.push([name, method === 0 ? zip.subarray(data, data + size) : inflateRawSync(zip.subarray(data, data + size))]);
    at += 46 + nameLen + extra + comment;
  }
  return out;
}

/** The project without C#: where a node had a C# script it has the proxy script and the C# script's path. */
export function webVariant(dir) {
  rmSync(dir, { recursive: true, force: true });
  mkdirSync(dir, { recursive: true });
  let settings = readFileSync(join(project, 'project.godot'), 'utf8').replaceAll('\r\n', '\n');
  settings = settings.replace('PackedStringArray("4.7", "C#")', 'PackedStringArray("4.7")')
    .replace(/\[dotnet\]\n\n[^\[]*/, '')
    .replace('[autoload]\n\n', '[autoload]\n\nCs2Wasm="*res://cs2wasm_bridge.gd"\n');
  writeFileSync(join(dir, 'project.godot'), settings);
  let scene = readFileSync(join(project, 'main.tscn'), 'utf8').replaceAll('\r\n', '\n');
  const parsed = parseScene(scene);
  let proxied = 0;
  const csharp = new Map([...parsed.ext].filter(([, res]) => res.type === 'Script' && res.path.endsWith('.cs')));
  for (const [id, res] of csharp)
    scene = scene.replace(`[ext_resource type="Script" path="${res.path}" id="${id}"]`, `[ext_resource type="Script" path="res://cs2wasm_node.gd" id="${id}"]`);
  // a scripted node: the script's line, then (as the editor writes a scene) the values of the script's properties.
  // Those go into metadata with the C# script's path: the export keeps of a node only what its class or the script
  // standing there declares.
  const out = [];
  let script = null, values = [];
  const close = () => {
    if (script) {
      out.push(`metadata/cs2wasm_script = "${script}"`);
      if (values.length) out.push(`metadata/cs2wasm_properties = { ${values.map(([k, v]) => `"${k}": ${v}`).join(', ')} }`);
      proxied++;
    }
    script = null; values = [];
  };
  for (const line of scene.split('\n')) {
    let m;
    if (script && (m = /^([\w/]+) = (.*)$/.exec(line)) && !m[1].startsWith('metadata/')) { values.push([m[1], m[2]]); continue; }
    if (script && (line === '' || line.startsWith('['))) close();
    out.push(line);
    if ((m = /^script = ExtResource\("([^"]+)"\)$/.exec(line)) && csharp.has(m[1])) script = csharp.get(m[1]).path;
  }
  close();
  scene = out.join('\n');
  writeFileSync(join(dir, 'main.tscn'), scene);
  for (const f of ['driver.gd', 'icon.svg']) cpSync(join(project, f), join(dir, f));
  for (const f of ['cs2wasm_bridge.gd', 'cs2wasm_node.gd']) cpSync(join(root, 'host', 'web', 'godot', f), join(dir, f));
  writeFileSync(join(dir, 'export_presets.cfg'), `[preset.0]

name="Web"
platform="Web"
runnable=true
dedicated_server=false
custom_features=""
export_filter="all_resources"
include_filter=""
exclude_filter=""
export_path=""
patches=PackedStringArray()
encryption_include_filters=""
encryption_exclude_filters=""
seed=0
encrypt_pck=false
encrypt_directory=false
script_export_mode=2

[preset.0.options]

custom_template/debug=""
custom_template/release=""
variant/extensions_support=false
variant/thread_support=false
vram_texture_compression/for_desktop=true
vram_texture_compression/for_mobile=false
html/export_icon=true
html/custom_html_shell=""
html/head_include=""
html/canvas_resize_policy=0
html/focus_canvas_on_start=true
html/experimental_virtual_keyboard=false
progressive_web_app/enabled=false
`);
  return proxied;
}

/** The files the self-hosted compiler asks its host for when a program references GodotSharp (a run under Node,
 * traced). The program is the smallest one (repro/Hello.cs): with the reference the compiler loads GodotSharp's
 * sources and asks about every library file it may read. (The game itself would be the program to trace, but the
 * self-hosted compiler does not get through it yet: see the README.) Also leaves hello-self.wasm, the same program
 * built without the reference, to compare a build in the browser with. */
function trace(file, compiler) {
  rmSync(file, { force: true });
  const hello = join(project, 'repro', 'Hello.cs');
  const self = join(project, 'check', 'self.mjs');
  // (the module the site serves, told it has one core: with more, a build of this size did not end in several
  // minutes here, its threads idle; see the README)
  const env = { ...process.env, CS2WASM_SELF: compiler, CS2WASM_PROCESSORS: '1' };
  let t0 = performance.now();
  // (the source's path is in the module's line table: mapped to the path the page gives the same file)
  if (run('node', [self, hello, '--pathmap', join(project, 'repro') + '=/game', '-o', join(outDir, 'hello-self.wasm')], { env }) !== 0) throw new Error('the self-hosted compiler failed under Node');
  console.log(`web: the self-hosted compiler under Node (one core) built repro/Hello.cs (${kb(statSync(join(outDir, 'hello-self.wasm')).size)}) in ${((performance.now() - t0) / 1000).toFixed(1)} s`);
  t0 = performance.now();
  const status = run('node', [self, '--trace-files', file, hello, '-r', godotSharp, '-o', join(outDir, 'hello-ref.wasm')], { env });
  if (status !== 0 || !existsSync(file)) throw new Error('the self-hosted compiler failed under Node (exit ' + status + ')');
  console.log(`web: and the same with a reference to GodotSharp in ${((performance.now() - t0) / 1000).toFixed(1)} s`);
}

export async function web(args) {
  const flags = new Set(args);
  mkdirSync(site, { recursive: true });

  // 1. the generators' sources, by the .NET-hosted compiler (and its module, to compare with)
  const generated = join(outDir, 'generated');
  rmSync(generated, { recursive: true, force: true });
  if (run(compilerExe(), [...buildArgs(), '-o', join(outDir, 'game-net.wasm')], { env: { ...process.env, CS2WASM_GENERATED: generated } }) !== 0) { console.error('web: cs2wasmc failed'); return 1; }
  const gen = walk(generated).sort();
  rmSync(join(site, 'game'), { recursive: true, force: true });
  mkdirSync(join(site, 'game', 'generated'), { recursive: true });
  const sources = [];
  for (const f of scripts()) { const name = relative(project, f).replaceAll(sep, '/'); cpSync(f, join(site, 'game', name)); sources.push({ path: '/game/' + name, url: 'game/' + name }); }
  for (const f of gen) { const name = 'generated/' + relative(generated, f).replaceAll(sep, '/'); mkdirSync(join(site, 'game', name, '..'), { recursive: true }); cpSync(f, join(site, 'game', name)); sources.push({ path: '/game/' + name, url: 'game/' + name }); }
  writeFileSync(join(site, 'game', 'sources.json'), JSON.stringify({ sources, args: ['-r', '/lib/GodotSharp.dll'] }, null, 1));
  console.log(`web: game/: ${scripts().length} scripts and ${gen.length} files Godot's source generators wrote for them`);

  const toolchain = join(site, 'toolchain');
  mkdirSync(toolchain, { recursive: true });
  // the bridge's table of the engine's methods (what the page needs of extension_api.json)
  const sigs = Object.fromEntries(signatures(JSON.parse(readFileSync(api, 'utf8'))));
  writeFileSync(join(toolchain, 'signatures.json'), JSON.stringify(sigs));

  // 2. the engine: the project's web variant, exported by the stock editor
  if (!flags.has('--no-export')) {
    const variant = join(outDir, 'webproject');
    const proxied = webVariant(variant);
    const godot = join(site, 'godot');
    rmSync(godot, { recursive: true, force: true });
    mkdirSync(godot, { recursive: true });
    // The stock editor with .NET writes the project's pack for the Web preset but refuses the last step of a web export
    // ("not supported in Godot 4 when using C#/.NET... use a non-C# editor build"), whatever the project holds. That
    // step is: the stock web template's files under the export's name beside the pack, and the page's configuration.
    // So it is done here from the stock template archive (the build without threads).
    run(editor(), ['--headless', '--path', variant, '--import']);
    const status = run(editor(), ['--headless', '--path', variant, '--export-pack', 'Web', join(godot, 'index.pck')]);
    if (!existsSync(join(godot, 'index.pck'))) { console.error(`web: the pack's export failed (the editor exited ${status})`); return 1; }
    // CS2WASM_GODOT_WEB_TEMPLATE: the web template archive to use, in place of the stock one where the editor keeps it
    const archive = process.env.CS2WASM_GODOT_WEB_TEMPLATE ?? join(templates(), 'web_nothreads_release.zip');
    if (!existsSync(archive)) { console.error('web: no web template at ' + archive); return 1; }
    const template = unzip(readFileSync(archive));
    if (!template.some(([name]) => name === 'godot.wasm') || !template.some(([name]) => name === 'godot.js')) {
      console.error(`web: ${archive} is not a web export template (it has no godot.wasm and godot.js; its first file is ${template[0]?.[0]}). Set CS2WASM_GODOT_WEB_TEMPLATE to a web_nothreads_release.zip of Godot ${version}.`);
      return 1;
    }
    for (const [name, bytes] of template) {
      if (name === 'godot.html' || name.includes('service.worker') || name.includes('offline')) continue; // (the page is this proof's own)
      writeFileSync(join(godot, name.replace(/^godot\./, 'index.')), bytes);
    }
    writeFileSync(join(godot, 'template.txt'), archive + '\n');
    // (what the exporter writes into its page as GODOT_CONFIG)
    writeFileSync(join(godot, 'config.json'), JSON.stringify({
      args: [], canvasResizePolicy: 0, emscriptenPoolSize: 0, ensureCrossOriginIsolationHeaders: false, executable: 'index', experimentalVK: false,
      fileSizes: { 'index.pck': statSync(join(godot, 'index.pck')).size, 'index.wasm': statSync(join(godot, 'index.wasm')).size }, focusCanvas: true, gdextensionLibs: [],
    }));
    console.log(`web: godot/: the web variant (${proxied} scripted nodes), the pack by the stock editor, the engine from ${archive}: index.wasm ${kb(statSync(join(godot, 'index.wasm')).size)}, index.pck ${kb(statSync(join(godot, 'index.pck')).size)}`);
  }

  // 3. the toolchain: the compiler's module, and a pack of what it reads
  if (!flags.has('--no-toolchain')) {
  const traceFile = join(outDir, 'trace.txt');
  // The compiler's module, with its memory's greatest size raised (check/raise-memory.mjs says why): 4 GiB.
  const raised = raiseMemory(readFileSync(selfHosted()), 65536);
  writeFileSync(join(toolchain, 'cs2wasm.wasm'), raised.bytes);
  console.log(`web: toolchain/cs2wasm.wasm: ${selfHosted()} with the greatest memory ${raised.old} -> 65536 pages`);
  if (flags.has('--retrace') || !existsSync(traceFile) || !existsSync(join(outDir, 'hello-self.wasm'))) trace(traceFile, join(toolchain, 'cs2wasm.wasm'));
  const refs = resolve(referenceDir()), cacheDir = resolve(cache);
  const packed = new Map(), wasOpened = new Set();
  for (const line of readFileSync(traceFile, 'utf8').split('\n')) {
    if (!line.startsWith('f ') && !line.startsWith('o ')) continue;
    const p = resolve(line.slice(2));
    const inside = (d) => p.toLowerCase().startsWith(d.toLowerCase() + sep);
    const virtual = inside(cacheDir) ? '/cache/' + relative(cacheDir, p).replaceAll(sep, '/') : inside(refs) ? '/ref/' + relative(refs, p).replaceAll(sep, '/')
      : p.toLowerCase() === resolve(godotSharp).toLowerCase() ? '/lib/GodotSharp.dll' : null;
    if (!virtual) continue;
    packed.set(virtual, p);
    if (line.startsWith('o ')) wasOpened.add(virtual);
  }
  // (the reference directory is listed, and every assembly in it opened: all of them)
  for (const f of readdirSync(refs)) if (f.endsWith('.dll') && !packed.has('/ref/' + f)) packed.set('/ref/' + f, join(refs, f));
  const pack = makePack([...packed].sort((a, b) => (a[0] < b[0] ? -1 : 1)).map(([path, p]) => ({ path, bytes: readFileSync(p) })));
  writeFileSync(join(toolchain, 'library.pack'), pack);
  const by = (prefix) => [...packed].filter(([v]) => v.startsWith(prefix)).reduce((s, [, p]) => s + statSync(p).size, 0);
  const opened = [...packed].filter(([v]) => wasOpened.has(v)).reduce((s, [, p]) => s + statSync(p).size, 0);
  console.log(`web: toolchain/library.pack: ${packed.size} files, ${kb(pack.length)} (the cache's sources ${kb(by('/cache/'))}, reference assemblies ${kb(by('/ref/'))}, GodotSharp.dll ${kb(by('/lib/'))}; ${wasOpened.size} of the files, ${kb(opened)}, were opened by the traced build, the others only asked about)`);
  // a console program to build in the browser too (the smallest: ?game=hello)
  mkdirSync(join(site, 'hello'), { recursive: true });
  cpSync(join(project, 'repro', 'Hello.cs'), join(site, 'hello', 'Hello.cs'));
  writeFileSync(join(site, 'hello', 'sources.json'), JSON.stringify({ sources: [{ path: '/game/Hello.cs', url: 'hello/Hello.cs' }], args: [], run: 'main' }));
  if (!flags.has('--no-compress')) {
    for (const f of ['cs2wasm.wasm', 'library.pack']) {
      const t0 = performance.now();
      const raw = readFileSync(join(toolchain, f));
      const br = brotliCompressSync(raw, { params: { [constants.BROTLI_PARAM_QUALITY]: 9, [constants.BROTLI_PARAM_LGWIN]: 24, [constants.BROTLI_PARAM_SIZE_HINT]: raw.length } });
      writeFileSync(join(toolchain, f + '.br'), br);
      console.log(`web: toolchain/${f}: ${kb(raw.length)}, Brotli (quality 9) ${kb(br.length)} in ${((performance.now() - t0) / 1000).toFixed(1)} s`);
    }
  }
  }

  // 4. the page and the browser host
  for (const f of ['index.html', 'main.mjs']) cpSync(join(project, 'web', f), join(site, f));
  rmSync(join(site, 'host'), { recursive: true, force: true });
  mkdirSync(join(site, 'host', 'web'), { recursive: true });
  cpSync(join(root, 'host', 'godot-bridge.mjs'), join(site, 'host', 'godot-bridge.mjs'));
  for (const f of readdirSync(join(root, 'host', 'web'))) if (f.endsWith('.mjs') && f !== 'server.mjs') cpSync(join(root, 'host', 'web', f), join(site, 'host', 'web', f));
  console.log(`web: the site is at ${site}; serve it with: node host/web/server.mjs ${relative(root, site)}`);
  return 0;
}
