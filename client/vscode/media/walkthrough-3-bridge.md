## （可选）接入 Unity 编辑器桥接

**不装也能正常用**。装上之后，诊断会用上 Unity 编辑器**实测**的全局宏与关键字状态，
而不是从工程文件推断出来的状态。

1. 把 `client/unity/MicroShaderBridge/` 整个文件夹复制到工程的任意 `Editor` 目录下
   （例如 `Assets/Editor/MicroShaderBridge/`）。
2. 回到 Unity 等它编译完成，菜单栏会出现 MicroShader 桥接开关。
3. 桥接只绑 `127.0.0.1` 回环，不开端口、不联网、不需要管理员权限。

装与不装，正常情况下的诊断结果应当**逐条相同** —— 桥接只是让宏状态更贴近编辑器实测。
安装细节与发现文件 schema 见 `client/unity/README.md`。
