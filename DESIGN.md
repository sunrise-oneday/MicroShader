# 项目说明书 · MicroShader (UnityShaderLSP)

> 面向低配设备（16GB 物理内存）的微型、零延迟 Unity URP ShaderLab / HLSL 诊断语言服务器。
> 技术基座：.NET 10 Native AOT + DirectXShaderCompiler (SM6.0)，**零第三方 NuGet 依赖**。

| 项目属性 | 内容 |
| :--- | :--- |
| 代号 | MicroShader / UnityShaderLSP |
| 形态 | LSP 语言服务器（独立进程）+ 双端编辑器客户端 |
| 目标管线 | Unity 2022.3 LTS + URP (Universal Render Pipeline) |
| 目标宿主 | Windows 10/11 (x64)，16GB 物理内存环境 |
| 技术基座 | .NET 10 Native AOT + DXC SM6.0（进程内 P/Invoke） |
| 协议 | LSP 3.17 / JSON-RPC 2.0 over Stdio |
| 依赖策略 | 零第三方 NuGet（NuGet.config 清空包源，构建可完全离线） |
| 工程构成 | 10 个 .NET 工程 + 一组 Unity 侧桥接模块（`client/unity/MicroShaderBridge/`） |

---

## 1. 项目是什么

MicroShader 是一个**特化的 Unity Shader 诊断语言服务器**。它不做全工程符号索引，只在用户**打字/保存的那一刻**，对当前正在编辑的 `.shader` 文件执行「切片 → 装配 → DXC 编译 → 诊断归属」流水线（切片：取出正在编辑的那个 Pass 的代码；装配：拼成一份可直接编译的完整源码；诊断归属：把报错坐标换算回原文件的行列），把精准的红线实时推回编辑器。

它的核心价值主张：

- **消灭「Alt+Tab 切回 Unity 看 Console 报错」的割裂体验** —— 写着色器时即时得到语法/语义诊断；
- **无缝解析 URP 虚拟包路径** —— 直接理解 `#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"`；
- **在 16GB 内存的机器上与 Unity、IDE 共存而不引发换页抖动** —— 常驻内存压到几十 MB 量级。

---

## 2. 背景：要解决的问题

在典型 Unity 商业项目中，16GB 内存是一道「生死线」：

| 进程 | 典型常驻工作集 |
| :--- | :--- |
| Unity Editor 2022.3（中大型 URP 工程） | 4 GB ~ 6 GB |
| JetBrains Rider（JVM 前端 + .NET 后端 + 全工程符号索引） | 3.5 GB ~ 5 GB |
| Windows 系统与后台服务 | ~3.5 GB |
| 浏览器 / 通讯工具 | ~2.5 GB |

合计逼近 15.5 ~ 16 GB，系统被迫频繁换页，导致打字掉帧、窗口切换冻结。

**架构抉择**：放弃「全量工程符号索引」的重型 IDE 路线，转而做一个**只在打字那一刻对当前着色器做精确诊断、常驻内存仅几十兆的特化 LSP**。这是本项目全部设计取舍的出发点。

---

## 3. 系统目标与非目标

### 3.1 目标（In-Scope）

- **核心场景**：Unity 2022.3 LTS + URP 单文件老派 `.shader` 的即时语法报错。
- **极速反馈**：打字停顿触发**聚焦 Pass**（正在编辑的那个 Pass）诊断 —— 连续按键会被合并成一次分析（**防抖**：默认停顿 **300 ms** 后触发，可在 200–500 ms 区间调整）；`Ctrl+S` 触发**全文件所有 Pass** 的全量诊断。
- **路径穿透**：解析 URP 虚拟包路径与 Unity 内置包根目录（虚拟路径到磁盘真实位置的映射由 VFS 完成）。
- **双端承载**：同一套二进制无需修改即可被 VS Code 扩展与 Sublime Text 4 的 LSP 插件驱动。

### 3.2 非目标（Non-Goals）

