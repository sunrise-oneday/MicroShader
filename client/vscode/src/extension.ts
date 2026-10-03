import { createHash } from 'node:crypto';
import * as fs from 'node:fs';
import * as vscode from 'vscode';
import {
  DocumentSelector,
  LanguageClient,
  LanguageClientOptions,
  ServerOptions,
  State,
  Trace,
} from 'vscode-languageclient/node';
import { resolveServerBinary } from './probe';
import { resolveUnityProjectRoot } from './projectRoot';

let client: LanguageClient | undefined;
let statusItem: vscode.StatusBarItem | undefined;
let outputChannel: vscode.OutputChannel | undefined;
let traceChannel: vscode.OutputChannel | undefined;
let restarting = false;

// 最近一次启动用到的服务端路径与工程根：状态栏 tooltip 与「复制诊断信息」都要用。
let lastServerPath: string | undefined;
let lastProjectRoot: string | undefined;

// 输出通道只能写、不能回读，所以自己留一份有界副本，
// 让「复制诊断信息」能带上一段现场日志，用户求助时不用再手工翻 Output 面板。
const LOG_RING_LIMIT = 60;
const logRing: string[] = [];

/** 同时写进输出通道与内存副本；所有对用户可见的进度信息都走这里。 */
function log(message: string): void {
  outputChannel?.appendLine(message);
  logRing.push(message);
  if (logRing.length > LOG_RING_LIMIT) logRing.shift();
}

export function activate(context: vscode.ExtensionContext): void {
  statusItem = vscode.window.createStatusBarItem(vscode.StatusBarAlignment.Right, 100);
  context.subscriptions.push(statusItem);

  outputChannel = vscode.window.createOutputChannel('MicroShader');
  context.subscriptions.push(outputChannel);

  // LSP 报文单独一个通道：它是排查「补全不出候选」时唯一的原始证据，混在主日志里会淹掉。
  traceChannel = vscode.window.createOutputChannel('MicroShader (LSP Trace)');
  context.subscriptions.push(traceChannel);

  context.subscriptions.push(
    vscode.commands.registerCommand('unityShaderLsp.restart', async () => {
      await restart(context);
    }),
  );

  // 一眼可诊断：激活了没、语言映射成了什么、服务器连上没。
  // 之前这类问题（补全静默消失）在编辑器里完全没有线索可查。
  context.subscriptions.push(
    vscode.commands.registerCommand('unityShaderLsp.diagnose', async () => {
      const editor = vscode.window.activeTextEditor;
      const lang = editor ? editor.document.languageId : '<no active editor>';
      const file = editor ? editor.document.fileName : '<none>';
      const state = client === undefined ? 'not started' : client.state;
      const msg = 'MicroShader 诊断: languageId=' + lang + ' | file=' + file + ' | clientState=' + state;
      log(msg);
      outputChannel?.show(true);
      void vscode.window.showInformationMessage(msg);
    }),
  );

  context.subscriptions.push(
    vscode.commands.registerCommand('unityShaderLsp.showLog', () => {
      outputChannel?.show(true);
    }),
  );

  // 把用户没法自己拼出来的现场信息一次收齐，直接进剪贴板，便于贴到 issue 里。
  context.subscriptions.push(
    vscode.commands.registerCommand('unityShaderLsp.copyDiagnostics', async () => {
      await vscode.env.clipboard.writeText(collectDiagnostics());
      void vscode.window.showInformationMessage('MicroShader 诊断信息已复制到剪贴板。');
    }),
  );

  // 服务端只在启动时读这些配置，改完不重启会让人以为「设置没生效」。
  // 之前这条信息只写在设置项的 markdownDescription 里，等于没有。
  context.subscriptions.push(
    vscode.workspace.onDidChangeConfiguration((event) => {
      if (!event.affectsConfiguration('microshader')) return;
      const needsRestart = [
        'microshader.serverPath',
        'microshader.attachForeignLanguages',
        'microshader.analysisDebounceMs',
        'microshader.maxCompileThreads',
        'microshader.syncIncludeFallbackFiles',
        'microshader.trace',
      ].some((key) => event.affectsConfiguration(key));
      if (!needsRestart) return;
      void vscode.window
        .showWarningMessage('MicroShader 设置已变更，需要重启语言服务器才会生效。', '立即重启')
        .then((choice) => {
          if (choice === '立即重启') void restart(context);
        });
    }),
  );

  log('MicroShader 扩展已激活（' + new Date().toISOString() + '）');
  start(context);
}

