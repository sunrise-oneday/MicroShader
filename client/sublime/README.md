# Sublime Text 4 接入（声明式适配）

Sublime 侧不需要任何可执行代码：LSP 包以声明式配置拉起 UnityShaderLsp.exe。

## 安装步骤

1. 前置：安装 Sublime Text 4 的 [LSP 包](https://packagecontrol.io/packages/LSP)，
   以及本仓库随附的 `shaderlab.sublime-syntax` 对应的语法（见第 3 步）。
2. 把语言服务器放到固定位置，例如 `D:/Tools/MicroShader/UnityShaderLsp.exe`
   （`dxcompiler.dll` 必须与 exe 同目录；`dxil.dll` 可选）。
3. 把本目录的两个文件复制到 Sublime 的 **Packages/User** 目录
   （菜单 Preferences → Browse Packages → User；放 zip 根目录不会生效）：
   - `LSP-MicroShader.sublime-settings`
   - `shaderlab.sublime-syntax`
4. 编辑 `LSP-MicroShader.sublime-settings` 里的 `command`，改成第 2 步的 exe 实际路径。
   Windows 路径在 JSON 里请用正斜杠，避免转义问题。

## 交互差异（相对 VS Code）

- **Ctrl+点击 include 跳转是 VS Code 独有交互**；Sublime LSP 的 `lsp_open_link` 默认未绑定，
  需要在 keymap 里自行绑定，或用命令面板执行 LSP: Follow Link。
- 其余能力（诊断 / 补全 / F12 / 符号）由服务端提供，两端一致。

## 已知边界

- `selector: source.shaderlab` 依赖本包附带的 `shaderlab.sublime-syntax`；未安装时 LSP 不激活。