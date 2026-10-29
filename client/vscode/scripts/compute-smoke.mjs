// compute 端到端冒烟：驱动打包后的真实 AOT exe（client/vscode/bin/UnityShaderLsp.exe），
// 用真实 stdio 帧验证 .compute 的编辑体验三件事：
//   1. didOpen 后能收到诊断（DXC 真的跑了）
//   2. `#pragma kernel ` 后的补全能给出本文件的 kernel 名
//   3. kernel 名上 F12 能跳到定义行
//
// 还要验证「不依赖 languageId」：客户端按 .hlsl/.shader 注册，
// 而 .compute 的 languageId 可能是 hlsl、也可能（没装内置 hlsl 扩展时）是 plaintext。
// 服务端不得按 languageId 过滤，否则用户在裸环境里就完全没有 compute 支持。
//
// 用法：node client/vscode/scripts/compute-smoke.mjs [exe]

import { spawn } from 'node:child_process';
import { readFileSync } from 'node:fs';
import * as path from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const repo = path.resolve(here, '..', '..', '..');
const exe = process.argv[2] ?? path.join(here, '..', 'bin', 'UnityShaderLsp.exe');
const project = process.env.MICROSHADER_UNITY_PROJECT ?? 'D:/Program Files/U3D/FamiliarWithURP';

const NL = String.fromCharCode(10);
const fixture = path.join(repo, 'probes', 'sample-compute-multikernel.compute');
const text = readFileSync(fixture, 'utf8');
const uri = pathToFileURL(fixture).href;

const failures = [];
function check(ok, label, detail) {
  if (ok) {
    console.log('  OK   ' + label + (detail ? '  (' + detail + ')' : ''));
  } else {
    failures.push(label + (detail ? ': ' + detail : ''));
    console.log('  FAIL ' + label + (detail ? '  (' + detail + ')' : ''));
  }
}

const child = spawn(exe, [], {
  env: { ...process.env, MICROSHADER_PARENT_PID: String(process.pid), MICROSHADER_UNITY_PROJECT: project },
});

let pending = Buffer.alloc(0);
const frames = [];
const waiters = [];

child.stdout.on('data', (chunk) => {
  pending = Buffer.concat([pending, chunk]);
  for (;;) {
    const headerEnd = pending.indexOf('\r\n\r\n');
    if (headerEnd < 0) break;
    const match = /Content-Length: (\d+)/i.exec(pending.slice(0, headerEnd).toString());
    if (!match) break;
    const need = Number(match[1]);
    if (pending.length < headerEnd + 4 + need) break;
    const body = pending.slice(headerEnd + 4, headerEnd + 4 + need).toString();
    pending = pending.slice(headerEnd + 4 + need);
    frames.push(JSON.parse(body));
    waiters.splice(0).forEach((w) => w());
  }
});
child.stderr.on('data', () => {});

function send(msg) {
  const body = Buffer.from(JSON.stringify(msg));
  child.stdin.write('Content-Length: ' + body.length + '\r\n\r\n');
  child.stdin.write(body);
}

// LSP 帧没有请求 id 的时候（publishDiagnostics）按 method 等。
function nextFrame(predicate, label, timeoutMs = 30000) {
  const hit = frames.find(predicate);
  if (hit) {
    frames.splice(frames.indexOf(hit), 1);
    return Promise.resolve(hit);
  }
  const timeout = timeoutMs;
  return new Promise((resolve, reject) => {
    const timer = setTimeout(() => reject(new Error('超时等待 ' + label)), timeout);
    const check2 = () => {
      const found = frames.find(predicate);
      if (found) {
        clearTimeout(timer);
        frames.splice(frames.indexOf(found), 1);
        resolve(found);
      } else {
        waiters.push(check2);
      }
    };
    check2();
  });
}

const byId = (id, label) => nextFrame((f) => f.id === id, label);

function position(needle, offsetInNeedle) {
  const at = text.indexOf(needle);
  if (at < 0) throw new Error('语料缺少 "' + needle + '"');
  const absolute = at + offsetInNeedle;
  const before = text.slice(0, absolute).split(NL);
  return { line: before.length - 1, character: before[before.length - 1].length };
}

function lineOf(needle) {
  const at = text.indexOf(needle);
  if (at < 0) throw new Error('语料缺少 "' + needle + '"');
  return text.slice(0, at).split(NL).length - 1;
}

function labels(result) {
  const items = Array.isArray(result) ? result : result?.items ?? [];
  return items.map((i) => i.label);
}

// ── 1. 握手 ──
send({ jsonrpc: '2.0', id: 1, method: 'initialize', params: { processId: process.pid, rootUri: null, capabilities: {} } });const init = await byId(1, 'initialize');
check(init?.result?.capabilities?.completionProvider !== undefined, 'initialize 宣告 completionProvider');
check(init?.result?.capabilities?.definitionProvider !== undefined, 'initialize 宣告 definitionProvider');
send({ jsonrpc: '2.0', method: 'initialized', params: {} });

