# 架构与代码索引

> 想知道「某个功能改哪个文件」，直接查 [§3 常见任务索引](#3-常见任务索引)。
> 想知道「某个模块对应哪些代码和用例」，查 [§2 模块速查表](#2-模块速查表)。

本项目是一个**特化的 Unity ShaderLab 诊断语言服务器**：只做「打字那一刻对当前着色器做精确诊断」，
不做全量工程符号索引。服务端是 .NET 10 Native AOT 原生 exe，客户端是 VS Code / Sublime 扩展，
可选接一个 Unity Editor 桥接包以使用编辑器实测的宏状态。

- 代码规模：约 **33.8 K 行** .cs（产品 23.3 K + 自检 10.5 K），另加 Unity 桥接 2.5 K（C#）
- 依赖：**零第三方 NuGet**（`NuGet.config` 清空包源），DXC 走自研 vtable 垫片
- 自检：**215 条**用例 / 26 个一级 filter 前缀，`--list` 可列全

---

## 1. 目录职责

| 路径 | 职责 | 入库 |
| :--- | :--- | :-: |
| `src/` | 10 个 .NET 工程：模块 1–10 的引擎 + 模块 13 自检 + Server 宿主 | ✅ |
| `client/vscode/` | VS Code 扩展（11A）：`src/` 是 TypeScript 源码，`bin/` 由脚本注入交付二进制 | ✅ |
| `client/sublime/` | Sublime Text 适配（11A）：纯声明式，无构建 | ✅ |
| `client/unity/` | Unity Editor 桥接包（11B）：放进工程 `Editor` 目录即生效 | ✅ |
| `tests/Fixtures/` | 自检语料：`.shader` / `.cginc` / `.hlsl` 固定夹具 | ✅ |
| `samples/` | 手工跑 `--analyze` 用的示例 shader：`HelloShader.shader` 是零错误基线，另两个是故意写错的对照组。用法与期望输出见 [`samples/README.md`](samples/README.md) | ✅ |
| `scripts/` | 发布与交付脚本（pwsh / cmd / sh 三套入口 + vsix 打包 + 二进制同步） | ✅ |
| `probes/` | 场外基准台与一次性探针（**大部分不入库**，干净 checkout 不可复现） | 部分 |
| `docs/` | 模块契约、缺陷记录、实测报告、规划 | ✅ |
| `third_party/dxc/` | 官方 DXC release 资产 + `dxc-lock.json` 哈希锁 | ✅ |
| `tools/` | 一次性辅助脚本，**不参与构建** | ✅ |
| `dist/` | 构建产物（发布目录、vsix） | ❌ |
| `DetailDesign/` | 15 份模块详细设计 | ❌ 见 README |
| `工具整体设计文档.md` | 主设计文档（88 KB） | ❌ 见 README |
| `.local/` | 本机物料（不还原、不入库） | ❌ |

---

## 2. 模块速查表

「自检」列是 `--filter <关键字>` 的关键字，可直接跑。

| 模块 | 工程 | 关键文件 | 自检 |
| :-- | :--- | :--- | :--- |
| **1** ShaderLab 切片 | `MicroShader.ShaderLab` | `ShaderLabStateMachine.cs`（状态机主入口）、`ShaderLineScanner.cs`、`ShaderTagValidator.cs`、`ShaderTagSchemaBuilder.cs` | `ShaderLab.Fixtures` `ShaderLab.RealWorld` `ShaderLab.MacroMatrix` `Tag` `Platform` |
| **2** VFS 与包路径 | `MicroShader.ContextEngine` | `VfsIndexBuilder.cs`、`VfsPath.cs`、`IncludeResolver.cs`（include 四规则）、`VfsWatcher.cs` | `Vfs.*` `VfsReal.*` |
| **3** 装配与锚定 | `MicroShader.DiagnosticEngine` | `VirtualTextAssembler.cs`、`Segment.cs`、`RenderedLineMap.cs`、`LineRewrite.cs` | `Asm.*` `AsmReal.*` |
| **4** DXC 网关 | `MicroShader.DiagnosticEngine` | `NativeCompilerGateway.cs`、`Native/DxcApi.cs`、`Native/DxcNativeLibrary.cs`、`DxcDiagnosticParser.cs` | `Dxc.*` |
| **5** 归属与降噪 | `MicroShader.DiagnosticEngine` | `DiagnosticAttributor.cs`、`RenderedTextWriter.cs`、`IncludeAudit.cs` | `Attribution` |
| **6** 诊断缓存 | `MicroShader.DiagnosticEngine` | `ShaderDiagnosticEngine.cs`（分析入口、缓存） | `Compile.*` |
| **7** 可观测性 | `MicroShader.ObservabilityEngine` | `LogRingBuffer.cs`（无锁环形日志）、`CrashFlightRecorder.cs`、`DiagTrace.cs`、`StdioAirGapGuard.cs` | `Log` `Dump` `Sink` `AirGap` |
| **8** 传输与调度 | `MicroShader.CoordinationEngine` | `Session/LspServerSession.cs`（报文分发）、`Scheduling/CoordinationScheduler.cs`（防抖/版本栅栏）、`Documents/DocumentShadowStore.cs`、`Documents/LineDiff.cs`、`Transport/StdioFramingChannel.cs` | `Lsp.*` `Doc.*` `Sched.*` `Diag.*` |
| **9** 智能补全 | `MicroShader.IntelliSenseEngine` | `IntelliSenseService.cs`、`HlslDocumentIndex.cs`、`IncludeSymbolWarmer.cs`、`IncludeSymbolCache.cs`、`TriggerContext.cs` | `IntelliSense` `IncludeSymbol` `BuiltinUniform` |
| **10** 导航 | `MicroShader.NavigationEngine` | `NavigationService.cs`、`DefinitionTracer.cs`、`SymbolOutlineBuilder.cs`、`DocumentLinkResolver.cs` | `Navigation` `NavigationLsp` |
| **11A** 客户端 | `client/vscode` `client/sublime` | `src/extension.ts`、`src/projectRoot.ts`、`src/probe.ts` | — |
| **11B** Unity 桥接 | `client/unity` + `ContextEngine` | `UnityBridgeEndpoint.cs`（发现文件）、`UnityBridgeClient.cs`、`client/unity/MicroShaderBridge/BridgeServer.cs` | `Bridge.*` |
| **12** AOT 交付 | `MicroShader.Server` + `scripts/` | `Program.cs`（宿主/参数）、`UnityProjectLocator.cs`、`scripts/publish-aot.ps1`（三闸门） | `Server.*` |
| **13** 自检台 | `MicroShader.SelfTest` | `Program.cs`（注册入口）、`TestCase.cs`、各 `*Tests.cs`、语料 `tests/Fixtures/` | 全部 215 条 |

### 自检套件分布（26 个一级前缀）

```text
ShaderLab 27   Bridge 22   Vfs 19   Lsp 16     Tag 14    Navigation 14
Asm 12        IntelliSense 11   Log 8     Attribution 7
Dxc 6         Compile 6    VfsReal 5  NavigationLsp 6  LspCorpus 5
IncludeSymbol 5   BuiltinUniform 5   Doc 4    AsmReal 4
Server 3      Sched 3      Platform 3   Dump 3
Sink 2        Diag 2       AirGap 2
```

---

## 3. 常见任务索引

| 我想改… | 去这里 |
| :--- | :--- |
| 加一个 ShaderLab 语法/标签诊断码 | `ShaderLab/ShaderLabDiagnosticCodes.cs`（SLxxxx）+ `DiagnosticEngine/DiagnosticEngineCodes.cs`（MS00xx） |
| 改补全候选从哪来 | `IntelliSenseService.cs`（成员与前缀）、`IncludeSymbolProvider.cs`（include 链类型）、`HlslBuiltinUniforms.cs`（引擎内置 uniform）、`HlslSwizzles.cs`（向量分量） |
| 改 F12 / 大纲 / #include 超链接 | `NavigationEngine/NavigationService.cs` → `DefinitionTracer.cs` / `SymbolOutlineBuilder.cs` / `DocumentLinkResolver.cs` |
| 改诊断触发时机（防抖、并发、版本栅栏） | `CoordinationEngine/Scheduling/CoordinationScheduler.cs` + `Session/LspServerOptions.cs` |
| 改 `didChange` 行为（影子文档、行级 diff、跳过分析） | `Session/LspServerSession.cs` + `Documents/DocumentShadowStore.cs` / `Documents/LineDiff.cs` |
| 改 stdio 分帧 / JSON-RPC 解析 | `Transport/StdioFramingChannel.cs`、`Lsp/LspMessageReader.cs`、`Lsp/Utf8Raw.cs`、`Lsp/LspParamsReader.cs` |
| 改 DXC 调用方式或 vtable 垫片 | `DiagnosticEngine/NativeCompilerGateway.cs` + `Native/DxcApi.cs` |
| 改 `#include` 路径解析规则 | `ContextEngine/IncludeResolver.cs` + `VfsPath.cs` + `VfsIndexBuilder.cs` |
| 改外部头文件报错的行号回映射 | `DiagnosticEngine/DiagnosticAttributor.cs` + `RenderedTextWriter.cs` + `RenderedLineMap.cs` |
| 改平台宏来源（Editor.log / ProjectSettings） | `PlatformMacroSet.cs`、`ProjectSettingsMacroDeriver.cs`、`EditorLogPlatformProbe.cs` |
| 改日志、崩溃现场、LSP trace | `ObservabilityEngine/`：`LogRingBuffer.cs` `CrashFlightRecorder.cs` `DiagTrace.cs` |
| 改 Unity 工程根的定位方式 | `Server/UnityProjectLocator.cs` + `ContextEngine/UnityProjectLayout.cs` + `client/vscode/src/projectRoot.ts` |
| 改扩展启动参数 / 设置项 | `client/vscode/src/extension.ts` + `client/vscode/package.json` |
| 改桥接发现文件或广播协议 | `ContextEngine/UnityBridgeEndpoint.cs`（读端）+ `client/unity/.../BridgeServer.cs`（写端），schema 见 `client/unity/endpoint.schema.json` |
| 改发布闸门 / 交付形态 | `scripts/publish-aot.ps1`（+ `.cmd` / `.sh` 三套必须同步） |
| 改扩展打包内容 | `scripts/package-vsix.ps1` + `client/vscode/.vscodeignore` |
| 加自检用例 | `MicroShader.SelfTest/*Tests.cs`（用 `TestSuite.Add` 登记），语料放 `tests/Fixtures/` |

---

## 4. 一次诊断的数据流

```text
didChange
  → Documents/DocumentShadowStore        影子文档存快照
  → Documents/LineDiff                   行级 diff 出「首个改动行」= 探针行
  → Documents/LineDiff.OnlyTriviaChanged 只改了空白/注释则直接跳过
  → Scheduling/CoordinationScheduler     防抖 300 ms + 版本栅栏 + 线程池上限
  → Server/ShaderDocumentAnalyzer        组织分析单元
  → ShaderLab                            切片（Pass / CGPROGRAM / HLSLINCLUDE）
  → ContextEngine                        虚拟路径重定向（`Packages/…` → 包根）
  → VirtualTextAssembler                 宏注入 + `#line` 锚定 → 渲染文本
  → NativeCompilerGateway                DXC 双阶段派发
  → DiagnosticAttributor                 外部头文件报错回映射到本文件行号
  → CoordinationScheduler                按 Pass 合并
  → Diagnostics/PublishDiagnosticsStore  → publishDiagnostics
```

---

## 5. 不可违反的约束

改动这个项目时，下面几条一旦破坏就是**发布级事故**，不是能靠测试兜住的：

1. **stdout 只允许出现 LSP 帧**。任何日志/诊断输出走 stderr，由 `StdioAirGapGuard` 强制。
2. **零第三方 NuGet**。需要新能力时自研（参考 `Native/DxcApi.cs` 的 vtable 垫片做法）。
3. **Native AOT 兼容**：不用反射、不用动态代码生成、不用 `System.Text.Json` 反射路径。
   IL2026/IL2050/IL3050/IL3051/IL3052 是 Warning 不是 Error，**发布阶段靠 `publish-aot.ps1` 的闸门兜底**。
4. **交付形态是「同目录成对」**：`UnityShaderLsp.exe` + `dxcompiler.dll` + `dxil.dll` 必须同目录，
   绝不从系统目录搜版本（见详设 v2.0 第 2/5 条）。
5. **改 `publish-aot.ps1` 必须同步改 `.cmd` 与 `.sh`**，三套入口是等价的。
6. **自检基线口径**：`215 / 215 / 0 / 0`（Release，且**必须带** `MICROSHADER_UNITY_PROJECT` 与
   `--urp`）。裸跑是 `215 / 213 / 1 / 1` 且退出码 1（`Server.AotSmoke` 失败、`Tag.GoldenUrp` 跳过，
   两者都是环境依赖）。详见 `docs/module-contracts.md`。

---

## 6. 文档地图

| 想找什么 | 去哪 |
| :--- | :--- |
| 某个模块的完整契约 + 实测数字 | `docs/module-contracts.md`（按模块分节，含自检基线块） |
| 装不上 / 用不了 / 补全不出 | `client/vscode/README.md`（按症状组织的排障） |
| Unity 桥接怎么装、失败怎么查 | `client/unity/README.md` + `endpoint.schema.json` |
| 已修复的缺陷根因与复现 | `docs/bugs/` |
| 尚未收尾的桥接问题 | `docs/bugs/UnityBridge-TCP-connected-no-frame.md` |
| 性能基线、压测、瓶颈定位 | `docs/roadmap/`（含 `README.md` 索引） |
| 自检纪律（负面结论必须先过装置自检） | `docs/testing-guidelines.md` |
| 项目的对外说明 | `README.md`、`DESIGN.md` |
| 提交规范与开发流程 | `CONTRIBUTING.md` |
| 版本变更记录 | `CHANGELOG.md` |
