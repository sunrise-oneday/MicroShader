import { spawn } from 'node:child_process';
import * as fs from 'node:fs';

// 对「已装的交付二进制」发真实 LSP 报文：URP 补全五场景 + CBUFFER 折叠回归（0.1.12 起）。
// 可复跑、无硬编码路径 —— 目标目录须含两个验收样本：CustomShadowDemo.shader 与
// ESMBlur.shader（0.1.12 的现场复现语料；锚点文本与这两个文件绑定）。
//
// 用法: node probes/urp-completion.mjs <UnityShaderLsp.exe> <Unity 工程根> <Shaders 目录>

const SEP = String.fromCharCode(13, 10);
const EXE = process.argv[2];
const PROJ = (process.argv[3] || '').replace(/\/+$/, '');
const SHADER_DIR = (process.argv[4] || '').replace(/\/+$/, '');

if (!EXE || !PROJ || !SHADER_DIR) {
  console.error('用法: node probes/urp-completion.mjs <UnityShaderLsp.exe> <Unity 工程根> <Shaders 目录>');
  process.exit(1);
}

const DEMO = SHADER_DIR + '/CustomShadowDemo.shader';
const ESM = SHADER_DIR + '/ESMBlur.shader';

const hardStop = setTimeout(() => { console.error('PROBE_TIMEOUT'); process.exit(2); }, 90000);
const uri = (p) => 'file:///' + p.replace(/^([a-z]):/i, (m, d) => d.toUpperCase() + ':').replace(/ /g, '%20');

const p = spawn(EXE, ['--project', PROJ], { stdio: ['pipe', 'pipe', 'pipe'] });
p.on('error', (e) => { console.error('SPAWN ERROR ' + e.message); process.exit(3); });

let buf = Buffer.alloc(0);
const pending = new Map();
let initResp = null;
p.stdout.on('data', (d) => {
  buf = Buffer.concat([buf, d]);
  for (;;) {
    const h = buf.indexOf(Buffer.from(SEP + SEP));
    if (h < 0) break;
    const m = /Content-Length: (\d+)/.exec(buf.slice(0, h).toString());
    if (!m) { buf = buf.slice(h + 4); continue; }
    const len = parseInt(m[1], 10);
    if (buf.length < h + 4 + len) break;
    let f = null;
    try { f = JSON.parse(buf.slice(h + 4, h + 4 + len).toString('utf8')); } catch { }
    buf = buf.slice(h + 4 + len);
    if (!f) continue;
    if (f.id !== undefined && pending.has(f.id)) { pending.get(f.id)(f); pending.delete(f.id); }
    else if (f.id !== undefined && !initResp) initResp = f;
  }
});
let errOut = '';
p.stderr.on('data', (d) => { errOut += d.toString().slice(0, 400); });

const send = (o) => { const b = JSON.stringify(o); p.stdin.write('Content-Length: ' + Buffer.byteLength(b) + SEP + SEP + b); };
const wait = (ms) => new Promise((r) => setTimeout(r, ms));
let rid = 0;
const ask = async (m, q) => {
  const id = ++rid;
  return await new Promise((res) => { pending.set(id, res); send({ jsonrpc: '2.0', id, method: m, params: q }); });
};

const posOf = (text, offset) => {
  let line = 0, start = 0;
  for (let i = 0; i < offset && i < text.length; i++) { if (text.charCodeAt(i) === 10) { line++; start = i + 1; } }
  return { line, character: offset - start };
};

let passed = 0, failed = 0;
const check = (ok, name, extra) => {
  console.log((ok ? '  PASS ' : '  FAIL ') + name + (extra === undefined ? '' : '  [' + extra + ']'));
  if (ok) passed++; else failed++;
};
const has = (items, label) => items.some((i) => i.label === label);

const completionAt = async (file, text, offset) => {
  const r = await ask('textDocument/completion', { textDocument: { uri: uri(file) }, position: posOf(text, offset) });
  return r && r.result && Array.isArray(r.result.items) ? r.result.items : [];
};

// ── 启动 ──
send({ jsonrpc: '2.0', id: ++rid, method: 'initialize', params: { processId: process.pid, rootUri: uri(PROJ), capabilities: {} } });
await wait(2500);
send({ jsonrpc: '2.0', method: 'initialized', params: {} });
const caps = (initResp && initResp.result && initResp.result.capabilities) || {};
const serverName = initResp && initResp.result && initResp.result.serverInfo && initResp.result.serverInfo.name;
console.log('服务端: ' + serverName);
check(caps.completionProvider !== undefined, '宣告 completionProvider');
check(caps.foldingRangeProvider === true, '宣告 foldingRangeProvider');

