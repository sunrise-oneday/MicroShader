# 变更日志

本扩展遵循 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.1.0/)。
条目按「对使用者可见」的程度写，内部重构不列。

## [0.1.8] - 2026-10-07

### 修复

- **`Pass` 现在能被粘滞滚动窗口（Sticky Scroll）识别。** 此前只显示 `HLSLPROGRAM`（程序块），
  `Pass` 一栏永远不出现 —— 而 Unity 官方 ShaderLab 扩展是能显示 `Pass` 的。

  根因是**行号口径漏了一层转换**：`ShaderPassSnippet.StartLineNumber` / `EndLineNumber` 是 1-based，
  而节点里必须经 `LspLine(...)` 转成 0-based（`BlockNode` 一直是对的）。`PassNode` 却直接用了
  `MinLine(blocks)` —— 于是 Pass 节点的 range 起点落在**第一个程序块**那一行，两点后果：

  1. 起点比 `Pass` 关键字晚若干行 —— Pass 的 ShaderLab 部分（`Name` / `Tags` / `ZWrite` /
     `Cull` …）整段都不在任何 Pass 节点的 range 内；
  2. Pass 的 range 与它的子节点（程序块）**完全重合**，粘滞窗口取最内层时只剩程序块。

  修法两处：

  1. `ShaderPassSnippet` 新增 `PassStartLineNumber`（`Pass` 关键字所在行），由
     `ShaderLabStateMachine` 在 `pendingPass` 置位时记录、随块定型写入；
  2. `PassNode` 用它作为 `Line` / `StartLine`，并统一经 `LspLine(...)` 转换；`EndLine` 同样补上转换
     以与 `BlockNode` 对齐。拿不到时（孤儿块）退回原行为。

  实测（同一文件，LSP 0-based 行号）：

  ```text
  修复前：ShadowCaster (Pass) range=27:0 → 76:19    Hlsl range=27:0 → 76:19   ← 完全重合
  修复后：ShadowCaster (Pass) range=17:0 → 76:19    Hlsl range=27:0 → 76:19   ← 起点 = Pass 关键字行
  ```

  两条既有硬约束仍满足：`range` 包含 `selectionRange`；父节点 range 包含全部子节点 range
  （嵌套为 `SubShader [17,212] ⊇ Pass [17,76] ⊇ 程序块 [27,76]`）。

### 验证

- `Navigation` 套件 20 / 20（含 `OutlineHierarchical` / `OutlineFlat` / `OutlineKindsInValueSet`）
- 全量自检 214 / 214 / 0 / 0

## [0.1.7] - 2026-10-06

### 新增

- **宏（`#define`）进入前缀补全 —— 本文件的与 include 链的都给。** 此前宏完全不进候选：
  `ProvidePrefixCompletions` 只合并内建、内建 uniform 与文档符号三张表，而宏不在其中任何一张
  （它只存在于 `HlslDocumentIndex.Declarations`）。后果是 Unity/URP 的宏
  （`UNITY_REVERSED_Z`、`SAMPLE_TEXTURE2D_SHADOW` …）一个都补不出来，只能手敲 ——
  而它们几乎全定义在包内头文件里，正好是最难记住的那一批。

  两处改动：

  1. `IntelliSenseService` 新增宏收集路径：先扫本文件索引，再扫 include 闭包
     （数据取自 include 符号缓存，本路径不额外读盘；缓存尚未预热时闭包为空，下次请求即有）。
     候选与文档符号同档排序（`sortText` 前缀 `0_`），`detail` 标注「宏（#define）」。
  2. **修复 `Merge` 丢失声明表**：它只合并了 `VariablesToType` 与 `Structs`，
     `Declarations` 整张表被丢弃 —— 于是「整篇文档索引」里一个宏都没有，本文件写的
     `#define` 同样补不出来。合并改为逐条去重（同名声明允许 1→N，但重复项会让
     F12 候选与文档大纲出现重条）。

  **本版不做宏可见性求值**（`#undef`、条件编译一律忽略）：真值需要按 include 顺序对
  `#define`/`#undef` 事件流求值（详设 v2.0 第 6、7 条）。此处宁可多给也不漏给 ——
  漏给的症状正是「宏得手敲」。

