# Unity 桥接宿主（模块 11B）

把 Unity 编辑器里**权威的**着色器宏状态（全局关键字 + 全局宏定义/未定义）实时推给
MicroShader 语言服务器，使诊断从「按平台表推定」升级为「按编辑器实测」。

```
Unity Editor（MicroShaderBridge/ 一组合类）
  ├─ 写 <Project>/Library/MicroShader/endpoint.json   ← 发现文件（原子替换）
  └─ 监听 127.0.0.1:<动态端口> TCP                ← 广播契约 2 载荷
            ▲
            │ 外连（Unity-as-server 拓扑，ADR-016 默认路线）
UnityShaderLsp.exe
  └─ UnityBridgeClient → ShaderContextRegistry.PublishDynamic → 模块 3 注入段
```

## 一、安装（零依赖、整目录复制）

1. 把 `MicroShaderBridge/` **整个文件夹**复制到 Unity 工程的**任意 `Editor` 文件夹**下，例如
   `<Project>/Assets/Editor/MicroShaderBridge/`。
   - 各文件自带 `#if UNITY_EDITOR` 保护，误放到 `Assets/` 根也不会污染播放器构建。
   - 不依赖任何 Package、Prefab、ScriptableObject，也不需要 UniTask。
   - 路径里必须有一个目录叫 `Editor`（Unity 的编译范围靠目录名判定）。
2. 回到 Unity（或 `Assets → Refresh`）。Console 出现一行：

   ```
   [MicroShader] 桥接已监听 127.0.0.1:60522（transport=tcp，动态端口），发现文件：<Project>/Library/MicroShader/endpoint.json
   ```

3. 语言服务端（VS Code 扩展内置的 `UnityShaderLsp.exe`）会自动发现并连接。
   Output 面板出现：

   ```
   Vfs: Unity 桥接（启动快照）：已连接 Unity（pid 32652，127.0.0.1:60522，已收 1 帧/应用 1 次），Unity 2022.3.62f3c1；…
   ```

**卸载**：删掉 `MicroShaderBridge/` 目录与 `Library/MicroShader/endpoint.json`。做任何事之前先退出 Unity，
或直接用菜单 `Tools/MicroShader/Unity Bridge/启用/禁用` 关掉（关掉时会顺手删发现文件）。

## 二、模块划分（为什么不是一个文件）

按「高内聚、低耦合」拆成 14 个类，每一类只有一个变更理由：

| 文件 | 职责 | 依赖 |
| :--- | :--- | :--- |
| `ShaderLspBridge.cs` | **组合根**：`[InitializeOnLoad]` 事件接线、采样→发布编排、对外状态 | 全组 |
| `ShaderLspBridgeMenu.cs` | 菜单与状态文本（**展示层**，与生命周期分开）| 组合根 |
| `BridgeContracts.cs` | 协议常量（契约 1/2 的唯一真相源）+ `IBridgeContextSource` | 无 |
| `BridgeLog.cs` | 日志出口；持有**线程安全**的详细日志开关 | 无 |
| `BridgeSettings.cs` | EditorPrefs / SessionState 的**唯一入口**（把主线程限定 API 收在一处）| 无 |
| `BridgeMainThread.cs` | 后台线程 → 主线程的唯一通道 + 线程契约断言 | 无 |
| `BridgeJson.cs` | JSON 转义与数组写入（被两个编码器共用）| 无 |
| `BridgeEnvironment.cs` | 编辑器环境事实采集（色彩空间/构建目标/图形 API/管线）| 无 |
| `BridgeContextSnoop.cs` | 10Hz 采样 + 零分配变化判定 + 产出载荷 | 环境、编码器 |
| `BridgePayloadCodec.cs` | 契约 2 载荷编码 + LDJSON 封装（纯函数）| Json |
| `BridgeEndpointDocument.cs` | 契约 1 发现文件编码 + `reloading` 标记（纯函数）| Json |
| `BridgeEndpointFile.cs` | 发现文件的原子写 / 标记 / 删除（纯 IO）| 契约 1 编码器 |
| `BridgeServer.cs` | TCP 监听与广播（**不引用任何主线程限定 API**）| 连接、日志 |
| `BridgeClientConnection.cs` | 单客户端连接的收发、断线判定、指令解析 | 协议常量 |