// ── 2. didOpen（languageId 用 hlsl，模拟装了内置 hlsl 扩展的常见情形）→ 必须有诊断 ──
send({
  jsonrpc: '2.0',
  method: 'textDocument/didOpen',
  params: { textDocument: { uri, languageId: 'hlsl', version: 1, text } },
});
const push = await nextFrame(
  (f) => f.method === 'textDocument/publishDiagnostics' && f.params?.uri === uri,
  'compute 的 publishDiagnostics',
);
const diags = push.params.diagnostics ?? [];
check(diags.length === 0, '干净的多 kernel compute 应当 0 条诊断', '实际 ' + diags.length + ' 条：'
  + diags.map((d) => (d.code ?? d.severity) + '@' + d.range.start.line + ':' + d.message).join(' | '));

// “0 条诊断”本身证明不了 DXC 真的跑过 —— 再开一份故意埋错的样本，必须报出来。
{
  const brokenPath = path.join(repo, 'probes', 'sample-compute-broken.compute');
  const brokenText = readFileSync(brokenPath, 'utf8');
  const brokenUri = pathToFileURL(brokenPath).href;
  const brokenLine = (needle) => brokenText.slice(0, brokenText.indexOf(needle)).split(NL).length - 1;

  send({
    jsonrpc: '2.0',
    method: 'textDocument/didOpen',
    params: { textDocument: { uri: brokenUri, languageId: 'hlsl', version: 1, text: brokenText } },
  });
  const brokenPush = await nextFrame(
    (f) => f.method === 'textDocument/publishDiagnostics' && f.params?.uri === brokenUri,
    'broken.compute 的 publishDiagnostics',
  );
  const brokenDiags = brokenPush.params.diagnostics ?? [];
  const lines = new Set(brokenDiags.map((d) => d.range.start.line));
  check(lines.has(brokenLine('p.weigh')), '拼错的成员 p.weigh 报在 ' + (brokenLine('p.weigh') + 1) + ' 行',
    brokenDiags.map((d) => (d.code ?? d.severity) + '@' + (d.range.start.line + 1)).join(','));
  check(lines.has(brokenLine('missingValue')), '未声明标识符 missingValue 报在 ' + (brokenLine('missingValue') + 1) + ' 行');
  check(brokenDiags.some((d) => d.severity === 1), 'broken 样本必须有 Error 级别诊断');

  send({ jsonrpc: '2.0', method: 'textDocument/didClose', params: { textDocument: { uri: brokenUri } } });
  await nextFrame((f) => f.method === 'textDocument/publishDiagnostics' && f.params?.uri === brokenUri, 'broken didClose');
}

// ── 3. `#pragma kernel ` 后的补全（languageId 不影响）──
const pragma = position('#pragma kernel ', '#pragma kernel '.length);
send({
  jsonrpc: '2.0',
  id: 2,
  method: 'textDocument/completion',
  params: { textDocument: { uri }, position: pragma },
});
const completion = await byId(2, 'completion');
const candidates = labels(completion?.result);
check(candidates.includes('CSClear'), '#pragma kernel 后补出 CSClear', candidates.join(','));
check(candidates.includes('CSCopy'), '#pragma kernel 后补出 CSCopy', candidates.join(','));
check(!candidates.includes('outputBuffer'), '变量不得混进入口候选', candidates.join(','));

// ── 4. kernel 名上 F12 → 定义行 ──
const defPos = position('#pragma kernel CSClear', '#pragma kernel '.length + 2);
send({ jsonrpc: '2.0', id: 3, method: 'textDocument/definition', params: { textDocument: { uri }, position: defPos } });
const definition = await byId(3, 'definition');
const defItems = Array.isArray(definition?.result) ? definition.result : definition?.result ? [definition.result] : [];
const defLine = defItems[0]?.range?.start?.line ?? defItems[0]?.targetRange?.start?.line;
check(defLine === lineOf('void CSClear('), 'F12 跳到 void CSClear( 那一行',
  '期望 ' + lineOf('void CSClear(') + '，实际 ' + defLine);

// ── 5. 换成 plaintext（没装内置 hlsl 扩展的环境）→ 服务端不得因此拒绝 ──
send({ jsonrpc: '2.0', method: 'textDocument/didClose', params: { textDocument: { uri } } });
await nextFrame((f) => f.method === 'textDocument/publishDiagnostics' && f.params?.uri === uri, 'didClose 清空诊断');

