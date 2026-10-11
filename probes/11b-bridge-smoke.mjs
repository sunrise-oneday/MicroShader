// 11B 端到端冒烟：真实 Unity 桥接 + AOT 服务端 + LSP 回环。
//
// 验证链：AOT exe 启动 → 读 Library/MicroShader/endpoint.json → 外连 Unity 的 TCP 桥接
//        → 收到 onShaderContextChanged → 诊断带上编辑器权威宏。
//
// 报告两件事：
//   ① window/logMessage 里出现「Unity 桥接：已连接 Unity（pid …）」= 链路成立；
//   ② didOpen 一个 use #if defined(_OUTPUT_DEPTH) 的 shader，服务端不误报。
//
// 用法：
//   node probes/11b-bridge-smoke.mjs            # 用 client/vscode/bin 里的 AOT exe
//   MICROSHADER_LSP_EXE=<path> node probes/11b-bridge-smoke.mjs
import { spawn } from 'node:child_process';
import * as path from 'node:path';
import * as fs from 'node:fs';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const exe = process.env.MICROSHADER_LSP_EXE ?? path.join(here, '..', 'client', 'vscode', 'bin', 'UnityShaderLsp.exe');
const project = process.env.MICROSHADER_UNITY_PROJECT ?? 'D:/Program Files/U3D/ImproveCamera';

const NL = String.fromCharCode(10);
const doc = [
  'Shader "MicroShader/BridgeSmoke"',
  '{',
  '    SubShader',
  '    {',
  '        Pass',
  '        {',
  '            HLSLPROGRAM',
  '            #pragma vertex vert',
  '            #pragma fragment frag',
  '            #pragma multi_compile _ALPHA_TEST_FAKE _OUTPUT_DEPTH',
  '            float4 vert(float4 positionOS : POSITION) : SV_POSITION',
  '            {',
  '                return float4(positionOS.xyz, 1.0);',
  '            }',
  '            half4 frag() : SV_Target',
  '            {',
  '                return half4(1, 1, 1, 1);',
  '            }',
  '            ENDHLSL',
  '        }',
  '    }',
  '}'].join(NL) + NL;

const endpointPath = path.join(project, 'Library', 'MicroShader', 'endpoint.json');
console.log('endpoint 存在: ' + fs.existsSync(endpointPath));
if (fs.existsSync(endpointPath)) {
  const ep = JSON.parse(fs.readFileSync(endpointPath, 'utf8'));
  console.log('endpoint: transport=' + ep.transport + ' port=' + ep.port + ' pid=' + ep.unityPid + ' reloading=' + ep.reloading);
}

const docUri = 'file:///D:/smoke/BridgeSmoke.shader';

const child = spawn(exe, [], {
  env: {
    ...process.env,
    MICROSHADER_PARENT_PID: String(process.pid),
    MICROSHADER_UNITY_PROJECT: project,
    MICROSHADER_DXC_DIR: path.dirname(exe),
  },
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

const stderrChunks = [];
child.stderr.on('data', (c) => stderrChunks.push(c.toString()));

function send(msg) {
  const body = Buffer.from(JSON.stringify(msg));
  child.stdin.write('Content-Length: ' + body.length + '\r\n\r\n');
  child.stdin.write(body);
}

function nextFrame(predicate, timeoutMs, label) {
  const hit = frames.find(predicate);
  if (hit) return Promise.resolve(hit);
  const timeout = timeoutMs ?? 30000;
  return new Promise((resolve, reject) => {
    const timer = setTimeout(() => reject(new Error('超时等待 ' + label)), timeout);
    const check = () => {
      const hit2 = frames.find(predicate);
      if (hit2) { clearTimeout(timer); resolve(hit2); } else { waiters.push(check); }
    };
    check();
  });
}

const bridgeLogs = () => frames
  .filter((f) => f.method === 'window/logMessage')
  .map((f) => f.params?.message ?? '')
  .filter((m) => m.includes('Unity 桥接') || m.includes('UnityBridge') || m.includes('桥接'))
  .concat([]);

send({ jsonrpc: '2.0', id: 1, method: 'initialize', params: { processId: process.pid, rootUri: null, capabilities: {} } });
const init = await nextFrame((f) => f.id === 1, 30000, 'initialize');
console.log('initialize: textDocumentSync=' + JSON.stringify(init.result.capabilities.textDocumentSync));

send({ jsonrpc: '2.0', method: 'initialized', params: {} });
send({ jsonrpc: '2.0', method: 'textDocument/didOpen', params: { textDocument: { uri: docUri, languageId: 'shaderlab', version: 1, text: doc } } });

const publish = await nextFrame((f) => f.method === 'textDocument/publishDiagnostics', 40000, 'publishDiagnostics');
const diags = publish.params.diagnostics ?? [];
console.log('publishDiagnostics: ' + diags.length + ' 条');
for (const d of diags.slice(0, 5)) {
  console.log('   ' + (d.range?.start?.line + 1) + ':' + (d.range?.start?.character + 1) + ' [' + d.severity + '] ' + d.message);
}

// 桥接链路要给它一点时间建立（首次连接 + 首帧广播）。
await new Promise((r) => setTimeout(r, 3000));
const logs = bridgeLogs();
console.log('桥接日志 ' + logs.length + ' 行：');
for (const l of logs) console.log('   ' + l.replace(/\s+/g, ' ').slice(0, 200));

const connected = logs.some((l) => l.includes('已连接 Unity') || l.includes('Connected'));
console.log('结论：桥接链路 ' + (connected ? '成立（服务端已外连 Unity 并收到首帧）' : '未建立（须检查 endpoint/TCP/判活）'));
console.log('结论：诊断 ' + (diags.length === 0 ? '零条（无假红线）' : diags.length + ' 条（见上）'));

send({ jsonrpc: '2.0', id: 3, method: 'shutdown', params: null });
await nextFrame((f) => f.id === 3, 15000, 'shutdown');
send({ jsonrpc: '2.0', method: 'exit', params: null });
await new Promise((r) => child.on('exit', r));
console.log('smoke 结束');
process.exit(connected && diags.length === 0 ? 0 : 1);