- **不解析 C# 业务代码** —— 完全交给官方 C# Dev Kit / OmniSharp。
- **不做全变体爆炸组合计算** —— MVP 仅检查「主干激活变体」，不穷举上百种变体分支。
- **不自研 GUI** —— 不碰字体渲染、文本选择、光标动画等编辑器 UI 细节。

---

## 4. 技术选型与关键约束

### 4.1 运行时：.NET 10 Native AOT

| 维度 | 传统 .NET JIT | Rust (rust-analyzer 模式) | **本项目（Native AOT）** |
| :--- | :--- | :--- | :--- |
| 冷启动 | 350~600 ms | < 10 ms | 端到端 ≤ 30 ms（进程创建下限实测约 12 ms） |
| 常驻内存 | 70~150 MB | 12~25 MB | Private Working Set ≤ 30 MB |
| FFI | 稳定但有 GC 停顿 | 零开销但心智门槛高 | C-ABI 零开销封送 |
| 维护门槛 | C# 生态成熟 | 对 TA 门槛极高 | TA 自主可控 |

### 4.2 编译器：进程内 P/Invoke 而非外部 CLI

- 外部 `dxc.exe` 每次 `CreateProcess` 冷启动 35~80 ms，打字时频繁起停会拉高 CPU；
- 进程内 `dxcompiler.dll` 直接在当前地址空间编译源文本，实测**比反复起停子进程快 17~20 倍**（热路径为毫秒级）。

### 4.3 关于第三方库的重要说明

本项目**不引入任何第三方 NuGet 包**。选型阶段评估过 R3 / ZString / Vortice.Dxc / MemoryPack 等库，逐条实测后全部放弃：

- **DXC 绑定库**：Native AOT 不支持内置 COM 互操作，第三方绑定在本机实测中直接抛不支持异常，官方兼容性声明不可采信 —— 改为**自研最小 COM 接口层**直接调用 `dxcompiler.dll`（只覆盖用到的接口）。
- **调度库 R3**：会把调度控制权交给第三方 —— 调度改为自研的**防抖 + 版本栅栏**（给每次分析编号，只发布最新一次的结果，过期结果直接丢弃）。
- **日志格式化库 ZString**：日志写入是最热路径，不引入第三方 —— 直接使用 .NET 标准库自带的零分配格式化接口。

代价与收益：放弃「开箱即用的高性能库」，换来**完全可控的 AOT 裁剪面、可预测的体积、可离线复现的构建**。任何新引入的第三方库都必须先在本机完成 AOT 三步实测验收（ADR-030），不采信任何兼容性矩阵表。

---

## 5. 系统架构

### 5.1 拓扑

```text
┌──────────────────────────────────────────────────────────────┐
│                    前端编辑器 (Editor Shell)                  │
│        VS Code (极简 .vsix)   /   Sublime Text 4 (LSP 插件)   │
└───────────────────────────────▲──────────────────────────────┘
                                │ JSON-RPC (LSP 3.17 via Stdio)
┌───────────────────────────────▼──────────────────────────────┐
│                  UnityShaderLsp.exe (.NET 10 AOT)             │
│                                                              │
│  [模块 8] CoordinationEngine ─ Stdio 分帧 / JSON-RPC / 防抖调度 │
│  [模块 1] ShaderLabStateMachine ─ 行扫描切片 / 变体首项提取     │
│  [模块 2] ContextEngine ─ VFS 包拓扑索引 / include 四规则解析   │
│  [模块 3] VirtualTextAssembler ─ 段模型装配 / #line 锚定 / 宏注入│
│  [模块 4] NativeCompilerGateway ─ 自研 COM 接口层调 DXC        │
│  [模块 5] DiagnosticAttributor ─ 降噪 / include 错误重映射      │
│  [模块 6] CoordinationScheduler ─ 版本栅栏 / 诊断缓存           │
│  [模块 7] ObservabilityEngine ─ 崩溃现场日志 / 日志与协议通道隔离│
│  [模块 9] IntelliSenseEngine ─ 成员补全 / include 链符号预热     │
│  [模块 10] NavigationEngine ─ F12 跳转 / 大纲 / 面包屑          │
└───────────────────────────────┬──────────────────────────────┘
                                │ 内存直接调用 (In-Process)
┌───────────────────────────────▼──────────────────────────────┐
│              原生编译器运行时 (Co-located DLLs)               │
│       dxcompiler.dll (编译器)  +  dxil.dll (可选校验器)        │
└───────────────────────────────┬──────────────────────────────┘
                                │ 物理 I/O
┌───────────────────────────────▼──────────────────────────────┐
│                    Unity 工程文件系统 (磁盘)                  │
│   Packages/ · Library/PackageCache/ · BuiltInPackages/ ...   │
└──────────────────────────────────────────────────────────────┘
```

