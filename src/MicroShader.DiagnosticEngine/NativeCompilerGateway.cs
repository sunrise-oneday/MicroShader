using System.Buffers;
using System.Runtime.InteropServices;
using System.Text;
using MicroShader.DiagnosticEngine.Native;
using MicroShader.ObservabilityEngine;

namespace MicroShader.DiagnosticEngine;

/// <summary>一次原生编译请求的原始结果（不做任何坐标映射）。</summary>
public readonly struct DxcCompileResult
{
    public required int Status { get; init; }

    /// <summary>Compile 调用本身的 HRESULT（与 <see cref="Status"/> 区分：后者来自 IDxcResult::GetStatus）。</summary>
    public required int CompileHr { get; init; }

    /// <summary>诊断原文（COM GetErrorBuffer → GetBlobAsUtf8；绝不用 dxc.exe CLI，那是 GBK）。</summary>
    public required string DiagnosticText { get; init; }

    /// <summary>主输出（DXIL 对象）字节数。</summary>
    public required int ObjectSize { get; init; }

    public required int ElapsedMicroseconds { get; init; }

    /// <summary>DXC 判定失败（rc != 0）或 Compile 本身失败。</summary>
    public bool Failed => CompileHr != 0 || Status != 0;

    /// <summary>
    /// 编译调用本身没成功（HRESULT 非 0 或结果对象为空）。此时 <see cref="DiagnosticText"/> 只是
    /// 工具自己写的一句说明，不含任何「file:line:col: severity:」结构，解析必然是 0 条 ——
    /// 消费方必须把它当「诊断不可用」，绝不能当成「零诊断 = 代码干净」。
    /// </summary>
    public bool InvocationFailed { get; init; }
}

/// <summary>
/// 原生编译器网关：管理 IDxcCompiler3 / IDxcUtils 的进程级常驻与跨 C-ABI 调用。
///
/// 线程契约（模块文档 §四）：ThreadPool 后台 Worker、同文档同步排他、MTA。
/// 实例按线程池化（PerThread），每个线程持有自己的 compiler/utils 指针，
/// 规避 dxcompiler 的跨线程共享风险（ADR-019）。
///
/// 内存所有权：源码以 UTF-16 固定指针零拷贝交给 DXC（DxcBuffer 直接指向调用方内存），
/// 调用返回后立即释放原生输出，不把任何非托管指针带出本方法。
/// </summary>
public sealed unsafe class NativeCompilerGateway : IDisposable
{
    private readonly DxcNativeLibrary _library;
    private readonly System.Collections.Concurrent.ConcurrentBag<CompilerSlot> _slots = new();
    private int _disposed;

    private NativeCompilerGateway(DxcNativeLibrary library, int maxThreads)
    {
        _library = library;
        MaxThreads = maxThreads;
    }

    /// <summary>实例池上限（等于可用 Worker 线程数上限）。</summary>
    public int MaxThreads { get; }

    public Version DxcVersion => _library.DxcVersion;

    /// <summary>当前已创建的原生实例槽数量（用于诊断泄漏）。</summary>
    public int LiveSlotCount => _slots.Count;

    /// <summary>
    /// 定位并加载 dxcompiler.dll，建立常驻网关。失败时 <paramref name="gateway"/> 为 null 且给出原因。
    /// </summary>
    public static bool TryCreate(string dxcDirectory, out NativeCompilerGateway? gateway, out string error, int maxThreads = 0)
    {
        gateway = null;
        if (!DxcNativeLibrary.TryLoad(dxcDirectory, out DxcNativeLibrary? library, out error) || library is null)
        {
            return false;
        }

        if (maxThreads <= 0)
        {
            maxThreads = Math.Clamp(Environment.ProcessorCount, 2, 8);
        }

        gateway = new NativeCompilerGateway(library, maxThreads);
        return true;
    }

    /// <summary>
    /// 编译一段 HLSL 源码。<paramref name="arguments"/> 与 dxc.exe 命令行同形，
    /// 约定 "arguments[0]" 为源文件名（位置参数），用于 DXC 的诊断文件名与相对 include 基准目录。
    /// </summary>
    /// <param name="source">UTF-16 源码（虚拟装配产物），调用期间被固定，不复制。</param>
    /// <param name="arguments">已构造好的参数表（不含 argv[0] 之外的托管对象生命周期问题）。</param>
    /// <param name="useIncludeHandler">
    /// 是否传入 DXC 默认 include 处理器。实测：传 null 时 DXC "完全不做任何 include 解析"，
    /// 连「与被包含文件同目录」的相对 include 都报 file not found（见 Dxc.IncludeBase 基准实验）。
    /// 生产路径恒为 true；false 只用于基准实验。
    /// </param>
    public DxcCompileResult Compile(ReadOnlySpan<char> source, IReadOnlyList<string> arguments, bool useIncludeHandler = true)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(arguments);