三条刻意维持的边界：

- **编码器是纯函数**：只吃数据、吐文本，不碰 Unity API、不做 IO —— 所以它们可以在任意线程被调用，
  也不会因为「顺手读了个设置」而引入线程约束。
- **传输层与编辑器解耦**：`BridgeServer` / `BridgeClientConnection` 只依赖 `System.Net.Sockets` 与
  线程安全的日志出口。它们不需要知道 Unity 存在，也就不可能踩到主线程限定 API。
- **时钟由调用方注入**：采样器的时间戳是参数，不自己问 `EditorApplication`，因此它只依赖
  `UnityEngine`、不依赖 `UnityEditor` —— 依赖面越小，越不会意外越界。

> 代价与取舍：安装单元从「一个文件」变成「一个目录」。换来的是每个类可独立阅读、
> 可独立修改、且耦合方向可一眼看清（依赖图是树，没有环）。

## 三、菜单

| 菜单项 | 作用 |
| :--- | :--- |
| `状态` | 打印启用/运行/端口/客户端数/广播次数/当前载荷 |
| `启用/禁用` | 切开关（EditorPrefs，默认启用）；关闭时删发现文件 |
| `重启监听` | 手动重绑端口并重写发现文件 |
| `立即广播` | 强制重算载荷并广播（含发现文件重写） |
| `打开发现文件` | 在资源管理器里定位 `endpoint.json` |
| `详细日志开关` | 打印客户端接入/断开、编译边界等细节 |

## 四、发现文件（`Library/MicroShader/endpoint.json`）

| 字段 | 说明 |
| :--- | :--- |
| `version` | schema 版本，当前 `1.0.0`（读端不认识即拒绝连接） |
| `bridgeVersion` | 本脚本版本 |
| `unityPid` / `unityProcessName` / `unityStartTimeUtc` | 判活三元组。启动时间是**抗 PID 复用的权威判据**，进程名只作辅助 |
| `unityVersion` | 例如 `2022.3.62f3c1` |
| `projectPath` / `projectName` | 工程身份（多实例消歧 + 「只读自己工程」的边界） |
| `transport` / `address` / `port` / `protocol` | 传输描述符。当前 `tcp` + `127.0.0.1` + **动态端口** + `ldjson-1` |
| `activeColorSpace` / `activeBuildTarget` / `graphicsApi` / `pipelineAssetType` | 环境快照（写进注入段便于排障） |
| `reloading` | 域重载窗口标记，见下节 |
| `startedAtUtc` | 本次写入时间 |

**为什么放 `Library/` 不放 `Temp/`**：Unity 官方定义 `Temp/` 关闭编辑器即清空，且它只能表达
「编辑器在跑」、不能表达「是哪个工程」。

**原子写**：先写 `endpoint.json.tmp` 再 `File.Move`/`File.Replace`，读端永远看不到半截 JSON。

## 五、广播协议（契约 2）

每客户端接上时先发一份**快照**，之后**只在状态变化时**发差异帧（内容不变则一帧都不发）。
换行分隔 JSON（LDJSON），一帧一行：

```json
{"event":"onShaderContextChanged","timestamp":1790779331,"data":{"activeKeywords":["_MAIN_LIGHT_SHADOWS","_ADDITIONAL_LIGHTS"],"globalDefines":{"defined":["SHADER_API_D3D11"],"undefined":["UNITY_COLORSPACE_GAMMA"]}}}
```

