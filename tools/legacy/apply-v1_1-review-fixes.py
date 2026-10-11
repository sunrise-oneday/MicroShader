# -*- coding: utf-8 -*-
# 一次性脚本：把 v1.1 评审修正写进两份设计文档。执行后即删除。
import sys

MAIN = r"E:\Com\BuildingShaderTool\工具整体设计文档.md"
DETAIL = r"E:\Com\BuildingShaderTool\DetailDesign\核心编译探针与宏注入的详细设计.md"

# ---------- 主文档修正 ----------
main_edits = []

# E0: 恢复被误删的版本行并升版（上一轮的占位 Edit 把该行删成了空行）
main_edits.append((
    '诊断语言服务器**\n\n\n* **目标管线**',
    '诊断语言服务器**\n\n* **文档版本**：v1.1.0（2026-09-25 详细设计评审修正：新增 ADR-009~013、附录A 增补方言行、契约3 收窄为纯 push、NFR-4 口径可测化、VFS 优先级重排）\n* **目标管线**'
))

# E1: 模块1 —— 增加 HLSLINCLUDE 共享块识别 + 对齐 CGPROGRAM 口径
main_edits.append((
    '* 遇到 `CGPROGRAM ... ENDCG` 时，显式标记 `IsLegacyCg = true` 并直接跳过，防止老式内置管线代码污染 DXC 编译器。',
    '* 遇到 `HLSLINCLUDE ... ENDHLSL`（及遗留 `CGINCLUDE ... ENDCG`）时，标记为 SubShader 级共享块：其内容必须**拼入同一 SubShader 内每个 Pass 编译单元的头部**（携带独立的 `#line` 锚），否则 Pass 内引用共享符号会被误报 `undeclared identifier`（URP 官方 Sprite-Mask、Particles 系列 shader 实测使用该结构）。\n* 遇到 `CGPROGRAM ... ENDCG` 时，显式标记 `IsLegacyCg = true`；若块内未引用现代 URP 核心头文件（Core.hlsl 等）则主动抑制 DXC 派发，防止老式内置管线代码引发大面积误报。'
))

# E2: 模块2 —— VFS 四级优先级重排（消除"第2级却标优先级最高"的矛盾）
main_edits.append((
    '├── 1. [manifest.json 本地映射]: 检查是否存在 "file:../LocalPackages/com.xxx" 相对引用\n├── 2. [Embedded Package 检查]: 检查工程根目录下是否存在实体目录 "Packages/com.xxx/" (优先级最高)\n├── 3. [PackageCache 缓存扫描]: 命中 "Library/PackageCache/com.xxx@版本号/ShaderLibrary/Core.hlsl" (常规兜底)\n└── 4. [Legacy CGIncludes 探测]: 遇 "UnityCG.cginc" 时，通过注册表或环境变量重定向至 "Editor/Data/CGIncludes"',
    '├── 1. [Embedded Package 检查]: 工程根目录下存在实体目录 "Packages/com.xxx/" 时直接命中（与 Unity 语义一致：实体包优先级最高，永远覆盖缓存）\n├── 2. [manifest.json 本地映射]: 解析 "file:../LocalPackages/com.xxx" 等本地相对引用\n├── 3. [PackageCache 缓存扫描]: 命中 "Library/PackageCache/com.xxx@版本号/ShaderLibrary/Core.hlsl" (常规兜底)\n└── 4. [Legacy CGIncludes 探测]: 遇 "UnityCG.cginc" 时，重定向至本机 Unity 编辑器的 "Editor/Data/CGIncludes"'
))

# E3: 模块3 —— 增补 include_with_pragmas 降级重写规则
main_edits.append((
    '* **技术价值**：原生的 `#line` 指令消除了任何“编译器报错行号与原文件绝对行号不一致”的计算 Bug。',
    '* **技术价值**：原生的 `#line` 指令消除了任何“编译器报错行号与原文件绝对行号不一致”的计算 Bug。\n* **Unity 私有指令降级重写**：`#include_with_pragmas "..."` 是 Unity 扩展指令（在 include 的同时把目标文件内顶层 `#pragma` 提升到当前着色器，如 DOTS.hlsl 的 `multi_compile _ DOTS_INSTANCING_ON`），DXC 视其为非法预处理指令（实测 URP 14 官方 `Lit.shader` 含 10 处）。装配时必须重写为普通 `#include "..."`；放弃的 pragma 提升仅影响 DOTS/RenderingLayers 等编辑器期变体，对默认变体检查零影响。其余 Unity 私有 `#pragma`（`target`/`multi_compile` 等）在 DXC 下仅触发 unknown-pragma 警告，由 `-no-warnings` 抑制，无需剥离。'
))