send({
  jsonrpc: '2.0',
  method: 'textDocument/didOpen',
  params: { textDocument: { uri, languageId: 'plaintext', version: 1, text } },
});
const push2 = await nextFrame(
  (f) => f.method === 'textDocument/publishDiagnostics' && f.params?.uri === uri && f.params?.version === 1,
  'plaintext 的 publishDiagnostics',
);
check((push2.params.diagnostics ?? []).length === 0, 'languageId=plaintext 时分析照常（不按语言过滤）');

send({ jsonrpc: '2.0', id: 4, method: 'textDocument/completion', params: { textDocument: { uri }, position: pragma } });
const completion2 = await byId(4, 'plaintext 的 completion');
check(labels(completion2?.result).includes('CSClear'), 'languageId=plaintext 时补全照常', labels(completion2?.result).join(','));

// ── 6. 成员补全：结构体变量后敲 . 必须出字段（compute 里的高频动作）──
{
  const memberUri = 'file:///D:/smoke/Member.compute';
  const memberNeedle = 'p.color = inputBuffer';
  const memberText = [
    '#pragma kernel CSMain',
    '',
    'RWStructuredBuffer<float4> outputBuffer;',
    'RWStructuredBuffer<float4> inputBuffer;',
    '',
    'struct Payload { float4 color; float weight; };',
    '',
    '[numthreads(1, 1, 1)]',
    'void CSMain(uint3 id : SV_DispatchThreadID)',
    '{',
    '    Payload p;',
    '    ' + memberNeedle + ';',
    '    outputBuffer[id.x] = p.color;',
    '}',
    ''].join(NL);

  send({
    jsonrpc: '2.0',
    method: 'textDocument/didOpen',
    params: { textDocument: { uri: memberUri, languageId: 'hlsl', version: 1, text: memberText } },
  });
  await nextFrame((f) => f.method === 'textDocument/publishDiagnostics' && f.params?.uri === memberUri, 'member 样本诊断');

  // 光标停在点号之后（"p." 之后），才是 MemberAccess 触发（左侧字符必须是点）。
  const at = memberText.indexOf(memberNeedle);
  const before = memberText.slice(0, at + 2).split(NL);
  const memberPos = { line: before.length - 1, character: before[before.length - 1].length };
  send({ jsonrpc: '2.0', id: 5, method: 'textDocument/completion', params: { textDocument: { uri: memberUri }, position: memberPos } });
  const memberCompletion = await byId(5, '成员补全');
  const memberLabels = labels(memberCompletion?.result);
  check(memberLabels.includes('color') && memberLabels.includes('weight'),
    '结构体变量后敲 . 补出 color/weight', memberLabels.join(','));

  send({ jsonrpc: '2.0', method: 'textDocument/didClose', params: { textDocument: { uri: memberUri } } });
  await nextFrame((f) => f.method === 'textDocument/publishDiagnostics' && f.params?.uri === memberUri, 'member didClose');
}

// ── 7. 语义拼写错：SV_DispatchThreadID 写成 SV_DispatchThreadIDD 必须标红 ──
{
  const typoUri = 'file:///D:/smoke/Typo.compute';
  const typoText = [
    '#pragma kernel CSMain',
    '',
    'RWStructuredBuffer<float4> outputBuffer;',
    '',
    '[numthreads(1, 1, 1)]',
    'void CSMain(uint3 id : SV_DispatchThreadIDD)',
    '{',
    '    outputBuffer[id.x] = float4(1, 0, 0, 1);',
    '}',
    ''].join(NL);

  send({
    jsonrpc: '2.0',
    method: 'textDocument/didOpen',
    params: { textDocument: { uri: typoUri, languageId: 'hlsl', version: 1, text: typoText } },
  });
  const typoPush = await nextFrame(
    (f) => f.method === 'textDocument/publishDiagnostics' && f.params?.uri === typoUri,
    '语义拼写错诊断',
  );
  const typoDiags = typoPush.params.diagnostics ?? [];
  check(typoDiags.some((d) => d.severity === 1), 'SV_DispatchThreadIDD 拼写错必须标红',
    typoDiags.map((d) => d.range.start.line + ':' + d.message).join(' | '));

  send({ jsonrpc: '2.0', method: 'textDocument/didClose', params: { textDocument: { uri: typoUri } } });
  await nextFrame((f) => f.method === 'textDocument/publishDiagnostics' && f.params?.uri === typoUri, 'typo didClose');
}

// ── 8. 收尾 ──
send({ jsonrpc: '2.0', method: 'textDocument/didClose', params: { textDocument: { uri } } });
send({ jsonrpc: '2.0', id: 9, method: 'shutdown', params: null });
await byId(9, 'shutdown');
send({ jsonrpc: '2.0', method: 'exit', params: null });
await new Promise((resolve) => child.on('exit', resolve));

if (failures.length > 0) {
  console.log(NL + 'compute-smoke 失败 ' + failures.length + ' 项：');
  for (const f of failures) console.log('  - ' + f);
  process.exit(1);
}
console.log(NL + 'compute-smoke OK');