import { spawn } from 'node:child_process';
import fs from 'node:fs';

const exe = process.argv[2];
const file = process.argv[3];
const text = fs.readFileSync(file, 'utf8');
const uri = 'file:///' + file.replace(/\\/g, '/').replace(/^([a-z]):/i, (m, d) => d.toUpperCase() + ':');

const points = [];
const lines = text.split('\n');
lines.forEach((l, i) => {
  const m = /float [ab] = (bia|ld\.dir)$/.exec(l.trim());
  if (m) points.push({ line: i, ch: l.length, tag: m[1] === 'bia' ? 'bia -> biasLocal' : 'ld.dir -> directionWS' });
});

const child = spawn(exe, [], { stdio: ['pipe', 'pipe', 'pipe'] });
let buf = Buffer.alloc(0);
const pending = new Map();
child.stdout.on('data', (d) => {
  buf = Buffer.concat([buf, d]);
  for (;;) {
    const idx = buf.indexOf('\r\n\r\n');
    if (idx < 0) return;
    const head = buf.slice(0, idx).toString();
    const len = parseInt(/Content-Length: (\d+)/i.exec(head)[1], 10);
    const body = buf.slice(idx + 4, idx + 4 + len);
    if (body.length < len) return;
    buf = buf.slice(idx + 4 + len);
    try { const msg = JSON.parse(body.toString());
      if (msg.id != null && pending.has(msg.id)) { pending.get(msg.id)(msg); pending.delete(msg.id); }
    } catch {}
  }
});
let id = 0;
const send = (method, params) => new Promise((res) => {
  const myId = ++id;
  pending.set(myId, res);
  const payload = JSON.stringify({ jsonrpc: '2.0', id: myId, method, params });
  child.stdin.write('Content-Length: ' + Buffer.byteLength(payload) + '\r\n\r\n' + payload);
  setTimeout(() => { if (pending.has(myId)) { pending.delete(myId); res(null); } }, 15000);
});
const notify = (method, params) => {
  const payload = JSON.stringify({ jsonrpc: '2.0', method, params });
  child.stdin.write('Content-Length: ' + Buffer.byteLength(payload) + '\r\n\r\n' + payload);
};

child.stderr.on('data', () => {});

await send('initialize', { processId: process.pid, rootUri: null, capabilities: {}, workspaceFolders: null });
notify('initialized', {});
notify('textDocument/didOpen', { textDocument: { uri, languageId: 'hlsl', version: 1, text } });
await new Promise(r => setTimeout(r, 2500));

const out = [];
for (const p of points) {
  const resp = await send('textDocument/completion', { textDocument: { uri }, position: { line: p.line, character: p.ch } });
  let labels = [];
  const r = resp && resp.result;
  if (Array.isArray(r)) labels = r.map(i => i.label);
  else if (r && Array.isArray(r.items)) labels = r.items.map(i => i.label);
  out.push({ tag: p.tag, count: labels.length, labels: labels.slice(0, 40) });
}
console.log(JSON.stringify(out));
child.kill();
process.exit(0);