# E4: 模块6 —— R3 调度代码：废弃合并流统一 Drop，改为防抖 Switch + 保存 Sequential
main_edits.append((
    '''        // 2.5s 防抖流
        var debouncedTyping = _typingSubject
            .Debounce(TimeSpan.FromMilliseconds(2500))
            .Select(doc => (Doc: doc, FullScan: false));

        // 保存立即触发流 (强制穿透，不防抖)
        var immediateSave = _saveSubject
            .Select(doc => (Doc: doc, FullScan: true));

        // 合并事件流，采用 Drop 策略丢弃积压任务
        Observable.Merge(debouncedTyping, immediateSave)
            .SubscribeAwait(async (request, ct) =>
            {
                var targetVersion = request.Doc.Version;
                var diagnostics = await ExecuteCompileAsync(request.Doc, request.FullScan, ct);

                // 版本仲裁：如果此任务计算完成时，已有更新版本的文档被处理，则直接丢弃结果
                if (targetVersion < Volatile.Read(ref _latestProcessedVersion))
                {
                    return; // 丢弃陈旧数据，防止新红线被旧结果覆盖
                }

                Volatile.Write(ref _latestProcessedVersion, targetVersion);
                await PublishToClientAsync(request.Doc.FileUri, diagnostics);
            }, AwaitOperation.Drop);
    }
}''',
    '''        // 打字防抖流：2.5s 无新输入才触发；编译途中来了新版本则取消旧任务跑新的
        // （R3 官方语义：Switch = 取消正在运行的异步方法，执行新值）
        _typingSubject
            .Debounce(TimeSpan.FromMilliseconds(2500))
            .SubscribeAwait(async (doc, ct) =>
                await RunAndPublishAsync(doc, fullScan: false, ct),
                AwaitOperation.Switch);

        // 保存流：Ctrl+S 必达，排队执行，绝不丢弃
        // （R3 官方语义：Sequential = 所有值排队，依次等待异步方法完成。
        //   严禁对保存使用 Drop：编译进行中按 Ctrl+S 会被静默丢弃，
        //   “保存必出全量结果”的预期落空）
        _saveSubject
            .SubscribeAwait(async (doc, ct) =>
                await RunAndPublishAsync(doc, fullScan: true, ct),
                AwaitOperation.Sequential);
    }

    private async Task RunAndPublishAsync(SourceShaderDocument doc, bool fullScan, CancellationToken ct)
    {
        var targetVersion = doc.Version;
        var diagnostics = await ExecuteCompileAsync(doc, fullScan, ct);

        // 版本仲裁：如果此任务计算完成时，已有更新版本的文档被处理，则直接丢弃结果
        if (targetVersion < Volatile.Read(ref _latestProcessedVersion))
        {
            return; // 丢弃陈旧数据，防止新红线被旧结果覆盖
        }

        Volatile.Write(ref _latestProcessedVersion, targetVersion);
        await PublishToClientAsync(doc.FileUri, diagnostics);
    }
}'''
))

# E5: 契约3 —— 删除 diagnosticProvider（纯 push，避免推拉双轨重复编译）
main_edits.append((
    '''    "textDocumentSync": {
      "openClose": true,
      "change": 1 // TextDocumentSyncKind.Full (全量文本替换，严禁使用增量同步)
    },
    "diagnosticProvider": {
      "interFileDependencies": true,
      "workspaceDiagnostics": false
    }''',
    '''    "textDocumentSync": {
      "openClose": true,
      "change": 1 // TextDocumentSyncKind.Full (全量文本替换，严禁使用增量同步)
    }
    // 诊断一律走服务端 push（textDocument/publishDiagnostics）。
    // 刻意不声明 diagnosticProvider（LSP 3.17 拉取模式）：声明后 VS Code 会改发
    // textDocument/diagnostic 拉取请求，与推模式双轨并行，同一份代码被编译两次。'''
))

# E6: NFR-4 口径修正为可验收指标
main_edits.append((
    '  * 状态机切片、文本预处理及 DXC 传参热路径全面采用 `Span<char>`、`StringPool` 与 `ZString`，打字热路径上 .NET GC Gen 0 / 1 / 2 触发次数恒为 **0 次**。',
    '  * 诊断热路径（切片、装配、DXC 传参）全面采用 `Span<char>`、`StringPool` 与 `ZString`，单次请求托管堆分配目标 **< 8 KB** 且不随文档大小线性增长；LSP 协议层（JSON 反序列化、全量文本接收）的必然分配不计入该指标，但稳态打字 60 秒内 **Gen1 / Gen2 回收次数必须为 0**。（原表述"GC 恒为 0"物理不可达——协议层必然产生分配，已修正为可验收口径。）'
))

