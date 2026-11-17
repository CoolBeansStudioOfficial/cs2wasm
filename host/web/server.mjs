// A static server for the browser host (SPEC section 9: the toolchain in the browser): the headers a page needs for
// shared memory (cross-origin isolation), a file "x" served from "x.br" when the browser takes Brotli and that file
// is there, and a count of what was sent.
// usage: node host/web/server.mjs <directory> [port=8085]      (or: import { serve } from this file)
//   GET /__stats    { requests: [{ url, status, bytes, encoded }], bytes }: bytes sent in bodies, since the start or
//                   the last GET /__reset
import { createServer } from 'node:http';
import { createReadStream, existsSync, statSync } from 'node:fs';
import { join, extname, normalize, sep, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const TYPES = { '.html': 'text/html; charset=utf-8', '.js': 'text/javascript; charset=utf-8', '.mjs': 'text/javascript; charset=utf-8',
  '.wasm': 'application/wasm', '.json': 'application/json', '.pck': 'application/octet-stream', '.png': 'image/png', '.svg': 'image/svg+xml',
  '.css': 'text/css; charset=utf-8', '.txt': 'text/plain; charset=utf-8', '.cs': 'text/plain; charset=utf-8', '.ico': 'image/x-icon' };

/** Serves a directory; resolves to { server, port, url, stats() }. */
export function serve(directory, port = 8085) {
  const base = resolve(directory);
  let requests = [];
  const server = createServer((req, res) => {
    const url = new URL(req.url, 'http://localhost');
    const head = {
      'Cross-Origin-Opener-Policy': 'same-origin', 'Cross-Origin-Embedder-Policy': 'require-corp', 'Cross-Origin-Resource-Policy': 'same-origin',
      'Cache-Control': 'no-store',
    };
    if (url.pathname === '/__stats' || url.pathname === '/__reset') {
      const body = JSON.stringify({ requests, bytes: requests.reduce((s, r) => s + r.bytes, 0) });
      if (url.pathname === '/__reset') requests = [];
      res.writeHead(200, { ...head, 'Content-Type': 'application/json' }); res.end(body);
      return;
    }
    let path = normalize(join(base, decodeURIComponent(url.pathname)));
    if (path !== base && !path.startsWith(base + sep)) { res.writeHead(403, head); res.end(); return; }
    if (existsSync(path) && statSync(path).isDirectory()) path = join(path, 'index.html');
    const entry = { url: url.pathname, status: 200, bytes: 0, encoded: false };
    requests.push(entry);
    if (!existsSync(path)) { entry.status = 404; res.writeHead(404, head); res.end('not found'); return; }
    head['Content-Type'] = TYPES[extname(path)] ?? 'application/octet-stream';
    let file = path;
    if (/\bbr\b/.test(req.headers['accept-encoding'] ?? '') && existsSync(path + '.br')) { file = path + '.br'; head['Content-Encoding'] = 'br'; entry.encoded = true; }
    head['Content-Length'] = statSync(file).size;
    // (the plain length, for a page that shows progress of an encoded download)
    head['X-Plain-Length'] = statSync(path).size;
    res.writeHead(200, head);
    if (req.method === 'HEAD') { res.end(); return; }
    const stream = createReadStream(file);
    stream.on('data', (c) => { entry.bytes += c.length; });
    stream.pipe(res);
  });
  return new Promise((done, fail) => {
    server.on('error', fail);
    server.listen(port, '127.0.0.1', () => {
      const p = server.address().port;
      done({ server, port: p, url: `http://127.0.0.1:${p}/`, stats: () => ({ requests, bytes: requests.reduce((s, r) => s + r.bytes, 0) }), reset: () => { requests = []; } });
    });
  });
}

if (process.argv[1] && fileURLToPath(import.meta.url) === resolve(process.argv[1])) {
  const [dir, port] = process.argv.slice(2);
  if (!dir) { console.error('usage: node host/web/server.mjs <directory> [port]'); process.exit(2); }
  const s = await serve(dir, port ? +port : 8085);
  console.log(`serving ${resolve(dir)} at ${s.url}`);
}
