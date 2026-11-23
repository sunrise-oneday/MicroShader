## 验证补全与跳转

1. 在一个已知类型的变量后敲 `.` —— 例如 URP 的 `Input` / `Surface`，应弹出成员候选。
2. 光标停在 `TransformObjectToHClip` 上按 `F12` —— 应跳到 `SpaceTransforms.hlsl`。
3. 按 `Ctrl+Shift+O` 打开大纲，应看到 `Properties` / `SubShader` / `Pass` 的层级结构。

**补全一条候选都不出？** 最常见的原因是当前窗口没落在 Unity 工程里：
执行「复制诊断信息」，看「解析到的 Unity 工程根」是不是 `<未找到>`。
是的话用 VS Code 打开 Unity 工程根目录，而不是只把单个 .shader 拖进来。