### 5.2 一次诊断的数据流

```text
用户按键
  → didChange（全量文本 SyncKind.Full）
  → 服务端行级 Diff 定位聚焦 Pass
  → 防抖 300 ms / 版本栅栏
  → 模块 1 切片：HLSLPROGRAM…ENDHLSL，提取 #pragma 变体首项、入口点
  → 模块 2 VFS：Packages/ 虚拟前缀 → 物理绝对路径（5 类包来源 + 4 条 include 规则）
  → 模块 3 装配：注入平台宏 / 变体宏 / _KEYWORD_DECLARED，用 #line 锚把编译位置标回原文件行号
  → 模块 4 双阶段派发：vs_6_0（若声明 vertex）+ ps_6_0（若声明 fragment），各带独立 SHADER_STAGE_*
  → DXC 返回诊断文本
  → 模块 5 归属映射：本文件直通 / 外部 include 重映射到 #include 行 + RelatedInformation
  → 模块 6 按 Pass 合并诊断集 → 按版本校验 → 整份推送诊断（publishDiagnostics 为整份替换）
```

---

## 6. 模块清单

| 模块 | 名称 | 承载工程 | 职责 |
| :---: | :--- | :--- | :--- |
| **1** | 行扫描切片器与变体推导 | `MicroShader.ShaderLab` | 纯 `ReadOnlySpan<char>` 单向状态机；结构切片、`HLSLINCLUDE` 共享块、`#pragma` 变体首项、遗留 CG 抑制 |
| **2** | VFS 路径重定向引擎 | `MicroShader.ContextEngine` | 5 类包来源 + 4 条 include 规则 + 无锁快照注册表 + 文件监视 |
| **3** | 零分配预处理与 #line 坐标锚定 | `MicroShader.DiagnosticEngine` | Segment 装配模型、平台/变体宏注入、include 自展开、`#include_with_pragmas` 降级 |
| **4** | 原生 DXC 双阶段派发器 | `MicroShader.DiagnosticEngine/Native` | 自研 COM 接口层调用 `dxcompiler.dll`；VS/PS 双阶段；每线程实例池 |
| **5** | 降噪过滤器与 Include 错误重映射 | `MicroShader.DiagnosticEngine` (`DiagnosticAttributor`) | pragma-message 过滤、直接/深层 include 重映射、去重与降级计数 |
| **6** | 响应式调度与版本仲裁 | `MicroShader.CoordinationEngine` | 防抖、版本栅栏、诊断缓存（同装配文本跳过重编译） |
| **7** | 环形无锁内存日志与自诊断中枢 | `MicroShader.ObservabilityEngine` | 固定容量环形日志（崩溃/卡死现场可读）、日志与协议通道隔离、崩溃转储、通道限流 |
| **8** | LSP 传输协议接入与调度中枢 | `MicroShader.CoordinationEngine` | Stdio 分帧、JSON-RPC 分发、文本影子快照、单写者出站、防抖调度 |
| **9** | 轻量符号索引与即时补全 | `MicroShader.IntelliSenseEngine` | 结构体成员 + 内建向量分量 + 文档内符号前缀 + include 链预热 + 引擎内置 uniform |
| **10** | 跨文件定义跳转与符号导航 | `MicroShader.NavigationEngine` | F12 定义穿透、include 超链接两段式 resolve、大纲/面包屑、折叠范围（粘滞滚动窗口） |
| **11** | 双端客户端适配与 Unity 桥接宿主 | `client/` | 11A：VS Code 扩展 + Sublime 声明式适配；11B：Unity 桥接脚本（`client/unity/`）+ 服务端外连客户端 |
| **12** | AOT 裁剪、依赖装配与交付流水线 | `MicroShader.Server` | LSP 宿主 + AOT 入口 + DXC/工程定位 + 标签判定表采集 |
| **13** | 无头沙盒回归测试与性能基准套件 | `MicroShader.SelfTest` | 零依赖自检 CLI（`--selftest`），按退出码挂 CI |
| — | 领域模型（跨模块契约） | `MicroShader.Domain` | `SourceShaderDocument` / `ShaderPassSnippet` / `CompilationPayload` / `ShaderDiagnosticItem` 等 |