        long start = System.Diagnostics.Stopwatch.GetTimestamp();

        // 实例建不出来时不再抛异常打断整篇分析：返回带 InvocationFailed 标记的结果，
        // 上层据此报「编译器不可用」并跳过单元缓存（下次编辑或保存会自动重试）。
        CompilerSlot? slot = RentSlot(out string slotError);
        if (slot is null)
        {
            DiagTrace.Mark("DXC 实例创建失败：" + slotError);
            return InstanceUnavailable(slotError, Elapsed(start));
        }

        // 每次编译都会走到这里：追踪没开时连字符串都别拼（拼接发生在调用之前，Mark 内部的守卫拦不住）。
        if (DiagTrace.Enabled)
        {
            DiagTrace.Mark("DXC 编译开始 sourceChars=" + source.Length + " args=" + arguments.Count
                + " thread=" + Environment.CurrentManagedThreadId);
        }

        fixed (char* pSource = source)
        {
            var buffer = new DxcApi.DxcBuffer
            {
                Ptr = pSource,
                // UTF-16 零拷贝：DXC_CP_UTF16 与 DXC_CP_UTF8 编译结果一致（ADR-013 实测）。
                Size = (nuint)(source.Length * sizeof(char)),
                Encoding = DxcApi.CodepageUtf16,
            };

            // ArrayPool.Rent 不清零，租到的数组里是上一次使用留下的旧值。
            // 因此必须"记住真正分配了几个"，只释放这几个 —— 用 "argv[i] != 0" 当判定会把
            // 未分配槽位里的垃圾值当成堆指针交给 FreeHGlobal，Native AOT 下即进程崩溃。
            nint[] argv = ArrayPool<nint>.Shared.Rent(arguments.Count);
            var allocated = 0;
            try
            {
                for (int i = 0; i < arguments.Count; i++)
                {
                    argv[i] = Marshal.StringToHGlobalUni(arguments[i]);
                    allocated++;
                }

                fixed (nint* pArgv = argv)
                {
                    Guid iid = DxcApi.IidDxcResult;
                    void* result = null;
                    void* handler = useIncludeHandler && slot.IncludeHandler is { } bridge ? (void*)bridge.Pointer : null;
                    int hr = DxcApi.Compile((void*)slot.Compiler, &buffer, pArgv, (uint)arguments.Count,
                                            handler, &iid, &result);

                    if (hr != 0 || result == null)
                    {
                        // 标记 InvocationFailed：这行文本不是 DXC 诊断，上层据此既不上报「干净」，
                        // 也不把它写进单元缓存。
                        return new DxcCompileResult
                        {
                            Status = -1,
                            CompileHr = hr,
                            DiagnosticText = $"DXC 调用本身失败：{DxcApi.Hex(hr)}",
                            ObjectSize = 0,
                            ElapsedMicroseconds = Elapsed(start),
                            InvocationFailed = true,
                        };
                    }

                    try
                    {
                        int status = 0;
                        DxcApi.ResultGetStatus(result, &status);

                        string text = ReadErrorBuffer(slot.Utils, result);
                        int objectSize = ReadObjectSize(result);

                        return new DxcCompileResult
                        {
                            Status = status,
                            CompileHr = hr,
                            DiagnosticText = text,
                            ObjectSize = objectSize,
                            ElapsedMicroseconds = Elapsed(start),
                        };
                    }
                    finally
                    {
                        DxcApi.Release(result);
                    }
                }
            }
            finally
            {
                for (int i = 0; i < allocated; i++)
                {
                    Marshal.FreeHGlobal(argv[i]);
                }

                ArrayPool<nint>.Shared.Return(argv);
                ReturnSlot(slot);
            }
        }
    }

    private static int Elapsed(long start)
        => (int)((System.Diagnostics.Stopwatch.GetTimestamp() - start) * 1_000_000L
                 / System.Diagnostics.Stopwatch.Frequency);

    /// <summary>诊断文本必须走 COM（UTF-8 blob），CLI 输出是 GBK 会乱码。</summary>
    private static string ReadErrorBuffer(nint utils, void* result)
    {
        void* errorBlob = null;
        if (DxcApi.ResultGetErrorBuffer(result, &errorBlob) != 0 || errorBlob == null)
        {
            return string.Empty;
        }

        try
        {
            void* utf8 = null;
            if (DxcApi.UtilsGetBlobAsUtf8((void*)utils, errorBlob, &utf8) == 0 && utf8 != null)
            {
                try
                {
                    byte* p = DxcApi.BlobUtf8Ptr(utf8);
                    nuint n = DxcApi.BlobUtf8Len(utf8);
                    return n == 0 ? string.Empty : Encoding.UTF8.GetString(p, (int)n);
                }
                finally
                {
                    DxcApi.Release(utf8);
                }
            }

            // 回退：按 blob 原始字节解 UTF-8。
            nuint raw = DxcApi.BlobSize(errorBlob);
            return raw == 0 ? string.Empty : Encoding.UTF8.GetString((byte*)DxcApi.BlobPtr(errorBlob), (int)raw);
        }
        finally
        {
            DxcApi.Release(errorBlob);
        }
    }

    private static int ReadObjectSize(void* result)
    {
        void* obj = null;
        if (DxcApi.ResultGetResult(result, &obj) != 0 || obj == null)
        {
            return 0;
        }

        try
        {
            return (int)DxcApi.BlobSize(obj);
        }
        finally
        {
            DxcApi.Release(obj);
        }
    }

    /// <summary>实例池里没有空槽时新建一个；建不出来返回 null 并把原因写进 error。</summary>
    private CompilerSlot? RentSlot(out string error)
    {
        if (_slots.TryTake(out CompilerSlot? slot))
        {
            error = string.Empty;
            return slot;
        }

        if (!TryCreateInstances(
                _library.TryCreateCompiler,
                _library.TryCreateUtils,
                ReleaseCom,
                out nint compiler,
                out nint utils,
                out error))
        {
            return null;
        }

        // 必须挂自带 VFS 映射的 include 处理器：DXC 默认处理器解析不了 Packages/<pkg> 虚拟路径
        // （registry 包在磁盘上是 <pkg>@<ver>），而包内文件之间的 include 我们无权改写。
        return new CompilerSlot(compiler, utils, DxcIncludeHandlerBridge.TryCreate(utils));
    }

    /// <summary>创建单个 COM 实例的钩子；自检用它注入「必然失败」的分支。</summary>
    internal delegate bool TryCreateInstance(out nint instance);

    /// <summary>
    /// 建出 compiler 与 utils 一对实例。utils 失败时必须释放已经建好的 compiler：
    /// 这段代码在每个缓存未命中的线程上都会跑一次，漏掉一个 COM 实例就会随线程数在进程里常驻。
    /// 抽成静态函数是为了能在自检里覆盖这个分支 —— 真机上无法让 DxcCreateInstance 只对 utils 失败。
    /// </summary>
    internal static bool TryCreateInstances(
        TryCreateInstance createCompiler,
        TryCreateInstance createUtils,
        Action<nint> release,
        out nint compiler,
        out nint utils,
        out string error)
    {
        utils = 0;
        error = string.Empty;

        if (!createCompiler(out compiler) || compiler == 0)
        {
            error = "无法创建 IDxcCompiler3 实例";
            return false;
        }

        if (!createUtils(out utils) || utils == 0)
        {
            // 修复前这里是短路求值后直接抛异常：compiler 已经 AddRef 过，异常路径上没人释放它。
            release(compiler);
            compiler = 0;
            error = "无法创建 IDxcUtils 实例";
            return false;
        }

        return true;
    }

    private static void ReleaseCom(nint instance)
    {
        if (instance != 0)
        {
            DxcApi.Release((void*)instance);
        }
    }

    /// <summary>
    /// 实例建不出来时的结果：与「Compile 调用本身失败」同形，上层按「编译器不可用」处理，
    /// 既不把它当成零诊断（那会显示为干净），也不写进单元缓存。
    /// </summary>
    internal static DxcCompileResult InstanceUnavailable(string detail, int elapsedMicroseconds) => new()
    {
        Status = -1,
        // DxcCreateInstance 的原始失败码在 TryCreateRaw 里已经被丢掉，这里只能给「调用未发生」。
        CompileHr = DxcApi.HrFail,
        DiagnosticText = "无法创建 DXC 实例，本单元诊断不可用：" + detail,
        ObjectSize = 0,
        ElapsedMicroseconds = elapsedMicroseconds,
        InvocationFailed = true,
    };

    private void ReturnSlot(CompilerSlot slot)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            ReleaseSlot(slot);
            return;
        }

        _slots.Add(slot);
    }

    /// <summary>
    /// 释放一个已经脱离实例池的槽：先放 include 处理器（它自己持有原生内存），再放两个 COM 实例。
    /// 释放后把字段全部置空，这样同一个槽被误释放第二次也只是空转，不会把已经归还的 COM 指针再 Release 一遍。
    /// </summary>
    private static void ReleaseSlot(CompilerSlot slot)
    {
        slot.IncludeHandler?.Dispose();
        slot.IncludeHandler = null;

        if (slot.Utils != 0)
        {
            DxcApi.Release((void*)slot.Utils);
            slot.Utils = 0;
        }

        if (slot.Compiler != 0)
        {
            DxcApi.Release((void*)slot.Compiler);
            slot.Compiler = 0;
        }
    }

    /// <summary>
    /// 回收实例池里的空闲槽，只留 <paramref name="keepAtLeast"/> 个热实例；返回实际释放的数量。
    /// </summary>
    /// <remarks>
    /// 三条兜底，任何一条失守都会让「省内存」变成「变慢」或「崩溃」：
    /// 一、至少留一个：keepAtLeast 小于 1 时按 1 处理，循环里一旦剩余不足也立即停手。
    /// 池被清空等于让下一次诊断重新走一遍 DxcCreateInstance（还要重新建 include 处理器），
    /// 省下几 MB 却让每篇文档多付一次完整创建，这个交换不划算。
    /// 二、只碰空闲槽：TryTake 成功才说明这一枚不在任何编译线程手里；取不到就说明池空了或正被抢，
    /// 立即停。绝不遍历释放已经借出去的实例 —— 那会和在飞编译撞车，Release 掉的是对方正在调用的 vtable，
    /// 原生访问冲突在托管侧拦不住，也留不下可读线索。
    /// 三、释放失败只记日志：本方法是后台空闲回收调用的，单个槽释放抛异常绝不能把进程带走，
    /// 最坏结果只是这一轮没省到内存。
    /// </remarks>
    public int TrimIdleSlots(int keepAtLeast)
    {
        if (keepAtLeast < 1)
        {
            keepAtLeast = 1;
        }

        if (Volatile.Read(ref _disposed) != 0)
        {
            return 0;
        }

        var released = 0;
        while (true)
        {
            // Count 只是近似快照，真正的判据是下面的 TryTake：抢不到就是没有空闲槽，立刻收手。
            if (_slots.Count <= keepAtLeast)
            {
                break;
            }

            if (!_slots.TryTake(out CompilerSlot? slot))
            {
                break;
            }

            // 取出之后重新确认：并发归还会让数量涨，并发抢占会让数量跌，
            // 若这一手把池削到不足 keepAtLeast，就把它放回去 —— 「至少留 keepAtLeast 个」必须恒成立。
            if (_slots.Count < keepAtLeast)
            {
                _slots.Add(slot);
                break;
            }

            // 取出与释放之间引擎可能已经整体释放（Dispose 会卸载 dxcompiler.dll）：
            // 这时放回去而不是继续 Release，宁可漏掉这一枚（进程随即退出），也不能在已卸载的模块上跑 vtable。
            if (Volatile.Read(ref _disposed) != 0)
            {
                _slots.Add(slot);
                break;
            }

            try
            {
                ReleaseSlot(slot);
                released++;
            }
            catch (Exception ex)
            {
                // 单槽失败不影响其余槽：漏省一点内存是可以接受的代价，让后台线程抛出去才是事故。
                DiagTrace.Mark("DXC 空闲槽释放失败：" + ex.GetType().Name + " " + ex.Message);
            }
        }

        return released;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        while (_slots.TryTake(out CompilerSlot? slot))
        {
            ReleaseSlot(slot);
        }

        _library.Dispose();
    }

    private sealed class CompilerSlot(nint compiler, nint utils, DxcIncludeHandlerBridge? includeHandler)
    {
        // 三个字段都可写：ReleaseSlot 释放完之后要把它们清零/置空，
        // 让「已释放的槽」在结构上是可辨认的，误释放第二次也不会重复归还原生指针。
        public nint Compiler { get; set; } = compiler;
        public nint Utils { get; set; } = utils;

        /// <summary>VFS 映射用 include 处理器（null 表示创建失败，此时 include 解析必然失败）。</summary>
        public DxcIncludeHandlerBridge? IncludeHandler { get; set; } = includeHandler;
    }
}