### 未做（如实标注）

- include 链的**函数与变量**仍不进前缀补全：`IncludeSymbolCache` 目前只把结构体喂给
  成员/类型推导，其余种类只留在缓存里。这是独立的一块，需要把它的消费面从
  「只取 HlslStruct」扩到全种类。

### 验证

真实进程实测（自建文件 + URP 工程）：

```text
[include 链的宏]   22 ms  候选 1  ✓ UNITY_REVERSED_Z
[文档自身的宏]      1 ms  候选 1  ✓ LOCAL_PROBE_MACRO
[文档自身的变量]    1 ms  候选 1  ✓ localVarProbe（未回归）
[include 链的函数]  1 ms  候选 0  ✗ SafeDiv（本版不覆盖，见上）
```

全量自检 214 / 214 / 0 / 0。

## [0.1.6] - 2026-10-06

### 修复

- **改好被 include 的头文件后，主 shader 上的红线现在会消失。** 此前只有在宿主 shader
  自己被编辑时才会重算，于是「修好 helper 头文件里的错，红线一直挂着」——看起来像工具坏了。
  现在服务端处理 `workspace/didChangeWatchedFiles`（客户端本就配了 `**/*.{shader,cginc,hlsl,...}`
  的监视，只是服务端没有对应处理器）：任何这类文件变化都把所有**已打开**文档走一次全量重算，
  与打字共用防抖合并。

  **这是刻意的「最小可用版」**：不做 include 精确匹配（那需要在分析时记录每个文档的直接 include
  物理路径并维护反查表，跨诊断引擎与会话两层）。选择宁可多算也不漏 —— 漏算的症状（红线不消失）
  看起来像工具坏了，多算只是一次可被合并的重算。

- **修复全量重算时「零诊断的 Pass 槽位不被清空」**（既有缺陷，本次由上面这条路径暴露）。
  `ShaderDocumentAnalyzer` 里给零诊断 Pass 补空列表的逻辑只写在非全量分支（2026-10-05 修
  打字场景时加的），全量分支只填了 `covered` 而没补 `PassDiagnostics` 条目 ——
  于是发布侧的 `SetPass` 永远碰不到那个槽位，旧红线永久残留。走全量重算的路径不止本次新增的
  watched-files，**didSave 也走全量**，所以这个缺陷此前也会在「保存后红线不消失」时出现。

- 诊断缓存（模块 6）补上失效通道：`AnalysisRequest` 新增 `ForceRecompile`，由
  `ShaderDocumentAnalyzer` 在**信号量内**清空单元缓存。放在分析线程而不是会话侧，是因为缓存字典
  不支持并发读写，会话侧直接清会和正在读它的分析线程撞车。
  缓存原本的假设是「被 include 的文件在会话内只读」（对 PackageCache 成立），用户改自己工程里的
  helper 头文件时这条假设不成立。

### 验证

真实进程实测（自建 shader + helper，改磁盘上的 helper 后发通知）：

```text
[发布 #1] 条数=1   头文件错误: [Helper.hlsl:4] use of undeclared identifier 'BoomSymbol'
[发布 #2] 条数=0   ← 改好磁盘上的头文件并发出通知后，红线消失
```

## [0.1.5] - 2026-10-06

### 修复

- **指向 `#include` 行的外部头文件诊断，现在会标明来源。** 当错误发生在被直接 include 的文件里时，
  红线按设计挂在主文件的 `#include` 行列上（这样包头文件里动辄几十条错不会糊满用户文件）；
  但消息此前是 DXC 原文、**不带任何来源标记** —— 于是「自己的 helper 头文件里写错一行」
  在主 shader 上看起来就像那行 `#include` 本身有问题。现在与另外两个分支
  （`深层依赖错误` / `外部头文件错误`）对齐，改写为：

  `头文件错误: [<文件名>:<行>] <DXC 原文>`

  实测（`--analyze`，故意在 helper 头文件里用未声明标识符）：

  `12:23  [Error] 头文件错误: [BHelper.hlsl:4] use of undeclared identifier 'BoomSymbolForProbe'`
  `         ↳ 关联: .../BHelper.hlsl:4`

