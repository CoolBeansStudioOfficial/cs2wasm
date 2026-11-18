// The host's files for NativeAOT's WebAssembly build of cs2wasmc (Emscripten, in Node): the drive is mounted at /c
// (Windows) or the root at /host, the working directory is the host's, and the CS2WASM_* variables are passed on.
// Paths are given as the mounted ones: /c/Users/... for C:\Users\...
Module.preRun = Module.preRun || [];
Module.preRun.push(() => {
  const win = process.platform === 'win32';
  const cwd = process.cwd().split(String.fromCharCode(92)).join('/');
  const mount = win ? '/' + cwd[0].toLowerCase() : '/host';
  FS.mkdir(mount);
  FS.mount(NODEFS, { root: win ? cwd.slice(0, 3) : '/' }, mount);
  FS.chdir(mount + (win ? cwd.slice(2) : cwd));
  for (const k of Object.keys(process.env)) if (k.startsWith('CS2WASM_') || k === 'TMP' || k === 'HOME') ENV[k] = process.env[k];
});
