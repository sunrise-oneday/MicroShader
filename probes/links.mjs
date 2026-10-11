import { spawn } from 'node:child_process';
import fs from 'node:fs';

const exe = process.argv[2];
const file = process.argv[3];
const exeArgs = process.argv.slice(4);
const text = fs.readFileSync(file, 'utf8');
const uri = 'file:///' + file.replace(/\\/g, '/').replace(/^([a-z]):/i, (m, d) => d.toUpperCase() + ':');

const child = spawn(exe, exeArgs, { stdio: ['pipe', 'pipe', 'pipe'] });
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
  setTimeout(() => { if (pending.has(myId)) { pending.delete(myId); res({ error: 'timeout ' + method }); } }, 20000);
});
const notify = (m, p) => { const s = JSON.stringify({ jsonrpc: '2.0', method: m, params: p });
  child.stdin.write('Content-Length: ' + Buffer.byteLength(s) + '\r\n\r\n' + s); };
child.stderr.on('data', () => {});

const init = await send('initialize', { processId: process.pid, rootUri: 'file:///' + file.replace(/\\/g, '/').replace(/[^/]+$/, ''), capabilities: {}, workspaceFolders: [] });
console.log('server capabilities.linkProvider:', JSON.stringify(init.result?.capabilities?.documentLinkProvider));
notify('initialized', {});
notify('textDocument/didOpen', { textDocument: { uri, languageId: 'hlsl', version: 1, text } });
await new Promise(r => setTimeout(r, 3000));

const links = await send('textDocument/documentLink', { textDocument: { uri } });
const arr = links.result ?? [];
const srcLines = text.split('\n');
console.log('LINKS:', arr.length);
for (const l of arr.slice(0, 12)) {
  const ln = l.range.start.line;
  const raw = srcLines[ln].slice(l.range.start.character, l.range.end.character);
  let target = l.target;
  if (!target) {
    const resolved = await send('documentLink/resolve', l);
    target = resolved.result?.target ?? '(unresolved)';
  }
  console.log('  line ' + (ln + 1) + ' [' + raw + '] -> ' + String(target).replace(/^file:\/\//, ''));
}
child.kill(); process.exit(0);
