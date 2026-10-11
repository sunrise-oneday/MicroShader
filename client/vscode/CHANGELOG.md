# 变更日志

本文件是 VS Code 市场页的变更日志；服务端随扩展一起分发，变更以本文件为准。
本扩展遵循 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.1.0/)；条目按「对使用者可见」的程度取舍，纯内部细节不展开。

## [0.1.13] - 2026-10-11

### 修复

- **内建类型进入补全。** 输入 `float` 会给出 `float2` / `float3` / `float4` / `float4x4` 等，
  `half` 给 `half2`~`half4`，`sam` 给 `sampler2D` / `SamplerState`——共覆盖标量、向量、矩阵、
  采样器与纹理对象 44 个常用类型。此前这类 HLSL 语言内建类型完全没有补全来源（输入 `float` 只会看到 URP 宏）。
  类型候选与宏同档排序，不会被同前缀的宏淹没。

## [0.1.12] - 2026-10-10

### 新增

- **前缀补全纳入 include 链里的 URP 函数与结构体。** 输入 `Transform` 会补出 `TransformObjectToHClip` 等一批转换函数；这类名字几乎都定义在包内头文件里，此前一条都补不出来。
- **括号内补全：`CBUFFER_START(` 与 `SAMPLER(`。** `CBUFFER_START(` 里给 Unity 的缓冲区约定名（`UnityPerMaterial` 排第一，另有 `UnityPerDraw` / `UnityPerFrame` / `UnityPerCamera` / `UnityPerCameraRare`）；`SAMPLER(` 里给已声明的采样器名与按纹理推导的 `sampler_ + 纹理名`。其他括号内不会弹候选。
- **纹理派生名补全：`_X_ST` 与 `_X_TexelSize`。** 写过 `TEXTURE2D(_MainTex)` 之后，输入 `float4 _Mai` 会给出 `_MainTex_ST` 与 `_MainTex_TexelSize` —— 这两个名字由 Unity 运行时提供，此前没有任何补全来源；已显式声明过的名字不会重复出现。Cube 纹理只给 `_ST`，3D / Array 纹理不给（Unity 对它们没有 tiling / offset 语义，推导只会是错误候选）。

### 修复

- **编辑与磁盘上的变化都即时反映到补全：** 新写的 `#include` 不必等保存就能补出内容；被 include 的头文件在磁盘上改动后，补全与 `F12` 也不会继续用旧符号。

### 已知事项

- 少数名字同时是 URP 宏与内建条目时会出现两条候选（例如 `TRANSFORM_TEX`、`SAMPLER`：一条标注「宏（#define）」、一条带内建签名），为既有行为的延续，留待后续合并。

## [0.1.11] - 2026-10-08

### 变更

- 内部性能优化：内存占用下降（使用者可见行为无变化）。

## [0.1.10] - 2026-10-08

### 变更

- 内部优化：折叠范围的产出与上一版完全一致（使用者可见行为无变化）。

## [0.1.9] - 2026-10-08

### 新增

- **折叠范围。** 按大括号块（`Shader` / `SubShader` / `Pass` / `Properties` / `struct` / 函数体等）、程序块（`HLSLPROGRAM` / `CGPROGRAM` 等与它们的 `END*` 收尾）、`CBUFFER_START` … `CBUFFER_END` 与预处理器条件（`#if` … `#endif`）产出可折叠区间。字符串与注释里的花括号不参与配对；错配的括号一律忽略 —— 宁可少给一条，不给错一条。
- **粘滞滚动窗口按结构显示。** 把 `editor.stickyScroll.defaultModel` 设为 `foldingProviderModel` 后，粘滞条目逐层显示 `Shader` → `SubShader` → `Pass` → 程序块 → 函数 / `#if`；控制流（`if` / `for` / `while` 等）不进粘滞列表，也因此不可折叠（合意取舍）。按语言各配一行即可，示例见 README。

## [0.1.8] - 2026-10-07

### 修复

- **粘滞滚动窗口现在能显示 `Pass` 层级。** 此前一栏只显示程序块，`Pass` 永远不出现 —— 而官方 ShaderLab 扩展是能显示 `Pass` 的。

