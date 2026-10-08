import * as fs from 'node:fs';
import * as path from 'node:path';
import * as vscode from 'vscode';

/** Unity 工程根的判定口径（与服务端 UnityProjectLocator 一致）。 */
const PROJECT_VERSION = path.join('ProjectSettings', 'ProjectVersion.txt');

/** 目录是否是 Unity 工程根：存在 ProjectSettings/ProjectVersion.txt。 */
export function isUnityProjectRoot(dir: string): boolean {
  try {
    return fs.statSync(path.join(dir, PROJECT_VERSION)).isFile();
  } catch {
    return false;
  }
}

/** 从目录向上逐级找 Unity 工程根。 */
function findUpward(startDir: string): string | undefined {
  let current = startDir;
  for (;;) {
    if (isUnityProjectRoot(current)) return current;
    const parent = path.dirname(current);
    if (parent === current) return undefined;
    current = parent;
  }
}

function isShaderLike(fsPath: string): boolean {
  return /\.(shader|cginc|hlsl|compute)$/i.test(fsPath);
}

/**
 * 解析要显式传给服务端的 Unity 工程根；找不到返回 undefined（保持旧行为）。
 *
 * 为什么必须显式传：服务端解析工程根的顺序是
 * `--project` → `MICROSHADER_UNITY_PROJECT` → **从 cwd 向上找 ProjectVersion.txt**，
 * 而 cwd 由 VS Code 决定：
 *   - 打开「工程文件夹」时 cwd = 该文件夹 ⇒ 服务端自己能找到（正常路径）；
 *   - 只打开「单个文件」（无工作区文件夹）时 cwd = VS Code 安装目录 ⇒ 永远找不到，
 *     服务端降级为空 VFS（0 个包、0 个编译单元、诊断只剩 MS0001）。
 * 2026-09-29 实测：同一 shader，文件夹窗口 = 84 个包 / 8 个编译单元 / 诊断为空；
 * 单文件窗口 = 0 个包 / 0 个编译单元 / MS0001。这里把工程根算出来，两种情况都稳。
 */
export function resolveUnityProjectRoot(): string | undefined {
  // ① 工作区文件夹里优先选**真正的 Unity 工程根**（多根工作区时就近取第一个含工程标记的）。
  const folders = (vscode.workspace.workspaceFolders ?? []).filter((f) => f.uri.scheme === 'file');
  for (const folder of folders) {
    if (isUnityProjectRoot(folder.uri.fsPath)) return folder.uri.fsPath;
  }

  // ② 无工作区、或工作区不含 Unity 工程：退回「已打开的 shader 文件向上找」，
  //    这正是「只打开单个 .shader」场景的唯一线索。
  const documents = [
    ...vscode.window.visibleTextEditors.map((e) => e.document),
    ...vscode.workspace.textDocuments,
  ];
  const seen = new Set<string>();
  for (const doc of documents) {
    if (doc.uri.scheme !== 'file' || !isShaderLike(doc.uri.fsPath)) continue;
    const dir = path.dirname(doc.uri.fsPath);
    if (seen.has(dir)) continue;
    seen.add(dir);
    const root = findUpward(dir);
    if (root !== undefined) return root;
  }

  return undefined;
}
