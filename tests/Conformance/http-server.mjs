// The HTTP server of the conformance programs that use HTTP (072_http): started by the runner once, on the port it
// prints (18765). It echoes what a request carried.
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
server.listen(18765, '127.0.0.1', () => { console.log(`ready ${server.address().port}`); });
