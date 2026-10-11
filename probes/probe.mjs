import { spawn } from 'node:child_process';
import fs from 'node:fs';

const exe = process.argv[2];
const file = process.argv[3];
const specs = process.argv.slice(4);          // "SUBSTR:OFFSET"
const text = fs.readFileSync(file, 'utf8');
const uri = 'file:///' + file.replace(/\\/g, '/').replace(/^([a-z]):/i, (m, d) => d.toUpperCase() + ':');
const lines = text.split('\n');

const points = specs.map((s) => {
  const i = s.lastIndexOf(':');
  const sub = s.slice(0, i), off = parseInt(s.slice(i + 1), 10);
  const li = lines.findIndex((l) => l.includes(sub));
  return { tag: s, line: li, ch: lines[li].indexOf(sub) + off };
});

const extraArgs = process.env.PROBE_ARGS ? process.env.PROBE_ARGS.split('|') : [];
const child = spawn(exe, extraArgs, { stdio: ['pipe', 'pipe', 'pipe'] });
let buf = Buffer.alloc(0); const pending = new Map();
child.stdout.on('data', (d) => {
  buf = Buffer.concat([buf, d]);
  for (;;) {
    const idx = buf.indexOf('\r\n\r\n'); if (idx < 0) return;
    const len = parseInt(/Content-Length: (\d+)/i.exec(buf.slice(0, idx).toString())[1], 10);
    const body = buf.slice(idx + 4, idx + 4 + len); if (body.length < len) return;
    buf = buf.slice(idx + 4 + len);
    try { const m = JSON.parse(body.toString());
      if (m.id != null && pending.has(m.id)) { pending.get(m.id)(m); pending.delete(m.id); } } catch {}
  }
});
let id = 0;
const send = (method, params) => new Promise((res) => {
  const myId = ++id; pending.set(myId, res);
  const p = JSON.stringify({ jsonrpc: '2.0', id: myId, method, params });
  child.stdin.write('Content-Length: ' + Buffer.byteLength(p) + '\r\n\r\n' + p);
  setTimeout(() => { if (pending.has(myId)) { pending.delete(myId); res(null); } }, 15000);
});
const notify = (m, p) => { const s = JSON.stringify({ jsonrpc: '2.0', method: m, params: p });
  child.stdin.write('Content-Length: ' + Buffer.byteLength(s) + '\r\n\r\n' + s); };
child.stderr.on('data', () => {});
await send('initialize', { processId: process.pid, rootUri: null, capabilities: {}, workspaceFolders: null });
notify('initialized', {});
notify('textDocument/didOpen', { textDocument: { uri, languageId: 'shaderlab', version: 1, text } });
await new Promise(r => setTimeout(r, 2500));
const out = [];
for (const p of points) {
  const resp = await send('textDocument/completion', { textDocument: { uri }, position: { line: p.line, character: p.ch } });
  const r = resp && resp.result;
  const labels = Array.isArray(r) ? r.map(i => i.label) : (r && Array.isArray(r.items) ? r.items.map(i => i.label) : []);
  out.push({ tag: p.tag, count: labels.length, labels });
}
console.log(JSON.stringify(out));
child.kill(); process.exit(0);