# E6b: NFR-5 补预加载闭环
main_edits.append((
    '  * 随包必须附带且仅附带对应平台的 `dxcompiler.dll` 和 `dxil.dll`，完全脱离对系统盘 `System32` 任何 DLL 文件的搜寻依赖。',
    '  * 随包必须附带且仅附带对应平台的 `dxcompiler.dll` 和 `dxil.dll`，完全脱离对系统盘 `System32` 任何 DLL 文件的搜寻依赖。进程启动时以绝对路径预加载双 DLL（见 ADR-012）。'
))

# E7: ADR-009~013 追加
main_edits.append((
    '''* **ADR-008：全量同步（SyncKind.Full）与文档版本号仲裁**
  * *判定理由*：在 2.5s 防抖节奏下放弃复杂的增量文本计算（Kind=2），全量同步保证状态绝对一致；利用 `Version` 递增原子比对丢弃陈旧诊断包，解决多线程任务乱序覆盖问题。

---''',
    '''* **ADR-008：全量同步（SyncKind.Full）与文档版本号仲裁**
  * *判定理由*：在 2.5s 防抖节奏下放弃复杂的增量文本计算（Kind=2），全量同步保证状态绝对一致；利用 `Version` 递增原子比对丢弃陈旧诊断包，解决多线程任务乱序覆盖问题。
* **ADR-009：`#include_with_pragmas` 降级重写为普通 `#include`**
  * *判定理由*：该指令是 Unity 私有扩展（include 并提升目标文件顶层 `#pragma`），DXC 视为非法预处理指令；实测 URP 14 官方 `Lit.shader` 含 10 处，不处理则真实 URP shader 全线误报。重写仅丢失 DOTS/RenderingLayers 变体提升，对 MVP 默认变体检查零影响。
* **ADR-010：`HLSLINCLUDE` 共享块拼入所有 Pass 编译单元**
  * *判定理由*：Unity 语义是把块内容注入同一 SubShader 的每个 `HLSLPROGRAM`；若按孤立块独立编译，Pass 内引用共享符号会误报 `undeclared identifier`。拼装时共享块与 Pass 各带独立 `#line` 锚，跨 Pass 合并诊断时按（行号, 消息）去重。
* **ADR-011：聚焦 Pass 的探针行来源 = 服务端行级 Diff**
  * *判定理由*：`SyncKind.Full` 的 `didChange` 不携带光标/编辑范围，CursorLine 无从获得。服务端对新旧全量文本做行级 Diff，取首个改动行定位聚焦 Pass——编辑器无关，VS Code / Sublime 前端均零额外代码；客户端自定义光标通知列为可选增强，不进 MVP。
* **ADR-012：原生 DLL 解析 = 启动时绝对路径预加载**
  * *判定理由*：`NativeLibrary.SetDllImportResolver` 仅对本程序集的 DllImport 生效，无法约束 Vortice 等第三方库内部的 DllImport；而 Win32 约定下同名模块一旦驻留进程，后续任何按名 `LoadLibrary` 均命中已加载模块。进程启动时用绝对路径 `NativeLibrary.Load` 预加载随包 `dxcompiler.dll`/`dxil.dll`，一步管住自家与第三方绑定库。
* **ADR-013：首个工程动作 = DXC AOT 探针（Spike），通过后才写业务模块**
  * *判定理由*：Vortice.Dxc 在 .NET 10 Native AOT 下的表现是全项目最大未知（官方 CHANGELOG 声明 "Support trimming and AOT"，但以本机实测为准）。先用最小 AOT 控制台 + 真实 dxcompiler.dll 编译一段含故意错误的 HLSL 验证全链路；若翻车则回退手写出站 vtable 绑定（仅需 `IDxcCompiler3`/`IDxcUtils`/`IDxcResult`/`IDxcBlobUtf8` 四个接口的纯出站调用，无回调，约百行），上层模块零改动。

---'''
))

