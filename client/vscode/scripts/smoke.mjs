import { spawn } from 'node:child_process';
import * as path from 'node:path';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const exe = path.join(here, '..', 'bin', 'UnityShaderLsp.exe');
const project = process.env.MICROSHADER_UNITY_PROJECT ?? 'D:/Program Files/U3D/FamiliarWithURP';

const NL = String.fromCharCode(10);
const doc = ["Shader \"T\"",
  "{",
  "    SubShader",
  "    {",
  "        Pass",
  "        {",
  "            HLSLPROGRAM",
  "            struct Varyings { float4 positionCS; half4 color; };",
  "            Varyings vert(Varyings input) { return input; }",
  "            half4 frag(Varyings i) : SV_Target { return i.color; }",
  "            ENDHLSL",
  "        }",
  "    }",
  "}"].join(NL) + NL;
const docUri = 'file:///D:/smoke/Wire.shader';

const child = spawn(exe, [], { env: { ...process.env, MICROSHADER_PARENT_PID: String(process.pid), MICROSHADER_UNITY_PROJECT: project } });
let pending = Buffer.alloc(0);
const frames = [];
const waiters = [];

child.stdout.on('data', (chunk) => {
  pending = Buffer.concat([pending, chunk]);
  for (;;) {
    const headerEnd = pending.indexOf('\r\n\r\n');
    if (headerEnd < 0) break;
    const header = pending.slice(0, headerEnd).toString();
    const match = /Content-Length: (\d+)/i.exec(header);
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

function nextFrame(matchId, timeoutMs) {
  const hit = frames.find((f) => f.id === matchId);
  if (hit) return Promise.resolve(hit);
  const timeout = timeoutMs ?? 20000;
  return new Promise((resolve, reject) => {
    const timer = setTimeout(() => reject(new Error('超时等 id=' + matchId)), timeout);
    const check = () => {
      const hit2 = frames.find((f) => f.id === matchId);
      if (hit2) { clearTimeout(timer); resolve(hit2); } else { waiters.push(check); }
    };
    check();
  });
}

send({ jsonrpc: '2.0', id: 1, method: 'initialize', params: { processId: process.pid, rootUri: null, capabilities: {} } });
const init = await nextFrame(1);
console.log('initialize: definition=' + JSON.stringify(init.result.capabilities.definitionProvider) + ' documentLink=' + JSON.stringify(init.result.capabilities.documentLinkProvider));

send({ jsonrpc: '2.0', method: 'initialized', params: {} });
send({ jsonrpc: '2.0', method: 'textDocument/didOpen', params: { textDocument: { uri: docUri, languageId: 'shaderlab', version: 1, text: doc } } });

const fAt = doc.indexOf('half4 frag(V i)');
const line = doc.slice(0, fAt).split(NL).length - 1;
const lineStart = doc.lastIndexOf(NL, fAt - 1) + 1;
const character = fAt - lineStart + 10;

send({ jsonrpc: '2.0', id: 4, method: 'textDocument/documentSymbol', params: { textDocument: { uri: docUri } } });
const syms = await nextFrame(4);
const flat = JSON.stringify(syms.result);
console.log('documentSymbol: frag=' + flat.includes('"frag"') + ' vert=' + flat.includes('"vert"') + ' Varyings=' + flat.includes('"Varyings"'));

  // 从大纲树里取 frag 的 selectionRange 作为光标（消除手算光标偏差）
  function findSym(nodes, name) {
    for (const n of nodes) {
      if (n.name === name) return n;
      if (n.children) { const hit = findSym(n.children, name); if (hit) return hit; }
    }
    return undefined;
  }
  let cursor = { line: 0, character: 0 };
  if (Array.isArray(syms.result)) {
    const fragSym = findSym(syms.result, 'frag');
    if (fragSym && fragSym.selectionRange) cursor = fragSym.selectionRange.start;
    else if (fragSym && fragSym.location) cursor = fragSym.location.range.start;
    console.log('大纲 frag 节点: ' + JSON.stringify(fragSym ? { range: fragSym.range, selection: fragSym.selectionRange } : null));
  }

send({ jsonrpc: '2.0', id: 2, method: 'textDocument/definition', params: { textDocument: { uri: docUri }, position: cursor } });
const def = await nextFrame(2);
console.log('definition: ' + JSON.stringify(def.result));

send({ jsonrpc: '2.0', id: 3, method: 'shutdown', params: null });
await nextFrame(3);
send({ jsonrpc: '2.0', method: 'exit', params: null });
await new Promise((r) => child.on('exit', r));
console.log('smoke OK');
