# MicroShader

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

Unity ShaderLab / HLSL 的语言服务器——在 VS Code 里写 shader 时实时给出 DXC 编译诊断、成员补全和定义跳转。

## 功能

- **即时诊断** — 打字停顿 300ms 触发当前 Pass 的诊断；`Ctrl+S` 触发全文件全量诊断，无需切回 Unity 看 Console
- **URP 路径穿透** — 直接理解 `#include "Packages/com.unity.render-pipelines.universal/...""`，覆盖 5 类包来源
- **精确错误归属** — 注入 `#line` 锚定，外部头文件的错误重映射回本文件的 `#include` 行
- **智能补全** — 结构体成员 / 内建向量分量 / 文档内符号 / include 链符号预热
- **F12 跳转** — 定义穿透 + `#include` 超链接 + 文档大纲
- **极低占用** — Native AOT 原生 exe ~3.7 MB，常驻内存 ≤ 30 MB，冷启动 ≤ 30 ms
- **零第三方依赖** — 构建可完全离线，DXC 走自研 COM vtable 垫片

## 架构

```text
┌─────────────────────────────────────────────────────┐
│         前端 (VS Code .vsix / Sublime LSP)           │
└──────────────────────────▲──────────────────────────┘
                           │ LSP 3.17 over Stdio
┌──────────────────────────▼──────────────────────────┐
│         UnityShaderLsp.exe (.NET 10 Native AOT)      │
│                                                      │
│  切片 → VFS 路径解析 → 装配锚定 → DXC 编译 → 诊断归属  │
└──────────────────────────┬──────────────────────────┘
                           │ 进程内 P/Invoke
┌──────────────────────────▼──────────────────────────┐
│              dxcompiler.dll (DXC SM6.0)               │
└─────────────────────────────────────────────────────┘
```

## 快速开始

### 构建

需要 .NET SDK 10（见 `global.json`）和 Node.js（扩展打包用）。

```bash
git clone https://github.com/sunrise-oneday/MicroShader.git
cd MicroShader
dotnet build MicroShader.sln -c Release
```

### 自检

```powershell
$env:MICROSHADER_UNITY_PROJECT = '<你的 Unity 工程根>'
dotnet run --project src\MicroShader.SelfTest -c Release -- --urp '<URP 包目录>'
```

### 打包安装 VS Code 扩展

```powershell
pwsh -File scripts\publish-aot.ps1
pwsh -File scripts\package-vsix.ps1 -Install
```

装完后 `Ctrl+Shift+P` → `Developer: Reload Window`。

## 文档

| 文档 | 内容 |
|---|---|
| [ARCHITECTURE.md](ARCHITECTURE.md) | 架构总览、模块速查 |
| [CONTRIBUTING.md](CONTRIBUTING.md) | 贡献指南 |
| [CHANGELOG.md](CHANGELOG.md) | 版本变更记录 |
| [samples/](samples/) | 示例 shader 与用法 |

## 技术栈

.NET 10 Native AOT · DXC SM6.0 · LSP 3.17 · Windows x64

## License

[MIT](LICENSE)
