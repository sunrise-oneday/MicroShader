# samples/ — 手工分析用的示例 shader

三个文件：**一个完全正确的基线**，两个**故意写错**的对照组。
不需要 VS Code，也不需要 Unity 在跑 —— 直接用自检 CLI 的 `--analyze`。

| 文件 | 用途 | 期望结果 |
| :--- | :--- | :--- |
| `HelloShader.shader` | **零错误基线**：最简 URP 可编译 shader | **诊断 0 条**，退出码 0 |
| `BrokenShader.shader` | 故意写错 5 处（包路径 / `RenderPipeline` / `LightMode` / 函数参数 / 未声明变量 / 返回类型） | 诊断 1 条 `MS0001` |
| `BrokenShader_IncludeFixed.shader` | 只修好 include 路径，其余错误保留 | 诊断 4 条（3 Error + 1 Warning） |

## 怎么跑

```powershell
# 构建一次
dotnet build MicroShader.sln -c Release

# --project 指向 Unity 工程根（即含 ProjectSettings/ProjectVersion.txt 的那一层）
# --urp    指向该工程里的 URP 包目录
dotnet src\MicroShader.SelfTest\bin\Release\net10.0\MicroShader.SelfTest.dll `
  --analyze samples\HelloShader.shader `
  --project 'D:\Program Files\U3D\FamiliarWithURP' `
  --urp    'D:\Program Files\U3D\FamiliarWithURP\Library\PackageCache\com.unity.render-pipelines.universal@14.0.12'
```

## 期望输出

### `HelloShader.shader` —— 这是"工具生效了"的判据

```text
源文件   : ...\samples\HelloShader.shader
Unity 工程: D:\Program Files\U3D\FamiliarWithURP
VFS 索引 : 84 个包（22.6 ms）
Unity 桥接: 未绑定 Unity（发现文件不存在（Unity 未运行，或桥接脚本未安装/被禁用））
DXC      : 1.8
切片     : 1 个 Pass / 0 个共享块 / 0 条变体声明 / 切片诊断 0 条

装配     : Pass 单元 1 → 阶段派发 2（编译 2 / 抑制 0 / 中止 0）
编译     : 原始诊断 0 条，丢弃 pragma-message 0 条，列降级 0 条，内部错误 0 条
耗时     : 编译器 81.4 ms，分析总计 67.5 ms，进程墙钟 195.3 ms

── 诊断：无 ────────────────────────────────────────────
```

**三个数必须同时成立**，说明整条链路都通了：

| 看什么 | 说明 |
| :--- | :--- |
| `VFS 索引 : 84 个包` | 找到了 Unity 工程根，且包拓扑解析成功 |
| `阶段派发 2（编译 2 / 中止 0）` | 顶点 + 片元两个阶段都真的送进了 DXC |
| `── 诊断：无 ──` | 装配、锚定、归属、降噪全程没有误报 |

### `BrokenShader.shader` —— 对照组

```text
装配     : Pass 单元 1 → 阶段派发 2（编译 0 / 抑制 0 / 中止 2）
编译     : 原始诊断 0 条，...
── 诊断 1 条 ──────────────────────────────────────
    32:23   [Error] MS0001 依赖缺失：无法解析 #include
             "Packages/com.unity.render-pipelines.universial/ShaderLibrary/Core.hlsl" —— 虚拟路径 Packages/ 下的包
             'com.unity.render-pipelines.universial' 不在当前工程的包拓扑中
```

注意 `编译 0 / 中止 2`：**include 解析失败时就不做原生编译**，避免把虚拟路径喂给 DXC
产出二次误报。这是有意设计，不是缺陷。

### `BrokenShader_IncludeFixed.shader` —— 看归属映射在干什么

```text
装配     : Pass 单元 1 → 阶段派发 2（编译 2 / 抑制 0 / 中止 0）
编译     : 原始诊断 18 条，丢弃 pragma-message 0 条，列降级 0 条，内部错误 0 条
── 诊断 4 条 ──────────────────────────────────────
    62:56   [Warning] implicit truncation of vector type [-Wconversion]
    65:24   [Error] use of undeclared identifier '_MainTex_ST'
    74:35   [Error] use of undeclared identifier '_MainTex'
    77:24   [Error] cannot initialize return object of type 'vector<half, 4>'
             with an lvalue of type 'vector<float, 3>'
```

**DXC 报了 18 条，最终只呈现 4 条** —— 其余 14 条来自 `#include` 进来的第三方头文件，
归属阶段把它们映射/丢弃了。行号也从 URP 头文件内部搬回了本文件的 62/65/74/77 行。

## 在编辑器里怎么用这三个文件

装好扩展后打开 `HelloShader.shader`，可以逐项验证编辑体验：

| 操作 | 位置 | 期望 |
| :--- | :--- | :--- |
| F12 | 光标放在 `TransformObjectToHClip` 上 | 跳进 URP 的 `SpaceTransforms.hlsl` |
| 补全 | `IN.` 之后 | 弹出 `positionOS` 等成员 |
| 补全 | `_BaseColor` 处 | 弹出 `CBUFFER_START(UnityPerMaterial)` 里声明的字段 |
| 悬停 | `Core.hlsl` 上 | 显示 include 解析结果 |

打开 `BrokenShader.shader` 则应看到问题面板里标红的 `#include` 行（MS0001）与
两个拼错的 `Tags`。

## 排查

下表全部来自本机实测（`D:\Program Files\U3D\FamiliarWithURP` + URP 14.0.12）。

| 现象 | 原因与确认 |
| :--- | :--- |
| `VFS 索引 : 0 个包`，且 `Unity 桥接: 未启用（未能在 ... 找到 ProjectSettings/ProjectVersion.txt）` | `--project` 指的不是 Unity 工程根 —— 要指**含 `ProjectSettings/ProjectVersion.txt` 的那一层**。此时诊断会报 MS0001「包不在拓扑中」，但**退出码仍是 0** |
| 不给 `--project` 居然也能跑 | 服务端有兜底探测（会读 Unity Hub 的编辑器/项目列表）。本机实测自动找到了 `D:\Program Files\U3D\NewWorld`。**别依赖它** —— 显式给 `--project` 才是确定行为 |
| `Unity 桥接: 未绑定 Unity（发现文件不存在 ...）` | **正常现象**。桥接是可选增强，不装也能用，见 `client/unity/README.md` |
| 桥接一直是「未绑定」 | Unity 没运行，或桥接包没复制进工程的 `Editor` 目录 |
| 诊断说某包「不在当前工程的包拓扑中」，但你确信装了 URP | 没给 `--urp`，或路径不是工程里 `Library/PackageCache/` 下的那个包目录 |
| `文件不存在: <路径>`，**退出码 2** | 这是 `--analyze` 唯一会返回非 0 的情况：目标文件路径不存在。**有诊断条目时退出码仍然是 0**（三个示例实测都是 0） |