| 部分 | 含义 |
| :--- | :--- |
| `activeKeywords` | 编辑器当前**启用**的全局着色器关键字（取 `Shader.globalKeywords` ∩ `Shader.IsKeywordEnabled`，已排序） |
| `globalDefines.defined` | 应当**被定义**的宏，形如 `"X"` 或 `"X=1"`（ADR-018：带值） |
| `globalDefines.undefined` | 应当**显式 `#undef`** 的宏。`Linear` 时含 `UNITY_COLORSPACE_GAMMA` —— 「不定义」本身是需要被如实表达的结论 |

可选客户端指令（排障增强，非协议必需）：发一行 `{"command":"refresh"}` 会让 Unity 立刻
主线程重算一次载荷并广播（含发现文件重写）。

未知事件必须被读端**忽略**而不是报错（前向兼容）。

## 六、跨域重载的正确收敛

| 时机 | 动作 | 理由 |
| :--- | :--- | :--- |
| `beforeAssemblyReload` | `Stop()` + `Join(2s)` 主动释放端口；发现文件**保留**并标 `"reloading": true` | 裸 `Thread.Abort` 能中断阻塞的 `Accept`，但**不释放 socket 句柄**（实测端口仍被占、重绑报 `AddressAlreadyInUse`）。`Stop()` 是唯一被证实能立即回收端口的手段（实测耗时 20–24 ms） |
| 重载后（静态构造函数） | 优先复用上次端口重绑，绑不上就回落动态分配 | `SessionState` 活过域重载，`static` 字段不会 |
| `EditorApplication.quitting` | 删发现文件 | 唯一删除位置。域重载路径**不删**，避免产生「文件没了但 Unity 还活着」的空洞 |
| `assemblyCompilationStarted` | 只留痕，**不动**任何文件 | 它早于 `beforeAssemblyReload`，且这次编译可能不触发重载（`assemblyCompilationNotRequired`）；提前动手会让读端误判 Unity 已退出 |

为什么标记 `reloading` 而不删文件：重载后端口必须在同一端口上重绑才能让读端平滑重连；
若删了文件，读端只能退化为「未绑定」；但若只保留旧文件，读端又可能去连一个**已被别的进程
抢走**的端口。标记把「端口已释放但进程还活着」这个窗口显式表达出来，读端据此不连接、等待。

## 七、性能与安全约束（全部有实测依据）

- **探针节流 10 Hz**：`EditorApplication.update` 实测约 295 次/秒（不是 60），节流后降采样约 30×。
- **稳态零分配**：比较用预分配 `List<string>` + 逐项比较；**只有状态真的变了**才序列化一次 JSON。
  不做每 tick `new`、不做 `HashSet` 扩容、不做字符串哈希。
- **主线程边界**：所有 Unity API 只在主线程调用。后台线程（监听/发送/客户端读取）**禁止**碰
  `EditorPrefs`、`PlayerSettings` 等主线程限定 API —— 开发期实测教训：`EditorPrefs.GetBool`
  在监听线程上抛 `UnityException`，异常逃出 `AcceptLoop` 会让监听线程**静默死亡**，
  症状是「TCP 能连上（内核完成三次握手）但一帧都收不到」，极难归因。因此后台线程只读
  已缓存的布尔值（`BridgeLog.Verbose`，由 `BridgeSettings` 在主线程写入、`BridgeSettings.PrimeCache()`
  预热）。这条边界现在落在了类型上：`BridgeServer` 与 `BridgeClientConnection` **不引用 UnityEditor**，
  因此它们想违反也没有入口。
- **后台线程异常不致命**：`AcceptLoop` 对非关闭原因异常只记日志并继续；`RegisterClient`
  对单客户端失败做了隔离 —— 监视线程绝不允许被单个客户端带走。
- **槽位回收**：每客户端读取线程负责回收；发送循环还有一次兜底扫描（`IsUsable`），
  杜绝「对端已断开但槽位没释放 → 新客户端再也接不进来」。
