# Sublime Text 4 接入

Sublime 侧**不需要任何可执行代码** —— LSP 包以声明式配置拉起同一个 `UnityShaderLsp.exe`。
协议层与 VS Code 完全一致（同一个 exe、同一套 LSP 能力），差别只在客户端的交互外壳。

## 最快路径：一个自包含的包（对位 VSIX）

```powershell
pwsh -File scripts\package-sublime.ps1 -Install
```

产出并安装 **`dist/LSP-MicroShader.sublime-package`**（8.8 MB）—— 这就是 Sublime 版的 VSIX：

```text
LSP-MicroShader.sublime-package   ← 一个文件，装进 Installed Packages/ 就完事
├─ LSP-MicroShader.sublime-settings   服务器配置（command 用 $storage_path 模板，无硬编码路径）
├─ plugin.py                          LspPlugin 子类：包加载时把二进制释放到 Package Storage
├─ shaderlab.sublime-syntax           语法（提供 source.shaderlab scope，selector 靠它匹配）
├─ Default.sublime-keymap             F12 定义跳转 / Ctrl+Alt+Enter 跟随 include
└─ bin/  UnityShaderLsp.exe + dxcompiler.dll + dxil.dll   服务端本体
```

**为什么能自包含**：LSP 支持 `LSP-*` 命名的 helper 包自带服务器配置（按包名推导配置文件名），
且 `command` 支持 `$storage_path` 等模板变量。所以包里没有任何绝对路径 ——
`plugin.py` 在包加载时把二进制释放到 `<Package Storage>/LSP-MicroShader/bin/`
（Windows 上 `Package Storage` 位于 `%LOCALAPPDATA%\Sublime Text\` —— 注意不是 `%APPDATA%`），
配置里写 `$storage_path/MicroShader/bin/UnityShaderLsp.exe` 即可。

| 参数 | 用途 |
| :--- | :--- |
| `-Install` | 装进 `Installed Packages/`（不加则只产出包文件） |
| `-SublimeDir` / `-DataDir` | 手工指定安装目录 / 数据目录（默认自动探测：便携版 `Data/` 优先） |

> 装完**必须完全退出并重开 Sublime** —— 插件宿主只在启动时加载包，
> 关掉窗口再打开不算（后台进程还在）。

## 另一条路径：写 User 配置（与上面的包互斥）

```powershell
pwsh -File scripts\install-sublime.ps1
```

这条路不装包，而是把 `microshader` 合并进 `Packages/User/LanguageServers.sublime-settings`
（只动自己那段，其它服务器配置与注释原样保留），并把语法与键位复制到 `Packages/User/`。

**两条路只能选一条。** 都装会让 LSP 同时启动两份服务器会话（配置名一个是 `MicroShader`、
一个是 `microshader`），表现为诊断与补全成对重复出现。两个脚本都做了互斥检测：
检测到 helper 包已装时 `install-sublime.ps1` 会拒绝执行并说明原因。

什么时候用这条路：不想让 Sublime 加载自定义插件代码，或需要把配置写进项目文件时。

## 与 VS Code 的能力对照

| 能力 | VS Code | Sublime Text 4 |
| :--- | :--- | :--- |
| 诊断（编译错误 + ShaderLab 标签校验） | ✅ 问题面板 | ✅ 同源；`Ctrl+Alt+M` 开面板 |
| 补全（成员 / 向量分量 / include 链 / 内置 uniform） | ✅ 自动弹出 | ✅ `Ctrl+空格` 触发，LSP 也会自动弹 |
| F12 定义跳转（跨 include 穿透） | ✅ 默认可用 | ⚠️ **需要键位覆盖**，见下 |
| `#include` 超链接 | ✅ Ctrl+点击 | ⚠️ 只能键盘触发（LSP 不提供鼠标绑定） |
| 文档大纲 / 面包屑 | ✅ 大纲视图 | ✅ `Ctrl+Shift+O` 转到符号 |
| 悬停说明 / 参数提示 | ✅ | ✅ 参数提示 `Ctrl+Alt+空格` |
| 查找引用 | ✅ | ✅ `Shift+F12` |
| 状态栏指示 | 专属项 + tooltip（工程根 / 服务端路径） | 只有状态栏左侧的服务器名，无 tooltip |
| 一键看日志 / 复制诊断信息 | ✅ 两个命令 | ❌ 用 `Ctrl+Shift+P` → `LSP: Troubleshoot Server` |
| 首次上手引导（walkthrough） | ✅ | ❌ |
| 安装形态 | 一个 vsix | 一个 sublime-package（本目录的脚本产出） |

### 为什么 F12 要单独覆盖

`f12` 在 Sublime 内置的 `Default` 包里绑的是原生 `goto_definition` —— 它不认识语言服务器，
在 shader 文件里永远找不到目标。而 LSP 包自带键位表里，`lsp_symbol_definition` 的 `f12`
绑定**是注释掉的**。所以这里提供 `Default.sublime-keymap` 补上：

| 键位 | 命令 | 说明 |
| :--- | :--- | :--- |
| `F12` | `lsp_symbol_definition` | 带 `fallback: true`，LSP 找不到时回退内置跳转；`context` 限定只在服务器宣告了 `definitionProvider` 时生效，不影响非 LSP 文件 |
| `Ctrl+Alt+Enter` | `lsp_open_link` | 跟随 `#include`，对位 VS Code 的 Ctrl+点击。选它是因为它只在替换面板里有绑定（带 context 限定），编辑器里空闲；`ctrl+enter` 已被内置 Add Line 宏占用 |

## 排查

| 现象 | 原因 |
| :--- | :--- |
| 状态栏左侧不出现 `MicroShader` | ① 没完全重启 Sublime（最常见）；② LSP 包没装；③ 语法包没生效导致 scope 不是 `source.shaderlab` —— 打开 `.shader` 看右下角语法名是不是 Unity ShaderLab |
| 诊断和补全都成对重复出现 | 两条路都装了（helper 包 + User 配置）。删掉其一 |
| 服务器名出现但立刻消失 | 通常是 exe 路径错，或 `dxcompiler.dll` 没跟 exe 同目录。用 `LSP: Troubleshoot Server` 看启动日志，并确认 `<Package Storage>\LSP-MicroShader\bin\` 里有三个文件（Windows 上 Package Storage 在 `%LOCALAPPDATA%\Sublime Text\`，不是 `%APPDATA%`） |
| F12 没反应 | 键位文件没生效，或没重启 Sublime |
| 补全一条都没有 | 与 VS Code 同根因：服务端没解析到 Unity 工程根。服务端会自行探测（读 Unity Hub 项目列表）；要固定就改用 User 配置那条路并传 `--project` |
| 诊断报 `MS0001` 包路径未解析 | 工程根不对，或该工程没装 URP |

## 已知边界

- **必须完全重启 Sublime**：插件宿主只在启动时加载包，关窗口不算。
- **Ctrl+点击 include 做不到**：Sublime 鼠标键位需要插件提供对应 command，LSP 没有提供。用 `Ctrl+Alt+Enter` 代替。
- **高亮是轻量自写版**：Sublime 不自带 HLSL 语法（54 个内置包里没有），HLSL 代码块内只有
  通用关键词着色，细度不如 VS Code 的 TextMate 语法。
- **helper 包会执行 Python 代码**：`plugin.py` 只有两个职责 —— 释放二进制、注册插件，
  共 61 行，无网络访问。不想让它执行就走「User 配置」那条路。
