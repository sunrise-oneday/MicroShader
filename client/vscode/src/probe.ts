import * as fs from 'node:fs';
import * as path from 'node:path';
import * as vscode from 'vscode';

export type ProbeResult =
  | { kind: 'ok'; exePath: string }
  | { kind: 'faulted'; reason: string };

function isExe(p: string): boolean {
  return fs.existsSync(p) && fs.statSync(p).isFile();
}

// 交付形态的二进制随扩展分发在 bin/ 目录（打包脚本负责拷入）。
function bundledExe(context: vscode.ExtensionContext): string {
  return path.join(context.extensionPath, 'bin', 'UnityShaderLsp.exe');
}

function validateBeside(exePath: string): ProbeResult {
  if (!isExe(exePath)) {
    return { kind: 'faulted', reason: '未找到语言服务器: ' + exePath };
  }

  const dir = path.dirname(exePath);
  const dxc = path.join(dir, 'dxcompiler.dll');
  if (!fs.existsSync(dxc)) {
    return { kind: 'faulted', reason: '缺少 dxcompiler.dll（与 UnityShaderLsp.exe 同目录）: ' + dir };
  }

  // dxil.dll 可选（DXC 1.8.2505 起始终使用内部 validator，缺失不算故障）。
  const dxil = path.join(dir, 'dxil.dll');
  if (!fs.existsSync(dxil)) {
    void vscode.window.showInformationMessage('MicroShader: 未检测到 dxil.dll，兼容签名路径已启用。');
  }

  return { kind: 'ok', exePath };
}

// 二进制探测：设置覆盖（exe 或目录）优先，其次扩展内置 bin/。
export function resolveServerBinary(context: vscode.ExtensionContext): ProbeResult {
  // 交付二进制是 Windows x64 原生 AOT，别的平台上即使文件存在也执行不了。
  // 之前只判断「文件在不在」，在 macOS / Linux 上会被误判成可用，然后
  // 语言客户端反复启动失败、用户只看到状态栏在「已停止」和「启动中」之间跳。
  if (process.platform !== 'win32') {
    return {
      kind: 'faulted',
      reason: '本扩展的服务端是 Windows x64 原生程序，当前平台（' + process.platform + '）暂不支持。',
    };
  }

  const configured = vscode.workspace.getConfiguration('microshader').get<string>('serverPath', '').trim();
  if (configured.length > 0) {
    const asDir = path.join(configured, 'UnityShaderLsp.exe');
    if (isExe(configured)) {
      return validateBeside(configured);
    }
    if (isExe(asDir)) {
      return validateBeside(asDir);
    }
    return { kind: 'faulted', reason: 'microshader.serverPath 指向的位置没有 UnityShaderLsp.exe: ' + configured };
  }

  return validateBeside(bundledExe(context));
}