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
.\src\MicroShader.SelfTest\bin\Release\net10.0\MicroShader.SelfTest.exe `
  --analyze samples\HelloShader.shader `
  --project 'D:\Program Files\U3D\FamiliarWithURP' `
  --urp    'D:\Program Files\U3D\FamiliarWithURP\Library\PackageCache\com.unity.render-pipelines.universal@14.0.12'
```

上面两条路径是示例，换成你自己 Unity 工程的路径。

## 期望输出

下面输出里的数字（包数、毫秒数等）都是本机快照，**仅作示例**——换一台机器、换一个工程就会不同；对照时看结构即可，不必逐字相等。

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

这些行是工具内部流水线的叫法，逐行翻译一下：

| 看什么 | 它在告诉你什么 |
| :--- | :--- |
| `VFS 索引 : 84 个包` | VFS = 工具在内存里给 Unity 工程建的一份文件索引，用来解析 include 路径。扫到 84 个包，说明工程根指对了、包与包的依赖也解析成功；数量随工程而变 |
| `DXC : 1.8` | 实际调用到的 HLSL 编译器版本 |
| `切片 : 1 个 Pass …` | 先把 shader 拆成可单独编译的单元：这份只有 1 个 Pass，共享块 / 变体声明 / 切片诊断都是 0 |
| `装配 → 阶段派发 2` | 装配 = 把切片组装成待编译的任务；阶段派发 = 每个阶段单独送进编译器。`编译 2` 表示顶点、片元两个阶段都真的编译了；`抑制` / `中止` 分别是「因故跳过」和「因故提前取消」，正常都是 0 |
| `编译 : 原始诊断 0 条 …` | 编译器层面没有报出任何问题；`内部错误 0 条` 表示工具自身也没有出错 |
| `耗时 : …` | 编译器、分析各花了多久——具体数字仅为示例，换机器会变 |
| `── 诊断：无 ──` | 最终呈现给你的问题列表是空的 |

**「工具生效了」的三条判据必须同时成立**：工程根被认出来（VFS 索引非 0）、两个阶段都真的编译（编译 2 / 中止 0）、从切片到诊断过滤全程没有误报（诊断无）。

### `BrokenShader.shader` —— 对照组

```text
装配     : Pass 单元 1 → 阶段派发 2（编译 0 / 抑制 0 / 中止 2）
编译     : 原始诊断 0 条，...
── 诊断 1 条 ──────────────────────────────────────
    32:23   [Error] MS0001 依赖缺失：无法解析 #include
             "Packages/com.unity.render-pipelines.universial/ShaderLibrary/Core.hlsl" —— 虚拟路径 Packages/ 下的包
             'com.unity.render-pipelines.universial' 不在当前工程的包拓扑中
```

注意 `编译 0 / 中止 2`：`#include` 解析失败时，两个阶段都会被提前中止、不送进编译器——这样就不会把虚拟路径当真实文件喂给 DXC，产出二次误报。这是有意设计，不是缺陷。

### `BrokenShader_IncludeFixed.shader` —— 看诊断怎么过滤、行号怎么还原

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

`原始诊断 18 条` 是编译器展开 `#include` 后看到的问题总数。**最终只呈现 4 条**——其余 14 条来自 URP 头文件内部，工具会逐条判断来源：不是你写的代码就不呈现；是你写的，行号也从展开后的位置搬回本文件（62/65/74/77 行）。

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

下表每条都实测确认过，按现象查。

| 现象 | 原因与确认 |
| :--- | :--- |
| `VFS 索引 : 0 个包`，且 `Unity 桥接: 未启用（未能在 ... 找到 ProjectSettings/ProjectVersion.txt）` | `--project` 指的不是 Unity 工程根 —— 要指**含 `ProjectSettings/ProjectVersion.txt` 的那一层**。此时诊断会报 MS0001「包不在拓扑中」，但**退出码仍是 0** |
| 不给 `--project` 居然也能跑 | 服务端有兜底探测（会读 Unity Hub 的编辑器/项目列表），本机实测确实能自动找到已登记的工程。**别依赖它** —— 显式给 `--project` 才是确定行为 |
| `Unity 桥接: 未绑定 Unity（发现文件不存在 ...）` | **正常现象**。桥接是可选增强，不装也能用，见 `client/unity/README.md` |
| 桥接一直是「未绑定」 | Unity 没运行，或桥接包没复制进工程的 `Editor` 目录 |
| 诊断说某包「不在当前工程的包拓扑中」，但你确信装了 URP | 没给 `--urp`，或路径不是工程里 `Library/PackageCache/` 下的那个包目录 |
| `文件不存在: <路径>`，**退出码 2** | 这是 `--analyze` 唯一会返回非 0 的情况：目标文件路径不存在。**有诊断条目时退出码仍然是 0**（三个示例实测都是 0） |
