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
... --list          # 列出全部 212 条用例名
```

期望：**212 / 212 / 0 / 0**，退出码 0。
裸跑（不设环境变量、不给 `--urp`）是 `212 / 210 / 1 / 1`、退出码 1 —— 失败项 `Server.AotSmoke`
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