## [0.1.7] - 2026-10-06

### 新增

- **宏（`#define`）进入前缀补全 —— 本文件的与 include 链里的都给。** `UNITY_REVERSED_Z`、`SAMPLE_TEXTURE2D_SHADOW` 这类 Unity / URP 宏不必再手敲。

### 已知事项

- 宏补全不判断可见性（不考虑 `#undef` 与条件编译）：可能多给，不会漏给。

## [0.1.6] - 2026-10-06

### 修复

- **改好被 include 的头文件后，主 shader 上的红线现在会自动消失。** 此前只有宿主 shader 自己被编辑才会重算 —— 在别处修好头文件、引用它的文件红线却一直挂着，看起来像工具坏了。
- **修复「某个 Pass 已经没有错误、保存后旧红线仍残留」的问题。**

## [0.1.5] - 2026-10-06

### 修复

- **挂在 `#include` 行上的外部头文件报错，现在标明真正出处。** 消息格式为 `头文件错误: [文件:行] DXC 原文`，并关联到出错位置；此前看起来像 `#include` 那一行本身有问题。

## [0.1.4] - 2026-10-05

### 修复

- **纯 HLSL / Cg 文件（`.hlsl`、`.cginc`）现在有完整的本文件符号补全。** 此前这类文件索引为空 —— 上一行刚声明的变量，下一行敲前缀一条都补不出来。

### 变更

- 打包流程加了一道检查：不得再把 `.hlsl` / `.cginc` 声明为本扩展所属的语言，防止 0.1.2 的语言归属问题回流。

## [0.1.3] - 2026-10-05

### 修复

- **不再抢占 `.hlsl` / `.cginc` 的语言归属。** 此前这两个扩展名会覆盖 VS Code 内置 `hlsl` 的声明，于是 `.hlsl` 高亮退回本扩展的 ShaderLab 语法；只在 `onLanguage:hlsl` 时激活的第三方扩展会静默地不再工作（没有报错、没有提示，只是 HLSL 里什么补全都没有）。
- 交还后三方可以同时生效、各管一摊：内置 `hlsl` 管 HLSL 高亮，第三方扩展（如 Shader languages support）管 HLSL 补全与跳转，本扩展管诊断和 Unity 上下文补全 —— 本扩展的语言服务器按文件名模式挂接，不受语言归属影响。想手动切回本扩展的语法仍可以：`Ctrl+K M` → `Unity ShaderLab`。

## [0.1.2] - 2026-10-02

### 新增

- 支持 VS Code 工作区信任（Restricted Mode）：在未信任的工作区里不再被静默禁用。
- 新增 `microshader.trace` 设置（把扩展与服务端之间的报文写进 `MicroShader (LSP Trace)` 输出通道），以及命令「`MicroShader: 打开日志`」「`MicroShader: 复制诊断信息到剪贴板`」（打包服务端路径、工程根、客户端状态与最近 60 行日志）。
- ShaderLab 编辑体验：花括号自动缩进与反缩进、`{` `}` 之间回车展开、块注释自动闭合；同时关闭纯文本词补全，避免文本词混进候选。

### 变更

- 非 Windows 平台改为启动即给出明确原因（此前会反复重试、停在「已停止」）；改动影响服务端启动的设置后，会提示一键重启语言服务器。

## [0.1.1]

### 新增

- 打开单个 `.shader` 文件（而不是整个 Unity 工程目录）时，扩展会把 Unity 工程根显式传给服务端 —— 修复此类场景下补全全空、诊断只剩 `MS0001` 的问题。
- 新增三个性能旋钮：`microshader.analysisDebounceMs` / `maxCompileThreads` / `syncIncludeFallbackFiles`。
- 修复扩展在部分环境下不会被激活的问题。

## [0.1.0]

### 新增

- 首个版本：ShaderLab / HLSL 诊断（真实 DXC 编译 + 标签语义校验）、成员与内建分量补全、`F12` 定义跳转、`#include` 超链接、文档大纲。
- 服务端二进制与 DXC 随扩展分发，离线可用（不需要 .NET、不需要联网）。
