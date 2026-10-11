# Unity Shader LSP (MicroShader)

> Unity ShaderLab / HLSL 的**即时诊断、智能补全与跨文件跳转**扩展。
> 服务端是原生 AOT 编译的 exe（自带 DXC 编译器）—— **不需要装 .NET、不需要联网、安装即用**。

## 能做什么

| 能力 | 说明 |
| :--- | :--- |
| **即时诊断** | 打字停顿 300 ms 自动诊断你正在改的那个 Pass；`Ctrl+S` 按全文件所有 Pass 诊断。错误来自真实 DXC 编译，不是正则猜测。 |
| **URP 路径穿透** | 直接理解 `#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"`，覆盖 5 类包来源 + 4 条 include 规则。 |
| **精确坐标归属** | 外部头文件里的报错会重映射回你自己的 `#include` 那一行，并附 RelatedInformation。 |
| **智能补全** | 结构体成员、内建向量分量、文档内符号、include 链里的类型、引擎内置 uniform。 |
| **跳转与大纲** | `F12` 跨 include 穿透跳定义；`#include` 变成可点超链接；大纲 / 面包屑；折叠范围（粘滞滚动窗口的数据源）。 |
| **标签语义校验** | ShaderLab 的 `Tags` / `LightMode` / `RenderPipeline` 拼写与取值校验（`SLxxxx`）。 |
| **可选：Unity 桥接** | 装一个 Editor 脚本后，诊断会用上 Unity 编辑器**实测**的全局宏与关键字状态。不装也能用。 |

> **粘滞滚动窗口**：把 `editor.stickyScroll.defaultModel` 设为 `foldingProviderModel` 后，粘滞条目使用本扩展的折叠范围（`Shader` → `SubShader` → `Pass` → 程序块 → 函数 / `#if`），控制流不会进入列表。按语言加即可，例如 `"[shaderlab]": { "editor.stickyScroll.defaultModel": "foldingProviderModel" }`。

## 前置条件

- **Windows x64** —— 服务端与 DXC 都是 Windows 原生二进制。
- **不需要** .NET 运行时。
- **不需要**联网。
- **不需要**装 Unity 官方扩展或任何桥接脚本 —— 桥接是可选增强。

## 安装

**方式一：从 vsix 安装（离线可用）**

```powershell
code --install-extension <路径>\microshader-unity-shader-lsp-0.1.11.vsix --force
```

扩展 ID 是 `microshader.unity-shader-lsp`。装完**重新加载窗口**（`Ctrl+Shift+P` → `Developer: Reload Window`）。

**方式二：从源码一键打包并安装**

```powershell
pwsh -File scripts\package-vsix.ps1 -Install
```

## 首次验证（30 秒）

1. **看状态栏** —— 打开任意 `.shader`，右下角应出现 `✓ MicroShader: 就绪`。
   若显示 `启动中` 卡住、`已停止` 或 `未就绪`，跳到下面的「故障排查」。
2. **试补全** —— 在一个已知类型的变量后敲 `.`，应弹出成员候选（如 URP 的 `Input` / `Surface` 结构体字段）。
3. **试跳转** —— 光标停在 `TransformObjectToHClip` 这类 URP 内建函数上按 `F12`，应跳到 `SpaceTransforms.hlsl`。

任何一步不对，先执行命令 **`MicroShader: 复制诊断信息到剪贴板`**，把内容贴出来 ——
里面有扩展版本、实际使用的服务端路径、解析到的 Unity 工程根、客户端状态和最近 60 行日志。

## 设置

| 设置键 | 默认 | 说明 |
| :--- | :--- | :--- |
| `microshader.serverPath` | 空 | 自定义 `UnityShaderLsp.exe`（exe 或所在目录）。留空用扩展内置的二进制。 |
| `microshader.attachForeignLanguages` | `true` | 是否也接管由其它扩展注册语言的 shader 文件（如 Unity 官方扩展的 `UnityShader`）。关掉后只服务语言为 `shaderlab` 的文件。 |
| `microshader.analysisDebounceMs` | 空 | 打字防抖毫秒数。留空用服务端默认（300，夹取到 200–5000）。调大能削掉打字时的 CPU 峰值，代价是诊断更迟。 |
| `microshader.maxCompileThreads` | 空 | DXC 并行编译线程上限。留空用默认（8）。低配机器调小可降 CPU 峰值。 |
| `microshader.syncIncludeFallbackFiles` | 空 | 补全缓存未命中时同步扫描「当前块直连 include」的个数。留空用默认（2）；`0` = 关闭同步兜底（补全路径完全不读盘）。 |
| `microshader.trace` | `off` | 把扩展与服务端之间的 LSP 报文写进 `MicroShader (LSP Trace)` 输出通道。`off` / `messages`（方法名与耗时）/ `verbose`（完整报文体）。 |