---

## 7. 源码结构

```text
MicroShader.sln
Directory.Build.props         # 统一 TFM / Nullable / AOT 兼容性 / 分析器开关
NuGet.config                  # 包源清空 —— 零第三方依赖，构建可离线
global.json                   # .NET SDK 10.0.401

src/
  MicroShader.Domain/             # 领域模型（跨模块契约）
  MicroShader.ShaderLab/          # 模块 1：行扫描状态机 + 关键字宏矩阵生成器
  MicroShader.ContextEngine/      # 模块 2：VFS 拓扑索引 + include 四规则 + 无锁快照注册表
  MicroShader.DiagnosticEngine/   # 模块 3+4：Segment 装配器 + #line 锚定 + DXC 网关（Native/）+ 归属映射
  MicroShader.ObservabilityEngine/# 模块 7：环形内存日志（崩溃现场可读）+ 日志通道隔离 + 崩溃转储（零工程引用）
  MicroShader.CoordinationEngine/ # 模块 8：Stdio 分帧 + JSON-RPC 分发 + 影子快照 + 防抖调度 + 单写者出站
  MicroShader.IntelliSenseEngine/ # 模块 9：容错声明扫描 + 成员/前缀补全 + include 链预热 + 引擎内置 uniform
  MicroShader.NavigationEngine/   # 模块 10：F12 定义跳转 + include 超链接 + 大纲/面包屑
  MicroShader.Server/             # 模块 12：LSP 宿主 + AOT 入口 + DXC/工程定位
  MicroShader.SelfTest/           # 模块 13：零依赖自检 CLI（--selftest）

tests/Fixtures/               # 固定着色器用例语料（.shader / .cginc / .hlsl 等）
scripts/                      # 发布流水线（publish-aot.ps1 / .cmd / .sh）
third_party/dxc/v1.9.2607/    # DXC 官方资产锁定（二进制不入库，仅留哈希清单与许可证）
probes/                       # 一次性技术探针（大多不入库；dxc-aot / rig-bench 等）
client/
  vscode/                     # 模块 11A：VS Code 扩展（.vsix，内置服务端与 DXC）
  sublime/                    # 模块 11A：Sublime Text 4 声明式适配
  unity/                      # 模块 11B：MicroShaderBridge/（桥接源码）+ endpoint.schema.json + README
docs/                         # 开发规范、性能优化交接、缺陷记录、待开发方案
DetailDesign/                 # 逐模块详细设计 + Unity 真机实测报告 + 验证与修正记录（本地资料，不随仓库分发）
```

### 7.1 关键文件索引（源码热点）