export function deactivate(): Thenable<void> | undefined {
  return client?.stop();
}

/**
 * 把语言服务器二进制的体积与 SHA-256 写进输出通道。
 *
 * 为什么值得在激活路径上做一次 3.8 MB 的哈希（约 10 ms，异步、不阻塞激活）：
 * 「装的 exe 不是最新的」这类问题**没有任何其它可观察信号** ——
 * 语言服务器会正常启动、正常应答，只是新功能整体不存在。
 * 有了这一行，`dist/stg/bin` 与扩展目录的哈希可以直接比对。
 */
function logServerBinaryIdentity(exePath: string): void {
  void (async () => {
    try {
      const bytes = await fs.promises.readFile(exePath);
      const sha256 = createHash('sha256').update(bytes).digest('hex').toUpperCase();
      log(
        '语言服务器二进制: ' + exePath
        + '（' + bytes.length + ' 字节，SHA-256 ' + sha256 + '）',
      );
    } catch (error) {
      log('语言服务器二进制校验失败: ' + String(error));
    }
  })();
}

/**
 * 读取三个性能旋钮。未设置（null）时返回 undefined —— 表示「不传参数」，
 * 由服务端使用自己的默认值。这样「没配过」与「配成默认值」在行为上完全一致，
 * 也保证升级扩展不会悄悄改掉服务端的既有默认。
 */
function readPerfTuning(): {
  analysisDebounceMs?: number;
  maxCompileThreads?: number;
  syncIncludeFallbackFiles?: number;
} {
  const config = vscode.workspace.getConfiguration('microshader');
  const pick = (key: string): number | undefined => {
    const raw = config.get<number | null>(key, null);
    return typeof raw === 'number' && Number.isFinite(raw) ? Math.trunc(raw) : undefined;
  };
  return {
    analysisDebounceMs: pick('analysisDebounceMs'),
    maxCompileThreads: pick('maxCompileThreads'),
    syncIncludeFallbackFiles: pick('syncIncludeFallbackFiles'),
  };
}

