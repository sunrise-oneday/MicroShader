# 变更日志

本扩展遵循 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.1.0/)。
条目按「对使用者可见」的程度写，内部重构不列。

## [0.1.2] - 2026-10-02

### 新增

- 支持 VS Code 工作区信任：在未信任的工作区里不再被静默禁用。
- 新增设置 `microshader.trace`，可把扩展与服务端之间的 LSP 报文写进 `MicroShader (LSP Trace)` 输出通道。
- 新增命令 `MicroShader: 打开日志`、`MicroShader: 复制诊断信息到剪贴板`（含服务端路径、工程根、客户端状态与最近 60 行日志）。
- 状态栏 tooltip 补上「实际使用的服务端」与「解析到的 Unity 工程根」。
- ShaderLab 编辑体验：花括号块的自动缩进与反缩进、`{` `}` 之间的回车展开、块注释自动闭合。
- 对 `shaderlab` 与 `UnityShader` 关闭基于文本词的补全建议，避免纯文本词混进语言服务器候选。

### 变更

- 修改影响服务端启动的设置后，会提示「需要重启语言服务器」并提供一键重启。
- 非 Windows 平台改为启动即报明确原因（此前会反复重试并停在「已停止」）。

## [0.1.1]

### 新增

- 扩展启动时显式把 Unity 工程根通过 `--project` 传给服务端。
  修复「只打开单个 .shader 文件时补全一条都不出」——此前服务端拿不到工程根，会退化成空索引，诊断只剩 `MS0001`。
- 新增三个性能旋钮设置：`microshader.analysisDebounceMs` / `maxCompileThreads` / `syncIncludeFallbackFiles`。
- 激活时把服务端二进制的体积与 SHA-256 写进输出通道，便于比对「扩展里装的 exe 是不是最新的」。

### 修复

- 补齐 `activationEvents`。此前扩展在部分环境下根本不会被激活。

## [0.1.0]

### 新增

- 首个版本：ShaderLab / HLSL 诊断（DXC 原生编译 + ShaderLab 标签语义校验）、成员与内建分量补全、`F12` 定义跳转、`#include` 超链接、文档大纲。
- 服务端二进制与 DXC 随扩展分发，离线可用。