| 文件 | 说明 |
| :--- | :--- |
| `MicroShader.IntelliSenseEngine/HlslDocumentIndex.cs` | HLSL 文档符号索引 |
| `MicroShader.CoordinationEngine/Session/LspServerSession.cs` | LSP 会话主循环 |
| `MicroShader.ShaderLab/ShaderLabStateMachine.cs` | 模块 1 状态机 |
| `MicroShader.DiagnosticEngine/VirtualTextAssembler.cs` | 模块 3 装配器 |
| `MicroShader.NavigationEngine/DefinitionTracer.cs` | 模块 10 定义穿透 |
| `MicroShader.Server/Program.cs` | AOT 入口 |
| `MicroShader.DiagnosticEngine/Native/DxcIncludeHandlerBridge.cs` | 自研 COM 接口层（DXC include 解析回调） |
| `MicroShader.CoordinationEngine/Scheduling/CoordinationScheduler.cs` | 模块 6/8 调度与版本栅栏 |
| `client/unity/MicroShaderBridge/ShaderLspBridge.cs` | 模块 11B：Unity 侧组合根（生命周期接线 + 采样/发布编排） |
| `client/unity/MicroShaderBridge/BridgeServer.cs` | 模块 11B：TCP 监听与广播（不引用主线程限定 API） |
| `MicroShader.ContextEngine/UnityBridgeClient.cs` | 模块 11B：服务端侧外连客户端（读 endpoint.json + 消费载荷） |

---

## 8. 关键设计决策（ADR 摘要）

完整 ADR-001 ~ ADR-030 见《工具整体设计文档.md》（维护者本地保管，不随仓库分发）。以下为对理解系统最重要的若干条：

| ADR | 决策 | 一句话理由 |
| :--- | :--- | :--- |
| **001** | 选 .NET 10 Native AOT | 低内存 + 原生冷启动 + TA 熟悉的 C# 生态 |
| **003** | 打字聚焦当前 Pass，保存全量扫描 | 双轨流水线兼顾即时性与兜底完整性 |
| **005** | 注入 HLSL 原生 `#line` 指令对齐行号 | 根除手写行号换算的 off-by-one |
| **009** | `#include_with_pragmas` 降级为普通 `#include` | DXC 视其为非法指令；不处理则真实 URP 全线误报 |
| **010** | `HLSLINCLUDE` 拼入本文件**所有**同类 program block | 官方语义是「anywhere in this source file」 |
| **014** | 防抖窗口 200–500 ms（默认 300 ms） | 停顿太久就失去了即时反馈的意义 |
| **018** | 关键字宏注入矩阵（整组语义） | 选中项 `#define K 1`；同组未启用项**不定义**；补 `_KEYWORD_DECLARED` |
| **019** | 自研最小 COM 接口层 + 每线程 DXC 实例池 | NativeAOT 无内置 COM，第三方绑定不可靠 |
| **023** | `#line` 锚定的分段装配模型 + 行列号单位约定 | 从结构上杜绝整类行列偏移 |
| **024** | 诊断按 Pass 合并后整份推送，每次打开独立版本序列 | 诊断推送是整份替换，且重开文档后版本号会回退 |
| **025** | 分帧失败即不可恢复，禁止逐字节重同步 | payload 内可能字面包含 `Content-Length:` |
| **026** | stdout 协议专线；三道防线防污染 | 任何非帧字节都会破坏客户端 framing |
| **028** | 诊断判定一律用 `IDxcResult::GetStatus()` | `rc==0` 不代表没有诊断（`#warning` 为 0） |
| **030** | 第三方库 AOT 可用性须过「三步验收」 | 不采信任何矩阵表承诺，只信本机实测 |

---

## 9. 客户端

### 9.1 VS Code 扩展（模块 11A）

