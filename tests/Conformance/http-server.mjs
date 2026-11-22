// The HTTP server of the conformance programs that use HTTP (072_http): started by the runner once, on the port it
// prints: a free one the system picks, so that several runners on one machine (the suite's two modes, two suites) each
// have their own (they all asked for 18765; the one whose server could not listen used another runner's, which was
// gone once that runner ended: connection refused). `node http-server.mjs 18765` for a port by hand. It echoes what
// a request carried.
import { createServer } from 'node:http';
const server = createServer((req, res) => {
  let body = '';
  req.on('data', (c) => { body += c; });
  req.on('end', () => {
    if (req.url === '/missing') { res.writeHead(404, { 'content-type': 'text/plain' }); res.end('nope'); return; }
    if (req.url === '/big') { res.writeHead(200, { 'content-type': 'application/octet-stream' }); res.end(Buffer.alloc(300000, 7)); return; }
    res.writeHead(200, { 'content-type': 'text/plain; charset=utf-8', 'x-test': 'yes' });
    res.end(`${req.method} ${req.url} [${body}] x-mine=${req.headers['x-mine'] ?? '-'} type=${req.headers['content-type'] ?? '-'}`);
  });
});
server.listen(+(process.argv[2] ?? 0), '127.0.0.1', () => { console.log(`ready ${server.address().port}`); });
