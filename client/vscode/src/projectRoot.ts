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
 * 解析要显式传给服务端的 Unity 工程根；找不到返回 undefined。
 *
 * 它的作用是给服务端一个「启动时的默认工程」：服务端解析顺序是
 * `--project` → `MICROSHADER_UNITY_PROJECT` → 从 cwd 向上找 ProjectVersion.txt，
 * 而 cwd 由 VS Code 决定（打开工程文件夹时就是该文件夹；只打开单个文件时是 VS Code 安装目录）。
 *
 * 注意它**不再是** include 能否解析的前提：服务端会按每个文件自己的位置解析所属工程，
 * 所以「父目录工作区」「只打开单个 .shader」都不会再退化成空索引。这里算出来的根只用于
 * 启动默认值（以及 Unity 桥接要连哪个工程）。见 docs/module-contracts.md 的「Unity 工程与包路径解析」。
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