- 扩展 ID：`microshader.unity-shader-lsp`（publisher `microshader`）；
- 注册语言 `shaderlab`，扩展名 `.shader` / `.cginc`，附带 TextMate 语法；
- 内置服务端二进制与 DXC（`client/vscode/bin/`：`UnityShaderLsp.exe` + `dxcompiler.dll` + `dxil.dll`）；
- 关键配置项：`microshader.serverPath`（自定义 exe 路径）、`microshader.attachForeignLanguages`（是否接管 Unity 官方扩展的语言）、`microshader.analysisDebounceMs`（防抖）；
- 交付物：`dist/` 下的 `.vsix` 安装包（由 `scripts/package-vsix.ps1` 产出，文件名带版本号）；
- 防孤儿进程：依赖 `vscode-languageclient` 内建的崩溃自动重启（默认最多 4 次），服务端在 stdin 关闭时自行退出。

### 9.2 Sublime Text 4（模块 11A）

- 声明式适配位于 `client/sublime/`：`LSP-MicroShader.sublime-settings` + `shaderlab.sublime-syntax`；
- 复用同一套 LSP 二进制，实现「LSP (~25MB) + 前端 (~60MB) < 100MB」的超轻量工作站。

### 9.3 Unity 桥接宿主（模块 11B）

- **交付物**：`client/unity/MicroShaderBridge/`（按职责分层的桥接脚本：协议常量 / 日志 / 设置 / 主线程泵 / 环境采集 / 采样器 / 编码器 / 发现文件 IO / TCP 服务端 / 单连接 / 组合根 / 菜单）+ `client/unity/endpoint.schema.json` + `client/unity/README.md`；
- **拓扑**：Unity 作为服务端 —— 桥接脚本在编辑器加载时绑定 `127.0.0.1` 的**动态端口**，把实际端口写进 `<Project>/Library/MicroShader/endpoint.json`（原子替换；放在 `Library/` 而不是 `Temp/`，后者关编辑器即清空）；
- **服务端侧**：`MicroShader.ContextEngine` 中的桥接客户端按该文件发现端口（并用 pid / 启动时间判断是否仍然有效），后台线程外连、一行一条 JSON 分帧，只有真实变化才推送；`Program.cs` 默认启用（`--unity-bridge 0` / `MICROSHADER_UNITY_BRIDGE=0` 可关），未绑定时完全静默、对诊断链路零影响；
- **提升诊断精度的两条路径**：① 注入段把「平台宏推定项」换成编辑器实测项（`SHADER_API_D3D11` 等），并如实表达 `UNITY_COLORSPACE_GAMMA` 的未定义状态；② `#pragma multi_compile/shader_feature` 的**变体选择**跟随 Unity 上报的启用项，不再由服务端按首项推断（整组语义不变：每组仍只启用一个关键字）；
- **域重载**（Unity 重新加载脚本域）：重载前主动释放端口（关停监听并等线程结束），发现文件保留并标记 `"reloading": true`；重载后复用端口重绑；只在编辑器退出时删除发现文件；
- **真机验证**：在 Unity 2022.3 + URP 工程上实测通过 —— 编译零错，发现文件、广播载荷、重复接入、域重载重绑、退出清理均正常；自检中有真 TCP 回环与离线零漂移门禁用例，端到端冒烟（AOT exe + 真编辑器）回环成立。

---

## 10. 构建、运行与交付

### 10.1 构建与自检

```powershell
dotnet build MicroShader.sln -c Release

# 运行全部自检（固定用例 + 真实 URP 语料）
dotnet src\MicroShader.SelfTest\bin\Release\net10.0\MicroShader.SelfTest.dll --selftest

# 手动分析单个真实的 .shader（切片 → 装配 → DXC → 归属映射）
dotnet src\MicroShader.SelfTest\bin\Release\net10.0\MicroShader.SelfTest.dll --analyze samples\BrokenShader.shader
```

常用开关：`--filter <关键字>`、`--urp <目录>`、`--list`、`--verbose`、`--analyze <文件>`、`--project <目录>`、`--line <n>`、`--type`、`--emit`。

环境变量：`MICROSHADER_UNITY_PROJECT`（真实语料工程根）、`MICROSHADER_URP_ROOT`（URP 包目录）、`MICROSHADER_DXC_DIR`（`dxcompiler.dll` 目录）。

