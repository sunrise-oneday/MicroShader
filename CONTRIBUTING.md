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

# 自检：必须带环境变量与 --urp，否则一批环境依赖用例会失败或跳过、退出码变成 1
$env:MICROSHADER_UNITY_PROJECT = '<你的 Unity 工程根>'
.\src\MicroShader.SelfTest\bin\Release\net10.0\MicroShader.SelfTest.exe --selftest --urp '<URP 包目录>'

# 只跑某几个套件（--filter 按子串匹配用例名，不区分大小写）
... --filter Bridge
... --list      # 列出全部用例名，总数以它为准
```

期望：除已知的环境敏感项外全部通过，退出码 0。总数以 `--list` 为准——本机实测 222 条、221 通过；
那 1 条失败是 `Dump.DiskFullFallback`：它向 `%TEMP%` 写探测文件来模拟磁盘不可写，被系统拒绝就会红，
属环境敏感项、与代码无关（干净的机器 / CI 环境通常全绿）。

不设 `MICROSHADER_UNITY_PROJECT`、不给 `--urp` 时，`Server.AotSmoke` 这类依赖真实工程的用例会失败、
`Tag.GoldenUrp` 这类会跳过，退出码 1——都是环境差异，先把环境配齐再判断谁是真的问题。

**不要用 `dotnet run` 跑自检**：本机上它会让一批写 `%TEMP%` 的用例假失败；直接运行构建出来的 exe（上面那行）没有这个问题。

改哪个模块、该跑哪组套件：看 [`ARCHITECTURE.md` 的模块速查表](ARCHITECTURE.md#2-模块速查表)——
每个模块的「自检」列就是 `--filter` 关键字；更细的验收口径在 `docs/module-contracts.md` 各模块一节里。

## 改代码前必读

[`ARCHITECTURE.md` §5 不可违反的约束](ARCHITECTURE.md#5-不可违反的约束) —— 那几条一旦破坏是发布级事故。
不确定代码在哪，先查 [§3 常见任务索引](ARCHITECTURE.md#3-常见任务索引)。

三条额外红线，每条都有真实事故：

- **绝不把虚拟路径喂给编译器**：`Packages/...` 这类映射出来的路径磁盘上并不存在，编译器解析不到 include
  时会中止整个编译单元，再叠加一条自造的误报。
- **绝不传 `-Wall` / `-WX` 这类激进告警开关**：URP 的私有 pragma 会把诊断刷满噪声。
- **URP 的类型、标签、合法 lightMode 值一律从现场已安装的包采集**（引擎已实现）；
  绝不把 URP 源码或符号表打包进仓库——随包分发是法律问题。

## 已知地雷（都有真实事故，动手前先看一眼）

- **两套坐标**：DXC 报的列是 UTF-8 字节偏移，LSP 要的是 UTF-16 字符列；行号上，DXC 报的已是源文件行号
  （`#line` 映射过，不要再映射一次），拿行去比对装配文本前还要换算成装配文本自己的行号
  （两者不相等——装配时在头部注入了宏）。过滤 pragma 噪声的那段逻辑在这上面出过真 Bug。
- **中文命令行传输不稳**：PowerShell 命令里内联中文（比如路径带中文的 git 命令）会静默失败、不报错。
  涉及中文路径/文本的操作改用 base64 或 8.3 短名传参。
- **偶发失败先复现、再归因**：先连续多跑几轮采样；注意区分「产品缺陷」和「依赖真实时钟的用例」——
  后者在机器被打满时会误红，与产品无关。
- **并行编辑会丢更新**：多 Agent / 多会话同时改同一个仓库时，旧快照可能把未提交的改动整体覆盖
  （文件时间还可能不变，制造「写成功」的假象）。规矩：改完立即读盘核验（文件时间 + 特征串）；
  发现被覆盖先停手，不要抢写。

## 提交规范

- 一个主题一笔提交。跨主题的文件（尤其是 `README.md`）按内容取舍。
- 提交前 `git diff --numstat` 核对行数：若远超实际改动，通常是行尾符被整体重写。
- 不入库的物料优先加 `.gitignore`，不要删用户文件。
- 消息用祈使句、说明**为什么**而非改了什么。示例：
  - `fix(coordination): OnlyTriviaChanged 补字符串状态，消除"真实改动被漏判"的盲区`
  - `docs: 自检基线改为「以 --list 为准」，避免总数一变就要满仓对齐`

## 文档纪律

- **负面结论（「补不出来」「不支持」「没用」）在采信前，必须先让测量工具本身过一遍自检**：
  探针脚本、测试台这类测量工具统称「装置」；「工具坏了」和「东西不行」看起来一模一样，
  本项目曾连续六次把前者当后者。细则见 [`docs/testing-guidelines.md`](docs/testing-guidelines.md)。
- 数字要能复现：能写成「运行 `--list` / 跑一遍就知道」的，就不要写死；确需写数字的，
  要说明它随环境可变，并同步改全仓引用（`README.md`、`DESIGN.md`、`docs/module-contracts.md` 等）。
- 日期化的实测记录不要改数字——要更正就在旁边补说明；改历史记录等于伪造证据。

### 证据放哪

查证类结论（尤其是**负面结论**）要留下可复跑的凭据。按性质走三个去处，不要新建目录：

- **缺陷调查与现场证据**：不单独留目录，随提交进 Git 历史（用 `git log` / `git show` 就能追溯当时的过程与数据）。
- **可复跑的探针脚本、基准台**：放 `probes/`。默认被 `.gitignore` 忽略；确实要入库的，
  在 `.gitignore` 给它加一条 `!probes/<名字>.mjs` 例外（现在的入库探针都是这么处理的，例子见 `probes/probe.mjs`）。
- **一次性辅助脚本**（不进构建、不被 `scripts/` 调用）：放 `tools/`。

四条规矩，都对应真实踩坑：

- **探针脚本必须参数化**：目标文件与程序路径走命令行参数，不硬编码本机路径；否则换台机器跑不起来，
  「可复现」是假的。
- **中间产物不算证据**：一次性的输入、临时输出都不要提交；要留的是「能复跑的脚本 + 一份读数」。
- **不要把对话记录原样入库**：复核结论整理成条目（结论 / 证据 / 如何复跑 / 未覆盖什么）再提交。
- **依赖仓库外部工具的脚本不入库**（比如只在某台机器上装过的驱动类工具）：
  在别人机器上必然失败，属于噪音而非证据。

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