function start(context: vscode.ExtensionContext): void {
  const probe = resolveServerBinary(context);
  if (probe.kind === 'faulted') {
    enterFaulted(probe.reason);
    return;
  }

  setStatus('$(sync~spin) MicroShader: 启动中');

  // 防孤儿由服务端自管：父进程 PID 经环境变量传入，父死则服务端 5 秒内自行退出
  // （详设 v2.0 第 1b 条）。崩溃重启仲裁交给 vscode-languageclient 内建的
  // DefaultErrorHandler（3 分钟窗口内 4 次上限），不自研看门狗。
  // 关键：显式把 Unity 工程根传给服务端。
  // 服务端的解析顺序是 --project → MICROSHADER_UNITY_PROJECT → 从 cwd 向上找，
  // 而「只打开单个文件」时 cwd 是 VS Code 安装目录，服务端必然找不到工程根，
  // 于是降级为空 VFS（0 个编译单元、诊断只剩 MS0001）。
  const projectRoot = resolveUnityProjectRoot();

  // 性能旋钮：默认一律「不传」，服务端保持自己的默认值（零行为漂移）；
  // 只有用户显式设了值才转成命令行参数。服务端只在启动时读取这些参数，
  // 因此改完需要执行「MicroShader: 重启语言服务器」。
  const perf = readPerfTuning();
  const args: string[] = [];
  if (projectRoot !== undefined) {
    args.push('--project', projectRoot);
  }
  if (perf.analysisDebounceMs !== undefined) {
    args.push('--debounce', String(perf.analysisDebounceMs));
  }
  if (perf.maxCompileThreads !== undefined) {
    args.push('--max-compile-threads', String(perf.maxCompileThreads));
  }
  if (perf.syncIncludeFallbackFiles !== undefined) {
    args.push('--sync-include-fallback', String(perf.syncIncludeFallbackFiles));
  }

  const serverOptions: ServerOptions = {
    command: probe.exePath,
    args,
    options: {
      env: {
        ...process.env,
        MICROSHADER_PARENT_PID: String(process.pid),
        ...(projectRoot === undefined ? {} : { MICROSHADER_UNITY_PROJECT: projectRoot }),
      },
    },
  };

  lastServerPath = probe.exePath;
  lastProjectRoot = projectRoot;

  log(
    projectRoot === undefined
      ? 'Unity 工程根: <未找到>（服务端将回退为「从 cwd 向上找」，单文件窗口下会退化成空索引）'
      : 'Unity 工程根: ' + projectRoot + '（已通过 --project 传给服务端）',
  );
  log('服务端参数: ' + (args.length === 0 ? '<全部使用默认>' : args.join(' ')));

  // 二进制身份留痕：2026-10-01 实测事故 —— 模块 11B 写完后 dist/stg/bin 已是新 exe，
  // 但扩展目录里仍是几小时前的旧 exe，于是「代码写完了、功能却没生效」，
  // 而症状只是「Output 面板里什么都没有」。把体积 + SHA-256 打出来，漂移一眼可见。
  logServerBinaryIdentity(probe.exePath);

  // 关键：按「文件名模式」挂接，而不是只按 language 挂接。
  // .shader 的默认语言归属会被别的扩展（如 Unity 官方 vstuc 的 UnityShader）抢走，
  // 只写 language: 'shaderlab' 会让客户端在真实工程里永远选不上文档 —— 表现为
  // 「扩展装了、语法高亮还在、但补全一条都不出」。模式匹配与语言归属无关，因此稳。
  const attachForeign = vscode.workspace
    .getConfiguration('microshader')
    .get<boolean>('attachForeignLanguages', true);

  const selector: DocumentSelector = [
    { scheme: 'file', pattern: '**/*.shader' },
    { scheme: 'file', pattern: '**/*.cginc' },
    { language: 'shaderlab', scheme: 'untitled' },
  ];
  if (attachForeign) {
    // 语言名兜底：覆盖 pattern 命不中的场景（如自定义虚拟文件系统里的 shader）。
    selector.push({ language: 'UnityShader', scheme: 'file' });
    selector.push({ language: 'hlsl', scheme: 'file' });
  }

  const clientOptions: LanguageClientOptions = {
    documentSelector: selector,
    synchronize: { fileEvents: vscode.workspace.createFileSystemWatcher('**/*.{shader,cginc}') },
    outputChannel,
    traceOutputChannel: traceChannel,
  };

  client = new LanguageClient(
    'unityShaderLsp',
    'MicroShader',
    serverOptions,
    clientOptions,
  );

  client.onDidChangeState((event) => {
    switch (event.newState) {
      case State.Starting:
        setStatus('$(sync~spin) MicroShader: 启动中');
        break;
      case State.Running:
        log('语言服务器已连接（就绪）');
        setStatus('$(check) MicroShader: 就绪');
        applyTrace();
        break;
      case State.Stopped:
        log('语言服务器停止（将自动重试）');
        setStatus('$(circle-slash) MicroShader: 已停止');
        if (restarting) return;
        void startAfterDelay(context, 1500);
        break;
    }
  });

  void client.start().then(
    () => { applyTrace(); },
    (error: unknown) => { log('语言服务器启动失败: ' + String(error)); },
  );
}

