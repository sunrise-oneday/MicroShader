# MicroShader · UnityShaderLSP

> 给 Unity Shader 开发者的小型语言服务：**实时诊断、智能补全、跳转导航**，
> 直接装进 VS Code / Sublime，自带编译器，不需要装 .NET、不需要联网。
> 为低内存机器而做——常驻内存几十兆，而不是几吉。

## 它能做什么

- **实时诊断**：打字停顿约 300 毫秒，就对当前正在写的 Shader 跑一次真实编译（内置 DirectXShaderCompiler），
  错误和警告直接标在代码上；按 `Ctrl+S` 对整份文件的所有 Pass 做完整检查。
  不用再切回 Unity 看 Console。
- **URP 路径穿透**：直接理解 `#include "Packages/com.unity.render-pipelines.universal/..."` 这类虚拟路径；
  第三方头文件里的报错会被映射回你自己的文件和行号。
- **智能补全**：结构体成员、向量分量（`.rgb` / `.xz`）、文件内符号、预处理器宏、
  URP 函数（`TransformObjectToHClip` 等，从你工程的现有代码里现场解析）、
  `CBUFFER_START(` 里的 `UnityPerMaterial`、`SAMPLER(` 里的采样器名、
  贴图的 `_X_ST` / `_X_TexelSize` 派生名。
- **跳转与导航**：`F12` 跳到定义（穿过 include 链）；`#include` 是可点开的链接；
  文件大纲与面包屑；折叠范围加粘滞滚动窗口（Shader → SubShader → Pass → 函数逐层跟随）。
- **标签语义校验**：`Tags` 里的标签拼错（比如 `RenderPipeline` 写成 `RenderPipeling`）编译器不会报错，
  但整段渲染会静默失效——这类问题由本服务专门检查（诊断码 `SL0101`~`SL0105`）。
- **可选的 Unity 桥接**：装进 Unity 工程后，诊断可以使用编辑器实测的宏与关键字状态；不装也完全正常。

## 为什么做「小的」

在 16 GB 内存的机器上跑 Unity 商业工程是什么体验：Unity 编辑器常驻 4–6 GB、Rider 常驻 3.5–5 GB、
系统加浏览器再吃掉几 GB——内存逼近上限，打字掉帧、切窗口冻结。

大型 IDE 的 Shader 支持依赖全工程符号索引，而这正是内存大户。
本项目换一条路：**只在你打字的那一刻，对当前这一个文件做精确诊断**——
把「精确」和「轻」同时拿到，常驻内存几十兆，冷启动毫秒级。

## 安装

**VS Code**（扩展 ID `microshader.unity-shader-lsp`）：服务端二进制与编译器都内置，不需要 .NET、不需要联网。

```powershell
# 从仓库 dist/ 目录选一个 .vsix（文件名形如 microshader-unity-shader-lsp-<版本>.vsix）
code --install-extension dist\microshader-unity-shader-lsp-0.1.12.vsix --force
```

装完**重载窗口**（`Ctrl+Shift+P` → `Developer: Reload Window`）即可用。

**Sublime Text 4**：把 `client/sublime/` 下的 `LSP-MicroShader.sublime-settings` 与
`shaderlab.sublime-syntax` 放进 `Packages/User/`，复用同一套服务端。

**Unity 桥接（可选）**：把 `client/unity/MicroShaderBridge/` 整个文件夹复制到工程的任意 `Editor` 目录
（例如 `Assets/Editor/MicroShaderBridge/`）。零依赖、只绑 `127.0.0.1`、不需要管理员权限。

## 30 秒确认它真的在工作

打开任意 `.shader` 文件：

1. 右下角状态栏出现 `MicroShader: 就绪`；
2. 在结构体变量后敲 `.`，弹出成员候选；
3. 光标放在一个函数名上按 `F12`，跳到定义。

三条都成立就是装好了。出问题时，用命令面板里的 `MicroShader: 复制诊断信息到剪贴板` 抓取现场信息；
按症状的排查方法见 [`client/vscode/README.md`](client/vscode/README.md)。

## 从源码构建与测试

需要 .NET SDK 10.0.401（见 `global.json`）。仓库零第三方 NuGet 依赖，构建可完全离线。

```powershell
dotnet build MicroShader.sln -c Release

# 运行自检（指向一个真实的 Unity 工程，启用全部用例；完整口径见 CONTRIBUTING.md）
$env:MICROSHADER_UNITY_PROJECT = '<Unity 工程根>'
.\src\MicroShader.SelfTest\bin\Release\net10.0\MicroShader.SelfTest.exe --selftest --urp '<URP 包目录>'

# 不用编辑器，直接看一个 shader 的诊断输出
.\src\MicroShader.SelfTest\bin\Release\net10.0\MicroShader.SelfTest.exe --analyze samples\BrokenShader.shader
```

- 用例总数以 `--list` 输出为准；退出码 `0` = 全部通过（可直接挂 CI）。
- 不依赖 Unity 工程的验证样例见 [`samples/README.md`](samples/README.md)。
- 发布打包（AOT 发布 + 打 vsix）脚本在 `scripts/`，用法与提交规范见 [`CONTRIBUTING.md`](CONTRIBUTING.md)。

## 更多文档

| 你想… | 去哪 |
| :--- | :--- |
| 装扩展、按症状排障 | [`client/vscode/README.md`](client/vscode/README.md) |
| 不依赖编辑器验证服务端 | [`samples/README.md`](samples/README.md) |
| 接 Unity 桥接 | [`client/unity/README.md`](client/unity/README.md) |
| 用 Sublime | [`client/sublime/README.md`](client/sublime/README.md) |
| 理解每个功能模块怎么工作 | [`docs/module-contracts.md`](docs/module-contracts.md)（功能模块说明书） |
| 改代码：目录职责、想改 X 去哪、红线 | [`ARCHITECTURE.md`](ARCHITECTURE.md) |
| 项目为什么这样设计 | [`DESIGN.md`](DESIGN.md) |
| 参与贡献（环境 / 自检 / 提交 / 文档纪律） | [`CONTRIBUTING.md`](CONTRIBUTING.md) |
| 看版本变更 | [`client/vscode/CHANGELOG.md`](client/vscode/CHANGELOG.md) |

## 许可证

MIT，见 [`LICENSE`](LICENSE)。