// ── CustomShadowDemo.shader：五场景 ──
console.log('--- CustomShadowDemo.shader ---');
const demo = fs.readFileSync(DEMO, 'utf8');
const demoUri = uri(DEMO);
send({ jsonrpc: '2.0', method: 'textDocument/didOpen', params: { textDocument: { uri: demoUri, languageId: 'shaderlab', version: 1, text: demo } } });
await wait(5000);   // 等 include 链（真实 URP 包）冷启动预热

const a1 = demo.indexOf('TransformObjectToWorld(IN.positionOS.xyz)') + 'Transform'.length;
const items1 = await completionAt(DEMO, demo, a1);
check(has(items1, 'TransformObjectToHClip'), '输入 Transform → 含 TransformObjectToHClip', items1.length + ' 条');

const a2 = demo.indexOf('CBUFFER_START(UnityPerMaterial)') + 'CBUFFER_START('.length;
const items2 = await completionAt(DEMO, demo, a2);
check(items2.length > 0 && items2[0].label === 'UnityPerMaterial', 'CBUFFER_START( → 第一条 UnityPerMaterial', items2.map((i) => i.label).join(','));

const a3 = demo.indexOf('CBUFFER_START(UnityPerMaterial)') + 'CBUFFER_START(Uni'.length;
const items3 = await completionAt(DEMO, demo, a3);
check(items3.length > 0 && items3[0].label === 'UnityPerMaterial', 'CBUFFER_START(Uni → 第一条 UnityPerMaterial', items3.map((i) => i.label).join(','));

const a4 = demo.indexOf('SAMPLER(sampler_BaseMap)') + 'SAMPLER('.length;
const items4 = await completionAt(DEMO, demo, a4);
check(has(items4, 'sampler_BaseMap'), 'SAMPLER( → 含 sampler_BaseMap', items4.map((i) => i.label).join(','));

const a5 = demo.indexOf('SAMPLER(sampler_BaseMap)') + 'SAMPL'.length;
const items5 = await completionAt(DEMO, demo, a5);
check(has(items5, 'SAMPLER'), '输入 SAMPL → 含 SAMPLER');

// 内建类型（0.1.13）：输入 float 应给 float2 / float4 等，且不被 URP 宏（FRAMEBUFFER_INPUT_FLOAT）淹没。
const a7 = demo.indexOf('float4 _BaseColor;') + 'float'.length;
const items7 = await completionAt(DEMO, demo, a7);
check(has(items7, 'float2') && has(items7, 'float4') && has(items7, 'float4x4'),
  '输入 float → 含 float2 / float4 / float4x4', items7.slice(0, 6).map((i) => i.label).join(','));

// ── 折叠：CBUFFER_START 块 ──
const fr = await ask('textDocument/foldingRange', { textDocument: { uri: demoUri } });
const ranges = (fr && fr.result) || [];
const demoLines = demo.split(/\r?\n/);
const cbufferRange = ranges.find((r) => (demoLines[r.startLine] || '').includes('CBUFFER_START(UnityPerMaterial)'));
check(!!cbufferRange, '折叠范围含 CBUFFER_START 块',
  cbufferRange ? cbufferRange.startLine + ' -> ' + cbufferRange.endLine : '未找到');

// ── ESMBlur.shader：_MainTex_ST / _MainTex_TexelSize 推导 ──
console.log('--- ESMBlur.shader（模拟正在写 float4 _Mai）---');
const esm = fs.readFileSync(ESM, 'utf8');
const esmUri = uri(ESM);
send({ jsonrpc: '2.0', method: 'textDocument/didOpen', params: { textDocument: { uri: esmUri, languageId: 'shaderlab', version: 1, text: esm } } });
await wait(3000);

const esmSim = esm.replace('float4 _MainTex_ST;', 'float4 _Mai');
send({ jsonrpc: '2.0', method: 'textDocument/didChange', params: { textDocument: { uri: esmUri, version: 2 }, contentChanges: [{ text: esmSim }] } });
await wait(1200);

const a6 = esmSim.indexOf('float4 _Mai') + 'float4 _Mai'.length;
const items6 = await completionAt(ESM, esmSim, a6);
check(has(items6, '_MainTex_ST') && has(items6, '_MainTex_TexelSize'), '输入 _Mai → 含 _MainTex_ST 与 _MainTex_TexelSize',
  items6.map((i) => i.label).join(','));

// ── 收尾 ──
send({ jsonrpc: '2.0', method: 'textDocument/didClose', params: { textDocument: { uri: demoUri } } });
send({ jsonrpc: '2.0', method: 'textDocument/didClose', params: { textDocument: { uri: esmUri } } });
await wait(300);

console.log('==== 合计: ' + (passed + failed) + '  通过: ' + passed + '  失败: ' + failed);
if (errOut.length > 0) console.log('stderr: ' + errOut);
clearTimeout(hardStop);
p.kill();
process.exit(failed === 0 ? 0 : 1);
