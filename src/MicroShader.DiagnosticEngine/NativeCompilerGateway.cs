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
        CompilerSlot slot = RentSlot();
        DiagTrace.Mark("DXC 编译开始 sourceChars=" + source.Length + " args=" + arguments.Count + " thread=" + Environment.CurrentManagedThreadId);

        fixed (char* pSource = source)
        {
            var buffer = new DxcApi.DxcBuffer
            {
                Ptr = pSource,
                // UTF-16 零拷贝：DXC_CP_UTF16 与 DXC_CP_UTF8 编译结果一致（ADR-013 实测）。
                Size = (nuint)(source.Length * sizeof(char)),
                Encoding = DxcApi.CodepageUtf16,
            };

            nint[] argv = ArrayPool<nint>.Shared.Rent(arguments.Count);
            try
            {
                for (int i = 0; i < arguments.Count; i++)
                {
                    argv[i] = Marshal.StringToHGlobalUni(arguments[i]);
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
                        return new DxcCompileResult
                        {
                            Status = -1,
                            CompileHr = hr,
                            DiagnosticText = $"DXC 调用本身失败：{DxcApi.Hex(hr)}",
                            ObjectSize = 0,
                            ElapsedMicroseconds = Elapsed(start),
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
                for (int i = 0; i < arguments.Count; i++)
                {
                    if (argv[i] != 0)
                    {
                        Marshal.FreeHGlobal(argv[i]);
                    }
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

    private CompilerSlot RentSlot()
    {
        if (_slots.TryTake(out CompilerSlot? slot))
        {
            return slot;
        }

        if (!_library.TryCreateCompiler(out nint compiler) || !_library.TryCreateUtils(out nint utils))
        {
            throw new InvalidOperationException("无法创建 IDxcCompiler3 / IDxcUtils 实例");
        }

        // 必须挂自带 VFS 映射的 include 处理器：DXC 默认处理器解析不了 Packages/<pkg> 虚拟路径
        // （registry 包在磁盘上是 <pkg>@<ver>），而包内文件之间的 include 我们无权改写。
        return new CompilerSlot(compiler, utils, DxcIncludeHandlerBridge.TryCreate(utils));
    }

    private void ReturnSlot(CompilerSlot slot)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            ReleaseSlot(slot);
            return;
        }

        _slots.Add(slot);
    }

    private static void ReleaseSlot(CompilerSlot slot)
    {
        slot.IncludeHandler?.Dispose();

        if (slot.Utils != 0)
        {
            DxcApi.Release((void*)slot.Utils);
        }

        if (slot.Compiler != 0)
        {
            DxcApi.Release((void*)slot.Compiler);
        }
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
        public nint Compiler { get; } = compiler;
        public nint Utils { get; } = utils;

        /// <summary>VFS 映射用 include 处理器（null 表示创建失败，此时 include 解析必然失败）。</summary>
        public DxcIncludeHandlerBridge? IncludeHandler { get; } = includeHandler;
    }
}