// Stopped 状态的自动恢复：客户端库的重启仲裁已在上游拦住连续崩溃，
// 这里兜底处理「探测失败后修好了配置」或一次性异常退出，带退避避免死循环。
async function startAfterDelay(context: vscode.ExtensionContext, delayMs: number): Promise<void> {
  await new Promise((resolve) => setTimeout(resolve, delayMs));
  if (client !== undefined) return;   // 已被 restart 接管
  start(context);
}

async function restart(context: vscode.ExtensionContext): Promise<void> {
  restarting = true;
  try {
    if (client !== undefined) await client.stop();
  } finally {
    client = undefined;
    restarting = false;
    start(context);
  }
}

function enterFaulted(reason: string): void {
  client = undefined;
  setStatus('$(error) MicroShader: 未就绪');
  void vscode.window.showErrorMessage('MicroShader 语言服务器未能启动: ' + reason,
    '打开设置').then((choice) => {
      if (choice === '打开设置') {
        void vscode.commands.executeCommand('workbench.action.openSettings', 'microshader.serverPath');
      }
    });
}

function setStatus(text: string): void {
  if (statusItem === undefined) return;
  statusItem.text = text;
  // tooltip 里带上「服务端在哪、工程根认到哪」——这两个是绝大多数排查的起点，
  // 之前只能靠命令面板里的诊断命令或翻 Output 面板才能看到。
  statusItem.tooltip = [
    'MicroShader Unity Shader 语言服务器',
    '服务端: ' + (lastServerPath ?? '<未启动>'),
    'Unity 工程根: ' + (lastProjectRoot ?? '<未找到>'),
    '点击重启语言服务器',
  ].join('\n');
  statusItem.command = 'unityShaderLsp.restart';
  statusItem.show();
}

/** 读取 microshader.trace；取值非法时按 off 处理。 */
function readTraceSetting(): 'off' | 'messages' | 'verbose' {
  const raw = vscode.workspace.getConfiguration('microshader').get<string>('trace', 'off');
  if (raw === 'messages') return 'messages';
  if (raw === 'verbose') return 'verbose';
  return 'off';
}

/** 把 trace 设置应用到语言客户端；客户端尚未创建时什么都不做（启动完成后会再调一次）。 */
function applyTrace(): void {
  if (client === undefined) return;
  const value = readTraceSetting();
  const trace = value === 'verbose' ? Trace.Verbose : value === 'messages' ? Trace.Messages : Trace.Off;
  void client.setTrace(trace);
}

/** 汇总用户自己很难拼出来的现场信息，供「复制诊断信息」与求助贴使用。 */
function collectDiagnostics(): string {
  const extension = vscode.extensions.getExtension('microshader.unity-shader-lsp');
  const version = (extension?.packageJSON as { version?: string } | undefined)?.version;
  const folders = (vscode.workspace.workspaceFolders ?? []).map((f) => f.uri.fsPath);
  return [
    '=== MicroShader 诊断信息 ===',
    '扩展版本: ' + (version ?? '<未知>'),
    'VS Code: ' + vscode.version,
    '宿主与平台: ' + vscode.env.appHost + ' / ' + process.platform + '-' + process.arch,
    '工作区文件夹: ' + (folders.length === 0 ? '<无>' : folders.join(' | ')),
    'microshader.serverPath 设置: '
      + (vscode.workspace.getConfiguration('microshader').get<string>('serverPath', '') || '<空>'),
    '实际使用的服务端: ' + (lastServerPath ?? '<未启动>'),
    '解析到的 Unity 工程根: ' + (lastProjectRoot ?? '<未找到>'),
    '客户端状态: ' + (client === undefined ? 'not started' : String(client.state)),
    'trace 设置: ' + readTraceSetting(),
    '',
    '=== 最近 ' + logRing.length + ' 行输出 ===',
    ...logRing,
  ].join('\n');
}