改完这些设置会弹一条「需要重启语言服务器」的提示，点「立即重启」即可生效。

## 常用命令

在命令面板（`Ctrl+Shift+P`）输入 `MicroShader`：

| 命令 | 用途 |
| :--- | :--- |
| `MicroShader: 重启语言服务器` | 改完设置、或状态异常时。点状态栏也能触发。 |
| `MicroShader: 打开日志` | 打开 `MicroShader` 输出通道。 |
| `MicroShader: 复制诊断信息到剪贴板` | 打包现场信息，求助时直接贴。 |
| `MicroShader: 诊断当前文件（语言/服务器状态）` | 一行问答：当前文件的 languageId、扩展激活没、客户端状态。 |

## 故障排查（按症状）

### 状态栏显示「MicroShader: 未就绪」

服务端二进制探测失败，扩展会同时弹一条带「打开设置」按钮的错误。

- **原因**：扩展内置的 `bin/UnityShaderLsp.exe` 或 `bin/dxcompiler.dll` 缺失；或 `microshader.serverPath` 指向了没有 exe 的位置。
- **确认**：执行「复制诊断信息」看「实际使用的服务端」那一行。
- **怎么办**：清空 `microshader.serverPath` 用回内置二进制；或重新安装扩展。

### 状态栏显示「MicroShader: 已停止」，反复重试

- **原因**：非 Windows 平台（扩展会直接报「当前平台暂不支持」），或 exe 被杀软拦截、被占用。
- **确认**：「打开日志」看有没有 `语言服务器停止（将自动重试）`。
- **怎么办**：把扩展目录加入杀软白名单；或执行「重启语言服务器」。

### 补全一条候选都不出

按顺序做这三件事：

1. **看状态栏**。不是 `就绪` 的话先按上一节处理。
2. **看 languageId**。执行「诊断当前文件」，如果 `languageId` 不是 `shaderlab` / `UnityShader` / `hlsl`，说明文件被别的扩展注册成了别的语言 —— 打开设置把 `microshader.attachForeignLanguages` 保持为 `true`。
3. **看有没有工程根**。执行「复制诊断信息」，若「解析到的 Unity 工程根」是 `<未找到>`，说明当前窗口既不在 Unity 工程目录下、打开的 shader 也不在工程里。
   扩展会退化成空索引 —— 诊断只剩 `MS0001`，补全自然也没有。**用 VS Code 打开 Unity 工程根目录**（而不是只拖一个 .shader 进来）即可。

### 诊断只剩 MS0001（包路径未解析）

同上一节的第 3 点。另外确认 `Packages/` 与 `Library/PackageCache/` 存在 —— 工程还没在 Unity 里打开过一次时，包缓存是不存在的。

### `.shader` 文件没有语法高亮

- **原因**：文件关联被别的扩展抢走（典型是 Unity 官方扩展把 `.shader` 注册成 `UnityShader`）。
- **怎么办**：状态栏右下角点语言名 → 选 `Unity ShaderLab`；或在设置里给 `files.associations` 加 `"*.shader": "shaderlab"`。本扩展按**文件名模式**挂接语言服务器，所以高亮归属和补全是否生效是两件独立的事 —— 高亮没了不代表补全失效。

### 一打开就进 Restricted Mode，扩展不工作

VS Code 的[工作区信任](https://code.visualstudio.com/docs/editor/workspace-trust)机制。本扩展已声明支持不受信任的工作区（它只在本地读工程文件、不执行工作区代码），因此**不会**因此被禁用；如果你看到它被禁用，检查是不是企业策略强制了 `security.workspace.trust`。

## 可选：安装 Unity 编辑器桥接

装上它，诊断会用上 Unity 编辑器**实测**的全局宏与关键字状态（而不是从工程文件推断）。

把 `client/unity/MicroShaderBridge/` 整个文件夹复制到你工程的任意 `Editor` 目录下（例如 `Assets/Editor/MicroShaderBridge/`）即可。零依赖、不占网络端口（只绑 `127.0.0.1`）。

装与不装，**正常情况下的诊断结果应逐条相同** —— 桥接只是让宏状态更贴近编辑器实测。细节见 `client/unity/README.md`。

## 卸载与回滚

```powershell
code --uninstall-extension microshader.unity-shader-lsp
```

回滚到旧版本：直接用 `--install-extension` 装上旧版 vsix，加 `--force`。

---

完整的功能范围、性能口径与已知缺口见仓库根 `README.md`。
