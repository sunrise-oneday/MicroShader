# 变更日志

本文件记录**服务端与整个项目**的变更；VS Code 扩展的变更单独见
[`client/vscode/CHANGELOG.md`](client/vscode/CHANGELOG.md)。

格式遵循 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.1.0/)。本项目尚未发布独立版本号，
服务端随扩展一起分发，因此以扩展版本为节。

## [未发布]

### 已知限制

- `DetailDesign/`（15 份模块详细设计）与 `工具整体设计文档.md`（88 KB 主设计）**不随仓库分发**，
  也不在版本控制内。干净 checkout 只有代码。
- `probes/` 基准台大部分不入库，干净 checkout 无法复现性能数字（`11b-bridge-smoke.mjs` 除外）。

## [0.1.2]

### 修复

- `didChange` 只改动空白或注释时不再触发分析（新增 `LineDiff.OnlyTriviaChanged`），
  省掉一次完整的 Parse + DXC 往返。
- 补全支持嵌套成员链（`v.posOS.` 一类多层点号访问），此前只支持一层 `.成员`。
- `/config` 的设置键会弹出「需要重启语言服务器」并提供一键重启，此前只写在设置项描述里。

### 新增

- 性能旋钮：`analysisDebounceMs`、`maxCompileThreads`、`syncIncludeFallbackFiles`。
- 发布脚本健壮性：三套入口（`ps1`/`cmd`/`sh`）闸门一致化。
- `scripts/package-vsix.ps1`：一条命令产出可安装 vsix（此前只有手工复制）。
- `scripts/sync-extension-bin.ps1`：按 SHA-256 同步扩展内的服务端二进制（幂等）。
- `probes/11b-bridge-smoke.mjs` 纳入版本控制（此前被 `.gitignore` 挡住，无法复验桥接）。

### 变更

- `MicroShader.Server.csproj` 的 `IL2026/IL2050/IL3050/IL3051/IL3052` 由 `WarningsAsErrors`
  降回 `Warnings`；AOT 兼容性改由发布阶段闸门验证。
- 闸门 1 由「删除残留 PDB」改为「归档到 `bin/symbols/`」，保留崩溃行号映射。

## [0.1.1]

### 修复

- **服务端崩溃根因**：`$/cancelRequest` 携带数字 id 时触发 FailFast（退出码 `0xC0000409`）。
  根因是 `Utf8Raw.Unquote` 对非字符串类型调用 `GetString()` 抛 `InvalidOperationException`，
  异常冒泡出读循环。补齐全类型安全 + 两层兜底 + 3 条回归用例。
- 扩展激活事件补齐，此前部分环境下扩展根本不会被激活。

### 新增

- 扩展显式传 `--project <Unity 工程根>`。此前只打开单个 `.shader` 时服务端拿不到工程根，
  会退化成空索引，表现为「补全一条都不出、诊断只剩 MS0001」。
- 三个性能旋钮设置项。
- 激活时把服务端二进制的体积与 SHA-256 写进输出通道，便于比对装的 exe 是不是最新的。

## [0.1.0]

首个可用版本：ShaderLab / HLSL 诊断（DXC 原生编译 + 标签语义校验）、成员与内建分量补全、
`F12` 定义穿透、`#include` 超链接、文档大纲。模块 1–10、12、13 与客户端 11A 已完成。
