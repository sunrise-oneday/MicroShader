# 贡献指南

## 环境

- .NET SDK `10.0.401`（见 `global.json`，`dotnet --version` 应输出该版本）
- Node.js（仅 VS Code 扩展需要，见 `client/vscode/package.json` 的 `engines`）
- Windows x64（服务端与 DXC 是 Windows 原生二进制；纯文档改动不受此限）

`NuGet.config` 已清空包源，**构建可完全离线**。不要引入第三方 NuGet 包。

## 构建与自检

```powershell
# 构建
dotnet build MicroShader.sln -c Release

# 自检必须带环境变量，否则会 1 失败 1 跳过并返回退出码 1
$env:MICROSHADER_UNITY_PROJECT = '<你的 Unity 工程根>'
dotnet src\MicroShader.SelfTest\bin\Release\net10.0\MicroShader.SelfTest.dll --selftest --urp '<URP 包目录>'

# 只跑某几个套件
... --filter Bridge
... --list          # 列出全部 214 条用例名
```

期望：**214 / 214 / 0 / 0**，退出码 0。
裸跑（不设环境变量、不给 `--urp`）是 `214 / 212 / 1 / 1`、退出码 1 —— 失败项 `Server.AotSmoke`
与跳过项 `Tag.GoldenUrp` 都是环境依赖，不是缺陷。

## 改代码前必读

[`ARCHITECTURE.md` §5 不可违反的约束](ARCHITECTURE.md#5-不可违反的约束) —— 那几条一旦破坏是发布级事故。
不确定代码在哪，先查 [§3 常见任务索引](ARCHITECTURE.md#3-常见任务索引)。

## 提交规范

- 一个主题一笔提交。跨主题的文件（尤其是 `README.md`）按内容取舍。
- 提交前 `git diff --numstat` 核对行数：若远超实际改动，通常是行尾符被整体重写。
- 不入库的物料优先加 `.gitignore`，不要删用户文件。
- 消息用祈使句、说明**为什么**而非改了什么。示例：
  - `fix(coordination): OnlyTriviaChanged 补字符串状态，消除"真实改动被漏判"的盲区`
  - `docs: 自检基线口径 210 -> 212 全仓对齐`

## 文档纪律

- **负面结论在采信前必须先过装置自检**。见 `docs/testing-guidelines.md`——
  本项目在路线 1 评估中连续踩过六次「装置缺陷伪装成能力缺陷」。
- 数字要能复现。改自检基线时同步改：`README.md`、`DESIGN.md`、`docs/module-contracts.md`。
- 日期化的历史实测记录**保留原样并加编者注**，不要改数字——改历史记录等于伪造证据。

### 证据放哪

查证类结论（尤其是**负面结论**）要留下可复跑的凭据，但**不要新建第三种目录**。
仓库已有两处，按性质二选一：

| 性质 | 放哪 | 入库 |
| :--- | :--- | :-: |
| 缺陷现场、日志、截图、复核记录 | `docs/bugs/evidence/` | ✅ |
| 可复跑的探针脚本、基准台 | `probes/` | ⚠️ 默认忽略；脚本本身要入库时在 `.gitignore` 加 `!probes/<名字>.mjs` 例外 |

四条规矩，都来自 2026-10-06 那次整理的实际教训：

- **探针脚本必须参数化**：目标文件与 exe 走 argv，不硬编码本机路径。否则换台机器跑不起来，
  「可复现」是假的。
- **中间产物不算证据**：一次性的输入 spec、空数组、临时 dump 都不要入库，
  真正要留的是「能复跑的脚本 + 一份读数」。
- **不要把对话记录原样入库**：复核结论整理成条目（结论 / 证据 / 如何复跑 / 未覆盖什么）再提交。
- **依赖仓库外部工具的脚本不入库**（例如指向某个本机安装的 MCP 驱动的脚本）——
  它在别人机器上必然失败，属于噪音而非证据。

示例可参考 `docs/bugs/evidence/2026-10-06-pure-hlsl-index-review.md` 与 `probes/probe.mjs`。

## 扩展开发

```powershell
# 改扩展 TypeScript：按 F5 开 Extension Development Host（见 .vscode/launch.json）
# 只改了服务端：重新发布 + 按哈希同步，不重装扩展
pwsh -File scripts\publish-aot.ps1
pwsh -File scripts\sync-extension-bin.ps1
# 出可分发包
pwsh -File scripts\package-vsix.ps1 -Install
```

改 `scripts/publish-aot.ps1` 时**必须同步改 `.cmd` 与 `.sh`**，三套入口等价。
