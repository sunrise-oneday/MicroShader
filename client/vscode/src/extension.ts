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
// 扩展停用后不再重建客户端：停用瞬间 client.stop() 也会触发 Stopped，
// 若不管，延时重启会在窗口关闭或重载过程中又拉起一个服务端进程。
let deactivated = false;
// 自动重启的退避参数：服务端一旦「启动即崩」，固定 1.5 秒重试会变成反复拉起进程的风暴。
// 连续自动重启的间隔逐次翻倍、封顶 30 秒；连上服务端或用户手动重启时清零。
const AUTO_RESTART_BASE_MS = 1500;
const AUTO_RESTART_MAX_MS = 30000;
let autoRestartCount = 0;

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
  // 同一个扩展宿主里「禁用再启用」不会重载模块，模块级标记必须在这里复位，
  // 否则停用留下的 deactivated 会让这一轮启动之后的所有自动重启都被挡掉。
  deactivated = false;
  autoRestartCount = 0;
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
  deactivated = true;
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

  // 默认（attachForeign 为 true）按「文件名模式」挂接，而不是只按 language 挂接。
  // .shader 的默认语言归属会被别的扩展（如 Unity 官方 vstuc 的 UnityShader）抢走，
  // 只写 language: 'shaderlab' 会让客户端在真实工程里永远选不上文档 —— 表现为
  // 「扩展装了、语法高亮还在、但补全一条都不出」。模式匹配与语言归属无关，因此稳。
  const attachForeign = vscode.workspace
    .getConfiguration('microshader')
    .get<boolean>('attachForeignLanguages', true);

  const selector: DocumentSelector = [];
  if (attachForeign) {
    // 默认行为：按扩展名接管，哪怕文件被别的扩展判成了别的语言。
    selector.push({ scheme: 'file', pattern: '**/*.shader' });
    selector.push({ scheme: 'file', pattern: '**/*.cginc' });
    selector.push({ scheme: 'file', pattern: '**/*.hlsl' });
    selector.push({ scheme: 'file', pattern: '**/*.glslinc' });
    selector.push({ scheme: 'file', pattern: '**/*.shaderinc' });
    selector.push({ scheme: 'file', pattern: '**/*.compute' });
    // 语言名兜底：覆盖 pattern 命不中的场景（如自定义虚拟文件系统里的 shader）。
    selector.push({ language: 'UnityShader', scheme: 'file' });
    selector.push({ language: 'hlsl', scheme: 'file' });
  } else {
    // 关掉外来语言接管后，只认 VS Code 自己判成 shaderlab 的文档。
    // 注意 .cginc 与 .hlsl 本扩展没有贡献过语言归属，实机上是 VS Code 内置 HLSL 扩展
    // 把它们注册成 hlsl 的；本扩展只靠上面的 pattern 命中，这里不再按扩展名接管，
    // 它们就不会被服务端处理 —— 这正是「不接管外来语言」想要的结果。
    selector.push({ language: 'shaderlab', scheme: 'file' });
  }
  // 未命名缓冲区没有文件名可匹配，两种模式下都只靠语言名接管。
  selector.push({ language: 'shaderlab', scheme: 'untitled' });

  const clientOptions: LanguageClientOptions = {
    documentSelector: selector,
    synchronize: { fileEvents: vscode.workspace.createFileSystemWatcher('**/*.{shader,cginc,hlsl,glslinc,shaderinc,compute}') },
    outputChannel,
    traceOutputChannel: traceChannel,
  };

  // 留一份本地引用：状态回调要用「事件来自哪个实例」做身份判断，
  // 不能读模块级的 client —— restart 会把它换成新实例，旧实例的迟到事件会被误认成当前实例。
  const instance = new LanguageClient(
    'unityShaderLsp',
    'MicroShader',
    serverOptions,
    clientOptions,
  );
  client = instance;

  instance.onDidChangeState((event) => {
    switch (event.newState) {
      case State.Starting:
        setStatus('$(sync~spin) MicroShader: 启动中');
        break;
      case State.Running:
        log('语言服务器已连接（就绪）');
        setStatus('$(check) MicroShader: 就绪');
        applyTrace();
        autoRestartCount = 0;  // 连上了就算这一轮恢复，下次异常重新从最短间隔起算
        break;
      case State.Stopped:
        log('语言服务器停止（将自动重试）');
        setStatus('$(circle-slash) MicroShader: 已停止');
        // restart 命令停旧客户端时也会走到这里，延时任务交给它自己排，避免两边各建一个客户端。
        if (restarting) return;
        void startAfterDelay(
          context,
          Math.min(AUTO_RESTART_BASE_MS * 2 ** autoRestartCount, AUTO_RESTART_MAX_MS),
          instance,
        );
        break;
    }
  });

  void instance.start().then(
    () => { applyTrace(); },
    (error: unknown) => { log('语言服务器启动失败: ' + String(error)); },
  );
}

// Stopped 状态的自动恢复：连续崩溃的上限由客户端库的 DefaultErrorHandler 挡，
// 这里只兜底「停住之后再没被拉起」的一次性异常退出。
//
// 不变量（此前这里是死码：Stopped 时 client 从不清空，client !== undefined 恒成立，
// 重启永不发生，用户看到的是补全突然消失、重载窗口才恢复）：
// 1. 只在延迟结束时、确认没人接手之后才清空 client。清早了，restart 命令与本任务
//    会各 new 一个客户端，同一份工程上跑两个服务端进程。
// 2. 清空前必须确认实例仍是当初那个，且它确实停在 State.Stopped：身份不符说明已被
//    restart 换新；状态不是 Stopped 说明上游已自行重启，此时再建客户端会把正在运行的
//    那一个变成没人管理、也停不掉的孤儿进程。
// 3. 从清空 client 到 start() 之间不得插入 await，保证别处读不到「client 为空但实例还活着」的中间态。
async function startAfterDelay(
  context: vscode.ExtensionContext,
  delayMs: number,
  stopped: LanguageClient,
): Promise<void> {
  await new Promise((resolve) => setTimeout(resolve, delayMs));
  if (deactivated) return;                      // 扩展已停用（窗口关闭/重载中），不再拉起服务端
  if (restarting) return;                       // restart 命令正在接管，它会自己 start
  if (client !== stopped) return;               // 已被 restart 换掉，或已被本函数清空
  if (stopped.state !== State.Stopped) return;  // 上游 DefaultErrorHandler 已自行重启
  autoRestartCount += 1;
  client = undefined;
  start(context);
}

async function restart(context: vscode.ExtensionContext): Promise<void> {
  restarting = true;
  autoRestartCount = 0;   // 手动重启是用户的意图，重新从最短间隔起算，不受此前退避影响
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