# E8: 附录A 增补 write-to-CB / never_use_dxc 方言行
main_edits.append((
    '| **隐式类型收缩** | 允许 `float2 a = float4(1,2,3,4);` 静默通过 | 本项目通过 `-no-warnings` 静默忽略此 Warning | 建议显式书写 `float2 a = float4(...).xy;` 保持代码健壮 |',
    '| **隐式类型收缩** | 允许 `float2 a = float4(1,2,3,4);` 静默通过 | 本项目通过 `-no-warnings` 静默忽略此 Warning | 建议显式书写 `float2 a = float4(...).xy;` 保持代码健壮 |\n| **常量缓冲区回写 (write-to-CB)** | 老式 Particle shader 允许对 cbuffer 成员赋值 | DXC 不支持该语法直接报错；Unity 官方对这类 shader 标注 `#pragma never_use_dxc` 主动回避 DXC | 改造为 cbuffer 只读；本工具检测到 `#pragma never_use_dxc` 时在诊断中附注“该 Pass 可能含 DXC 不兼容方言” |'
))

# ---------- 详设文档修正 ----------
detail_edits = []

# D1: 组件1 增加 HLSLINCLUDE 识别
detail_edits.append((
    '  * **必须同时识别 `HLSLPROGRAM ... ENDHLSL` 与 `CGPROGRAM ... ENDCG`**。',
    '  * **必须同时识别 `HLSLPROGRAM ... ENDHLSL` 与 `CGPROGRAM ... ENDCG`**。\n  * **必须识别 `HLSLINCLUDE ... ENDHLSL`（及遗留 `CGINCLUDE ... ENDCG`）共享块**：记录其物理行区间与原文内容，供装配器拼入同一 SubShader 内每个 Pass 编译单元的头部（URP 官方 Sprite-Mask、Particles 系列 shader 实测使用该结构）。'
))

# D2: 组件2 增加 include_with_pragmas 重写 + 共享块拼装
detail_edits.append((
    '  * 将 `#include "Packages/..."` 重写为物理真实路径。\n  * 注入平台保底宏、变体首项宏，并写入 `#line` 坐标锚定指令。',
    '  * 将 `#include "Packages/..."` 重写为物理真实路径。\n  * 将 Unity 私有指令 `#include_with_pragmas "..."` 降级重写为普通 `#include "..."`（DXC 视其为非法预处理指令；实测 URP 14 `Lit.shader` 含 10 处，不处理则真实 URP shader 全线误报）。\n  * 若存在 `HLSLINCLUDE` 共享块，将其内容拼装在 Pass 代码之前，并为共享块与 Pass 块各自写入独立的 `#line` 锚（Pass 级 `#line` 行号 = 该 Pass `HLSLPROGRAM` 的下一物理行）。\n  * 注入平台保底宏、变体首项宏，并写入 `#line` 坐标锚定指令。'
))

# D3: 引导期 DLL 预加载修正
detail_edits.append((
    '1. **DLL 绑定拦截**：在任何 DXC 类型被触及之前，向 .NET 运行时注册 `NativeLibrary.SetDllImportResolver`，强制将 `dxcompiler.dll` 与 `dxil.dll` 的搜寻路径绑定到应用启动根目录，规避加载系统老旧 DLL 的风险。',
    '1. **DLL 绝对路径预加载**：在任何 DXC 类型被触及之前，用 `NativeLibrary.Load(绝对路径)` 显式加载随包的 `dxcompiler.dll` 与 `dxil.dll`。**注意**：`NativeLibrary.SetDllImportResolver` 仅对本程序集的 DllImport 生效，无法约束 Vortice 等第三方库内部的 DllImport；而 Win32 约定下同名模块一旦驻留进程，后续任何按名 `LoadLibrary` 均命中已加载模块——预加载即劫持，一步同时管住自家代码与第三方绑定库。'
))

# D4: 步骤A 探针行来源修正
detail_edits.append((
    '''   ├─► 步骤 A：锁定分析目标
   │     • 打字触发 (IsSaveEvent = false)：提取器以 CursorLine 为探针，命中当前 Pass。
   │     • 保存触发 (IsSaveEvent = true)：提取器无视光标，提取文件中全部 Pass。''',
    '''   ├─► 步骤 A：锁定分析目标
   │     • 打字触发 (IsSaveEvent = false)：LSP 全量同步（SyncKind.Full）的 didChange 不携带
   │       光标位置，服务端对新旧全量文本做行级 Diff，取首个改动行作为探针命中当前 Pass
   │       （编辑器无关，前端零额外代码；客户端自定义光标通知列为可选增强，不进 MVP）。
   │     • 保存触发 (IsSaveEvent = true)：提取器无视改动位置，提取文件中全部 Pass。'''
))