### 变更

- 新增两条断言钉住上述文案（加在既有用例 `AttributionTests.DirectIncludeRemapped` 里，
  不新增用例）。自检总数仍为 214。

## [0.1.4] - 2026-10-05

### 修复

- **纯 HLSL / Cg 文件现在有完整的本文件符号索引。** 影响 `.hlsl`、`.cginc` 以及被 include 的头文件。
  此前 `IntelliSenseService` 只从切片器产出的 `Passes` 与 `SharedSnippets` 取块，而这类文件里
  没有任何 `HLSLPROGRAM` / `HLSLINCLUDE` 标记 ⇒ 切片器不产出块 ⇒ 索引为空。症状是
  「文件里上一行刚声明的变量，下一行敲前缀却一条都补不出来」，而且看起来像工具根本没生效。

  修复：`Passes` 与 `SharedSnippets` 都为空、**且切片器确认没有 `SubShader`** 时，
  把整篇文本当成单个 HLSL 块。用 `SubShaderCount == 0` 做门是为了不误伤真实的 `.shader` ——
  它们即使没有 program block、甚至块未闭合也一定检测得到 `SubShader`，
  不该被当成 HLSL 整篇索引（否则 `Properties` 里的名字会变成 HLSL 候选）。

- 新增自检用例 `IntelliSense.BareHlsl`：正断言纯 HLSL 文件能补出本文件声明的变量，
  反断言带 `SubShader` 的文件不会被整篇索引。自检总数 214
  （同步对齐了文档里过期的基线数字 212 → 214，其中 213 是上一笔提交新增但未同步的部分）。

### 变更

- `scripts/package-vsix.ps1` 新增发布闸门：`languages.extensions` 不得声明 `.hlsl` / `.cginc`。
  0.1.2 曾声明过这两个扩展名，而 VS Code 的「扩展名 → language id」只有一格、且后注册者通吃，
  于是抢走了内置 `hlsl` 语言的归属 —— 后果是**所有只在 `onLanguage:hlsl` 激活的第三方扩展
  静默失效**（没有报错、没有提示，只表现为「HLSL 里什么补全都没有」，极难定位）。
  该声明已在 0.1.3 撤掉，这条闸门防止它被写回来。

## [0.1.3] - 2026-10-05

### 修复

- **不再抢占 `.hlsl` / `.cginc` 的语言归属。** 此前 `contributes.languages` 把这两个扩展名
  归到 `shaderlab` 语言下，而 VS Code 内置的 `hlsl` 语言也声明了它们。扩展的注册晚于内置，
  会覆盖内置的声明，后果有两条，都是对使用者可见的功能消失：

  1. `.hlsl` / `.cginc` 的高亮从内置 HLSL 语法退回本扩展的 ShaderLab 语法 ——
     后者是为 ShaderLab 外壳写的，对纯 HLSL 覆盖不足，表现为「没有 HLSL 上下文」；
  2. 一些只在 `onLanguage:hlsl` 时激活的第三方扩展（典型是
     「Shader languages support for VS Code」，它的激活事件只有
     `onLanguage:hlsl` / `glsl` / `cg`）**完全不会激活** ——
     它的补全、悬停、跳转、符号全部静默消失，且没有任何报错提示。

  现在这两个扩展名交还给内置 `hlsl`。**本扩展自身的功能不受影响**：语言服务器的
  `documentSelector` 是按文件名模式挂接的（`**/*.hlsl`、`**/*.cginc`），与语言归属无关，
  诊断 / 补全 / 导航照常工作。这样三方可以同时生效：

  | 角色 | 由谁提供 |
  | :--- | :--- |
  | HLSL 语法高亮 | VS Code 内置 `hlsl` 语言 |
  | HLSL 补全 / 悬停 / 跳转 / 符号 | 第三方扩展（如 Shader languages support） |
  | 诊断 + Unity 上下文补全与导航 | 本扩展 |

  想手动切回本扩展的语法仍可以：`Ctrl+K M` → 选 `Unity ShaderLab`。

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