退出码：`0` = 全部通过；`1` = 存在失败用例（可直接挂 CI）。

> 便携环境提示：MSBuild 的 `Csc` 任务需要捕获编译器子进程输出管道；若报 `MSB3883 ... 拒绝访问`，需放宽文件沙箱或改用普通终端执行。

### 10.2 发布流水线（模块 12）

```powershell
# 交付流水线：清理 → Native AOT 发布 → 三闸门
#   闸门①：PDB 归档到 bin/symbols/（不删除，保留崩溃行号映射）
#   闸门②：原生依赖须与服务端同目录
#   闸门③：真加载 DXC
scripts\publish-aot.ps1        # 亦有 .cmd / .sh 版本
```

- 产物是一个原生 exe（不含同目录的原生 DLL）；体积不做固定预期 —— 它取决于实际触达的 .NET 标准库子系统：WebSocket、网络监听、异步管道、文件监视等能力一旦启用，exe 会明显变大；
- 发布前先清空暂存区（增量发布会留下旧 DLL / 旧 PDB）；
- 发布产出的 PDB 一律归档到 `bin/symbols/`，不删除；三套入口（`.ps1` / `.cmd` / `.sh`）语义一致。

---

## 11. 测试与自检（模块 13）

自检 CLI 采用独立的零依赖可执行项目（xunit/NUnit 这类测试框架在 AOT 下不可用），以 `--selftest` 方式运行。

- **用例分档**：行为 / 真机 / 回环 / 桥接契约 / AOT；当前是两百条量级，准确总数与分档明细用 `--list` 查看；
- **回环台**：在内存里把 LSP 客户端与服务端直接对接（复用生产代码的分帧通道），不依赖真实进程；
- **真机用例**在缺工程 / URP / DXC 时记为 **SKIP，不影响退出码**；
- **按套件过滤**：例如 `--filter ShaderLab.Fixtures`、`--filter Vfs.Include`；套件名清单用 `--list` 查看；
- **覆盖方向**：切片与宏矩阵、真实语料、装配与平台宏、归属映射、补全与跳转、VFS 路径与文件监视、日志与崩溃转储、LSP 协议与调度、DXC 编译等。

**场外基准台**（`probes/`，大多不入库、不进 CLI）：`rig-bench`（严格基准台）、`perf-stress`、`opt-verify`、`user-shader`、`dxc-aot` 等。

**运行预期**：在环境变量齐备的机器上（Release 构建），`--selftest` 应当全部通过、退出码 0；裸跑（不设 `MICROSHADER_UNITY_PROJECT`、不给 `--urp`）会出现少量环境依赖的失败与跳过（AOT 冒烟、真机语料等），退出码为 1 —— 这是环境缺依赖，不是代码缺陷；CI 不带环境变量等于恒红。

---

## 12. 非功能指标（NFR）

| 指标 | 验收口径 |
| :--- | :--- |
| **NFR-1 常驻内存** | Private Working Set ≤ 30 MB（总 WS 含映射 DLL 映像页，不设上限也不作验收） |
| **NFR-2 冷启动** | 编辑器唤起 → `initialize` 应答，端到端 ≤ 30 ms（进程创建下限实测约 12 ms） |
| **NFR-3a 托管热路径** | 切片/装配/符号查询/跳转/补全 ≤ 10 ms（单线程、预热后；线程级分配增量 = 0） |
| **NFR-3b 原生编译** | 按切片规模分档；≤400 行切片 P95 建议 ≤ 500 ms，必须后台线程 + 支持取消 + 结果缓存 |
| **NFR-4 零 GC 扰动** | 主断言：`GC.GetAllocatedBytesForCurrentThread()` 增量 = 0（串行 + 空闲基线）；稳态打字 60 s 内 Gen1/Gen2 = 0 |
| **NFR-5 原生 DLL 闭环** | 随包附带 `dxcompiler.dll`（必需）与 `dxil.dll`（可选），脱离系统盘/SDK 目录搜寻；启动时绝对路径预加载 |