# D5: 时序仲裁补队列契约
detail_edits.append((
    '''* **处理契约**：每个任务携带着发起时刻的 `DocumentVersion`。无论各阶段在线程池中谁先执行完毕，最终提交给客户端前，执行单调递增比对：
  `if (task.Version < CurrentActiveDocument.Version) { 丢弃本次输出; }`''',
    '''* **处理契约**：每个任务携带着发起时刻的 `DocumentVersion`。无论各阶段在线程池中谁先执行完毕，最终提交给客户端前，执行单调递增比对：
  `if (task.Version < CurrentActiveDocument.Version) { 丢弃本次输出; }`
* **派发队列契约**（R3 `AwaitOperation` 语义，按官方文档核实）：
  * 打字防抖流用 `Switch`：编译进行中来了新版本，取消旧任务跑新的（允许被打断）。
  * 保存流用 `Sequential`：Ctrl+S 全量任务**排队必达，绝不丢弃**（严禁对保存使用 `Drop`——编译进行中按 Ctrl+S 会被静默丢弃，“保存必出全量结果”的预期落空）。'''
))

# D6: 接口签名 cursorLine → probeLine
detail_edits.append((
    '`IDiagnosticEngine.AnalyzeDocument(SourceShaderDocument doc, int cursorLine, bool isSave) -> Task<DiagnosticReport>`，后续 LSP 协议层可直接无缝对接。',
    '`IDiagnosticEngine.AnalyzeDocument(SourceShaderDocument doc, int probeLine, bool isSave) -> Task<DiagnosticReport>`（`probeLine` 由协调层通过行级 Diff 求得，语义为"首个改动行"，非光标行），后续 LSP 协议层可直接无缝对接。'
))

# D7: 文末追加 §七 探针 + 修正清单
detail_edits.append((
    '本模块完成详细设计后，输出的标准接口契约即为：',
    '''---

## 七、 落地顺序与首道工程探针（Spike）

详细设计落地后的第一个工程动作**不是写状态机**，而是验证全项目最大的技术未知：**Vortice.Dxc 在 .NET 10 Native AOT 下能否驱动真实 dxcompiler.dll**。

* **探针内容**：最小控制台工程（net10.0-windows，`PublishAot=true`），引用 Vortice.Dxc；启动时绝对路径预加载 `dxcompiler.dll` + `dxil.dll`；编译一段含故意语法错误的 hello-world HLSL，验证 AOT 发布成功、编译正常返回、诊断文本可解析且行号正确。
* **依据与纪律**：Vortice 官方 CHANGELOG 声明 "Support trimming and AOT"，且本项目只做**出站调用**（无 `IDxcIncludeHandler` 回调），理论风险可控——但以本机实测为准，不以官方声明为准。**探针未通过前，禁止进入业务模块编码**。
* **回退预案**：若翻车，改用手写出站 vtable 绑定（仅需 `IDxcCompiler3` / `IDxcUtils` / `IDxcResult` / `IDxcBlobUtf8` 四个接口的纯出站调用，无回调，约百行），上层模块零改动。

## 附：本轮评审修正清单（v1.0 → v1.1）

1. 新增 `#include_with_pragmas` 降级重写规则（DXC 不识别该 Unity 私有指令，实测 URP 14 `Lit.shader` 含 10 处）。
2. 新增 `HLSLINCLUDE/CGINCLUDE` 共享块识别与"拼入每个 Pass 头部"语义（原为孤立块独立编译，会导致共享符号误报）。
3. 聚焦 Pass 的探针行来源由"CursorLine"修正为"行级 Diff 首个改动行"（SyncKind.Full 不携带光标位置）。
4. R3 派发策略修正：防抖流 `Switch`、保存流 `Sequential`，废弃合并流统一 `Drop`（会丢保存）。
5. DLL 解析由 `SetDllImportResolver` 修正为启动时绝对路径预加载（前者管不到第三方库内部 DllImport）。

本模块完成详细设计后，输出的标准接口契约即为：'''
))

def apply(path, edits, tag):
    with open(path, "r", encoding="utf-8", newline="") as f:
        content = f.read()
    nl = "\r\n" if content.count("\r\n") > content.count("\n") / 2 else "\n"
    fails = 0
    for i, (old, new) in enumerate(edits):
        old_n = old.replace("\n", nl)
        new_n = new.replace("\n", nl)
        c = content.count(old_n)
        if c == 1:
            content = content.replace(old_n, new_n)
            print(f"[{tag} E{i}] OK")
        else:
            fails += 1
            print(f"[{tag} E{i}] FAIL count={c} :: {old[:40]!r}")
    if fails == 0:
        with open(path, "w", encoding="utf-8", newline="") as f:
            f.write(content)
        print(f"[{tag}] written, newline={nl!r}")
    else:
        print(f"[{tag}] NOT written (has failures)")

apply(MAIN, main_edits, "MAIN")
apply(DETAIL, detail_edits, "DETAIL")
