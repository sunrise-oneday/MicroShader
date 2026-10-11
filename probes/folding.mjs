import { spawn } from 'node:child_process';
import * as fs from 'node:fs';
const SEP = String.fromCharCode(13, 10), NL = String.fromCharCode(10);
const EXE = process.argv[2], FILE = process.argv[3], PROJ = process.argv[4];
console.log('PROBE START exe=' + EXE);
const hardStop = setTimeout(() => { console.error('PROBE_TIMEOUT'); process.exit(2); }, 25000);
const uri = (p) => 'file:///' + p.replace(/^([a-z]):/i, (m, d) => d.toUpperCase() + ':').replace(/ /g, '%20');
const p = spawn(EXE, ['--project', PROJ], { stdio: ['pipe', 'pipe', 'pipe'] });
p.on('error', (e) => { console.error('SPAWN ERROR ' + e.message); process.exit(3); });
let buf = Buffer.alloc(0); const pending = new Map(); let initResp = null;
p.stdout.on('data', (d) => { buf = Buffer.concat([buf, d]);
  for (;;) { const h = buf.indexOf(Buffer.from(SEP + SEP)); if (h < 0) break;
    const m = /Content-Length: (\d+)/.exec(buf.slice(0, h).toString()); if (!m) { buf = buf.slice(h + 4); continue; }
    const len = parseInt(m[1], 10); if (buf.length < h + 4 + len) break;
    let f = null; try { f = JSON.parse(buf.slice(h + 4, h + 4 + len).toString('utf8')); } catch {}
    buf = buf.slice(h + 4 + len); if (!f) continue;
    if (f.id !== undefined && pending.has(f.id)) { pending.get(f.id)(f); pending.delete(f.id); }
    else if (f.id !== undefined && !initResp) initResp = f; } });
let err = ''; p.stderr.on('data', (d) => { err += d.toString().slice(0, 400); });
const send = (o) => { const b = JSON.stringify(o); p.stdin.write('Content-Length: ' + Buffer.byteLength(b) + SEP + SEP + b); };
const wait = (ms) => new Promise((r) => setTimeout(r, ms));
let rid = 0;
const ask = async (m, q) => { const id = ++rid; const r = await new Promise((res) => { pending.set(id, res); send({ jsonrpc: '2.0', id, method: m, params: q }); }); return r; };
send({ jsonrpc: '2.0', id: ++rid, method: 'initialize', params: { processId: process.pid, rootUri: uri(PROJ), capabilities: {} } });
await wait(2500); console.log('INITIALIZED sent, initResp=' + (initResp ? 'yes' : 'no'));
send({ jsonrpc: '2.0', method: 'initialized', params: {} });
const text = fs.readFileSync(FILE, 'utf8'); const lines = text.split(/\r?\n/);
send({ jsonrpc: '2.0', method: 'textDocument/didOpen', params: { textDocument: { uri: uri(FILE), languageId: 'shaderlab', version: 1, text } } });
await wait(4000); console.log('DIDOPEN done, requesting foldingRange...');
const caps = initResp && initResp.result && initResp.result.capabilities || {};
console.log('foldingRangeProvider 已宣告: ' + (caps.foldingRangeProvider === true));
const r = await ask('textDocument/foldingRange', { textDocument: { uri: uri(FILE) } });
const rs = (r.result || []).slice().sort((a, b) => a.startLine - b.startLine || b.endLine - a.endLine);
console.log('范围数: ' + rs.length);
for (const f of rs) {
  const head = (lines[f.startLine] || '').trim().slice(0, 78);
  console.log('  ' + String(f.startLine).padStart(4) + ' -> ' + String(f.endLine).padStart(4) + '   ' + head);
}
clearTimeout(hardStop);
p.kill(); process.exit(0);