- **`MaxClients = 4`**：超出配额直接断开新客户端（明确的「先到者胜」），避免踢掉正在工作的读端。
- **时钟注入**：采样器的时间戳是参数而非 `EditorApplication.timeSinceStartup`，
  因此 `BridgeContextSnoop` 只依赖 `UnityEngine`、不依赖 `UnityEditor`。
- **无网络暴露面**：只绑 `IPAddress.Loopback`，不监听 `0.0.0.0`，不需要 URL ACL / 管理员权限。

## 八、验收与回归

| 层面 | 手段 | 位置 |
| :--- | :--- | :--- |
| 契约 / 判活 / 真 TCP 回环 / 离线零漂移 | 自检套件 `--filter Bridge`（22 条，可挂 CI）| `src/MicroShader.SelfTest/BridgeTests.cs` |
| AOT exe + 真编辑器端到端 | 场外冒烟（**需要 Unity 在运行**，不入库）| `probes/11b-bridge-smoke.mjs`（`node probes/11b-bridge-smoke.mjs`，退出码即判据）|
| 缺陷复盘 | 「TCP 连得上但一帧都收不到」的两个坑 | `docs/bugs/UnityBridge-TCP-connected-no-frame.md` |
| 实现阐释与排障 | 实现纪律、开发难题、使用注意、**Agent CLI/MCP 排障速查表** | `DetailDesign/模块11B-Unity桥接-实现阐释与Agent排障指南.md` |

冒烟脚本断言两件事：Output 面板出现 `Unity 桥接（启动快照）：已连接 Unity（pid …）`，
且打开的 shader 诊断 0 条。退出码 0 = 两条都成立。

## 九、边界与已知限制（如实声明）

1. **`EditorPrefs` 开关是按用户/工程记的**，不进 git；团队里每个人的开关状态独立。
2. **`.hlsl` 不在服务范围内**（ADR 与 v2.0 清单第 5 条）：本脚本不上报任何与 `.hlsl` 文档
   归属相关的信息，`.hlsl` 的工程归属仍由 LSP 侧的模块 8 决定。
3. **色彩空间是 `PlayerSettings.colorSpace`**（编辑器活动色彩空间），不是 `QualitySettings`。
   两者在本机实测一致（均为 Linear），但语义上仍以前者为准。
4. **图形 API 取的是构建目标的 API**（`PlayerSettings.GetGraphicsAPIs(activeBuildTarget)`），
   不是编辑器自己跑在哪个后端 —— 着色器编译按目标平台走，编辑器后端不是权威口径。
   映射不出的后端（未知枚举）一律返回空串，**宁可不注入，也不注入一个推测的宏名**。
5. **同一工程被两个 IDE 窗口打开**时，两个 LSP 进程都会连同一个桥接；桥接按 `MaxClients=4`
   都接纳，但注册表各写各的（两个进程内存隔离）。**未做引用计数**。
6. **`.tmp` 残留**：极端情况下（写一半被强杀）可能留下 `endpoint.json.tmp`；读端忽略它，
   下次写入会覆盖。
7. **多实例同机**：判据是「文件属于哪个工程根」，一个工程一个桥接实例；同机多工程互不越界。
8. **pid 已死的 `endpoint.json` 残留**：Unity 被强杀/崩溃时来不及删除发现文件。LSP 启动时 Output 会打一行
   「发现文件不可信（pid 已不在…）」并退化为无桥接——**这是预期行为，不是故障**；读端按「四不原则」不删该文件，
   下次 Unity 正常启动会原子覆盖它。想立刻消除提示，重开 Unity 即可。
9. **Unity 以管理员身份运行时无法自动化验收**：会弹「Unity is running as administrator.」模态框，
   其 UIA 不响应 `InvokePattern`；计划任务 `RunLevel Limited` 在 UAC 关闭的机器上仍提权；
   `-batchmode` 能加载工程但不执行 `[InitializeOnLoad]`，`endpoint.json` 永远不会生成。
   **结论：真机验收须从 Unity Hub 以普通权限启动编辑器**（勿从提权终端启动）。