---

## 13. 当前能力与已知限制

### 13.1 已具备的能力

- 诊断、补全、跳转三条主线齐备（模块 1–10、12），黄金判据成立：URP 官方 `Lit.shader` 在正确宏集下零 Error；
- 在真实手写语料上实测过补全与跳转：覆盖上百个 `.shader`、数千个成员补全点，F12 跳转自洽且全部落在延迟预算内（语料规模与测量口径见 `docs/testing-guidelines.md`）；
- 交付形态齐备：原生 exe + 三闸门发布脚本 + LSP 回环门禁 + VS Code 扩展包；
- Unity 桥接（可选）接入后，注入的宏与变体选择来自编辑器实测状态；未接入时服务端完全静默，诊断链路不受影响。

### 13.2 已知限制

- **自检没有统一报表出口**：目前只有 stdout 文本，没有 JSON/Markdown 基线文件，也没有自动对比与 CI 阈值。
- **三处覆盖薄弱点**（尚无专项用例）：归属映射对主文件的列换算只覆盖 ASCII 输入；编译缓存对「跨文档同名编译单元」的键隔离；「缓存命中 + 并发派发」的组合。
- **环境依赖用例没有最低执行条数门禁**：受限环境下「全绿」可能由大量 SKIP 构成。
- **性能基准工具与历史数据无法从干净 checkout 复现**（`probes/` 大多不入库）。

### 13.3 明确不投入的方向

- **DXC 自带的 `IDxcIntelliSense` 接口**：曾评估用它提升补全精度，实测后放弃，补全与跳转继续由自研的轻量索引承担（它跑的是 C++ 前端、给不出 HLSL 语义，且延迟不符合交互预算）。

---

## 14. 文档索引

| 文档 | 定位 |
| :--- | :--- |
| `README.md` | 仓库首页：项目定位、特性、架构、快速上手、构建自检、文档索引 |
| `docs/module-contracts.md` | 功能模块说明书：每个功能模块是什么、怎么工作、保证与限制 |
| `docs/testing-guidelines.md` | 测量类结论的准入规则 |
| `ARCHITECTURE.md` | 代码索引：目录职责、想改 X 去哪、不可违反的约束 |
| `CONTRIBUTING.md` | 贡献指南：环境、构建与自检、提交规范、文档纪律 |
| `client/vscode/CHANGELOG.md` | 版本变更记录 |
| 内部资料 | 主设计规格、逐模块详细设计、选型论证等由维护者本地保管，不随仓库分发 |

---

## 15. 术语表

| 术语 | 含义 |
| :--- | :--- |
| **Pass** | ShaderLab 中的一次渲染通道，含 `HLSLPROGRAM…ENDHLSL` 代码块 |
| **切片（Slicing）** | 从 `.shader` 中提取单个 Pass 的 HLSL 源码文本 |
| **装配（Assembling）** | 把切片 + 注入宏 + `#line` 锚 + include 展开组装成可编译文本 |
| **VFS** | 虚拟文件系统 —— 把 `Packages/...` 虚拟路径解析为磁盘物理路径 |
| **归属映射** | 把 DXC 报的错误坐标映射回 `.shader` 或外部头文件的行列 |
| **变体首项** | `#pragma multi_compile _DIRECTIONAL _POINT` 中被选中的关键字 |
| **Segment 模型** | 模块 3 的装配结构：Injected / Anchored 段只 append，杜绝行号偏移 |
| **聚焦 Pass** | 打字时由行级 Diff 推断出的当前正在编辑的 Pass |
| **黄金判据** | URP 官方 `Lit.shader` 在正确宏集下必须零 Error |
| **SKIP** | 真机/DXC 用例在依赖缺失时的「跳过」态，不影响退出码 |

---

*本说明书的数字与结论以仓库现有源码与文档为准；逐模块 API 契约请查 `docs/module-contracts.md` 的对应模块章节。*
