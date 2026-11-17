using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using MicroShader.ContextEngine;

namespace MicroShader.DiagnosticEngine.Native;

/// <summary>
/// 自定义 "IDxcIncludeHandler" 桥（手写 vtable + <see cref="UnmanagedCallersOnlyAttribute"/> 导出）。
///
/// 为什么必须有它（对 ADR-002 的实测修正）：
///   ADR-002 选择「内存字符串改写」而不是 COM include 回调，前提是"只有主文件里的 include 需要改写"。
///   实测推翻了该前提：URP 官方 Lit.shader 的 "#include "Packages/…/LitInput.hlsl"" 被改写成功，
///   但 LitInput.hlsl "文件内部"还有 "#include "Packages/…/ShaderLibrary/Core.hlsl"" ——
///   这段文本由 DXC 从磁盘读取，我们没有任何改写机会，而 "Packages/&lt;pkg&gt;" 在磁盘上并不存在
///   （registry 包的真实目录是 "Library/PackageCache/&lt;pkg&gt;@&lt;ver&gt;"）。
///   证据：Dxc.IncludeBase 基准实验（handler=null 时连同目录相对 include 都解析不了）+ Compile.Lit 的 7 条 file not found。
///
/// 因此改用「出站 + 单个托管回调」方案：
///   * 回调是 "[UnmanagedCallersOnly]"（Native AOT 安全，无 marshal 桩、无 IL3050）；
///   * 回调只做两件事：用 VFS 把请求名映射成物理路径，然后交给 "IDxcUtils::LoadFile" 读盘（DXC 判编码）；
///   * VFS 快照是只读的，读端无锁，因此回调线程安全；
///   * 我们不做任何 blob 生命周期管理 —— 读盘与编码全在 DXC 侧。
/// </summary>
public sealed unsafe class DxcIncludeHandlerBridge : IDisposable
{
    private const int HResultFileNotFound = unchecked((int)0x80070002);

    private static readonly nint* Vtable;
    private static VfsIndex? _index;

    /// <summary>DXC 实际请求过的文件名（最近若干条，仅排障用）。</summary>
    private static readonly List<string> RecentRequests = new(256);

    private static readonly string[] VirtualSegments = ["Packages/", "Packages\\", "Assets/", "Assets\\"];

    /// <summary>回调里最后一条异常（托管异常绝不能穿越 <see cref="UnmanagedCallersOnlyAttribute"/> 边界，那会直接终结进程）。</summary>
    private static string? _lastError;

    private readonly nint _object;
    private readonly nint _utils;
    private int _disposed;

    /// <summary>已创建的处理器实例数（排障：为 0 说明网关根本没挂上桥）。</summary>
    public static int CreatedCount;

    /// <summary>LoadSource 被 DXC 调用的次数。</summary>
    public static int CallCount;

    static DxcIncludeHandlerBridge()
    {
        // IUnknown 0/1/2 + IDxcIncludeHandler::LoadSource = 3
        var table = (nint*)NativeMemory.Alloc(4 * (nuint)sizeof(nint));
        table[0] = (nint)(delegate* unmanaged[Stdcall]<void*, Guid*, void**, int>)&QueryInterface;
        table[1] = (nint)(delegate* unmanaged[Stdcall]<void*, int>)&AddRef;
        table[2] = (nint)(delegate* unmanaged[Stdcall]<void*, int>)&Release;
        table[3] = (nint)(delegate* unmanaged[Stdcall]<void*, char*, void**, int>)&LoadSource;
        Vtable = table;
    }

    private DxcIncludeHandlerBridge(nint utils)
    {
        Interlocked.Increment(ref CreatedCount);
        _utils = utils;
        _object = (nint)NativeMemory.Alloc((nuint)sizeof(HandlerObject));
        var self = (HandlerObject*)_object;
        self->Vtable = (nint)Vtable;
        self->RefCount = 1;
        self->Utils = utils;
    }

    /// <summary>供 vtable 回调使用的 COM 对象布局（首字段必须是 vtable 指针）。</summary>
    private struct HandlerObject
    {
        public nint Vtable;
        public int RefCount;
        public nint Utils;
    }

    /// <summary>传给 "IDxcCompiler3::Compile" 的处理器指针。</summary>
    public nint Pointer => _object;

    /// <summary>
    /// 当前生效的 VFS 快照。由引擎在编译前赋值 —— 快照本身不可变，写入是原子引用替换，
    /// 因此编译线程读到的一定是某个完整快照。
    /// </summary>
    public static VfsIndex? Index
    {
        get => Volatile.Read(ref _index);
        set => Volatile.Write(ref _index, value);
    }

    public static IReadOnlyList<string> SnapshotRecentRequests()
    {
        lock (RecentRequests)
        {
            return RecentRequests.ToArray();
        }
    }

    public static void ClearRecentRequests()
    {
        lock (RecentRequests)
        {
            RecentRequests.Clear();
        }
    }

    public static string? TakeLastError() => Interlocked.Exchange(ref _lastError, null);

    public static DxcIncludeHandlerBridge? TryCreate(nint utils)
    {
        if (utils == 0)
        {
            return null;
        }

        return new DxcIncludeHandlerBridge(utils);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        NativeMemory.Free((void*)_object);
    }

    // ───────────────────────── vtable 实现 ─────────────────────────

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int QueryInterface(void* pThis, Guid* iid, void** ppv)
    {
        if (ppv == null)
        {
            return unchecked((int)0x80004003); // E_POINTER
        }

        // 只承认 IUnknown（本项目不需要别人按接口查询它）。
        *ppv = null;
        return unchecked((int)0x80004002); // E_NOINTERFACE
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int AddRef(void* pThis)
    {
        var self = (HandlerObject*)pThis;
        return Interlocked.Increment(ref self->RefCount);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int Release(void* pThis)
    {
        var self = (HandlerObject*)pThis;
        return Interlocked.Decrement(ref self->RefCount);
    }

    /// <summary>
    /// DXC 请求加载一个 include。返回非 0 即「找不到」，DXC 会据此报 fatal error（正是我们要的语义）。
    /// </summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int LoadSource(void* pThis, char* pFilename, void** ppIncludeSource)
    {
        if (ppIncludeSource == null)
        {
            return unchecked((int)0x80004003);
        }

        Interlocked.Increment(ref CallCount);
        *ppIncludeSource = null;
        if (pFilename == null)
        {
            return HResultFileNotFound;
        }

        // 托管异常绝不允许穿越 UnmanagedCallersOnly 边界（会直接终结进程，且无托管调用栈可查）。
        try
        {
            var self = (HandlerObject*)pThis;
            string requested = new(pFilename);

            if (!TryMapToPhysical(requested, out string physical))
            {
                return HResultFileNotFound;
            }

            Record("[命中] " + requested + " → " + physical);

            byte[] bytes;
            try
            {
                bytes = File.ReadAllBytes(physical);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                return HResultFileNotFound;
            }

            // 编码：Unity 侧源码绝大多数是 UTF-8（可能带 BOM）；只有 UTF-16LE BOM 才改判。
            uint codePage = DxcApi.CodepageUtf8;
            if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            {
                codePage = DxcApi.CodepageUtf16;
            }

            void* blob = null;
            int loadHr;
            fixed (byte* pBytes = bytes)
            {
                loadHr = DxcApi.UtilsCreateBlob((void*)self->Utils, pBytes, (uint)bytes.Length, codePage, &blob);
            }

            if (loadHr != 0 || blob == null)
            {
                return loadHr != 0 ? loadHr : HResultFileNotFound;
            }

            *ppIncludeSource = blob;
            return 0;
        }
        catch (Exception ex)
        {
            Interlocked.Exchange(ref _lastError, ex.ToString());
            return HResultFileNotFound;
        }
    }

    /// <summary>请求名 → 物理路径：先走 VFS（虚拟前缀 / 工程相对），再退化为真实存在的路径。</summary>
    private static bool TryMapToPhysical(string requested, out string physical)
    {
        physical = requested;
        var index = Index;

        // ① 主文件里被改写成绝对物理路径的 include，以及 DXC 传回的原始虚拟路径。
        if (index is not null && TryResolveVirtual(index, requested, out physical))
        {
            return true;
        }

        // ② DXC 会把 include 原文与"当前文件所在目录"拼成候选路径再交给处理器 ——
        //    拼出来的前缀是假的（虚拟目录并不存在），但 Packages/ 或 Assets/ 之后的虚拟路径仍然有效。
        if (index is not null)
        {
            int at = IndexOfVirtualSegment(requested);
            if (at > 0 && TryResolveVirtual(index, requested[at..], out physical))
            {
                Record($"[按虚拟段命中] {requested} → {physical}");
                return true;
            }
        }

        // ③ 兜底：真实存在的路径（覆盖 CGIncludes 等物理目录）。
        if (File.Exists(requested))
        {
            physical = requested;
            return true;
        }

        Record($"[未解析 index={(index is null ? "null" : "有")}] {requested}");
        return false;
    }

    private static bool TryResolveVirtual(VfsIndex index, string candidate, out string physical)
    {
        physical = candidate;
        if (candidate.Length == 0)
        {
            return false;
        }

        var resolution = IncludeResolver.Resolve(index, candidate.AsSpan(), default, verifyOnDisk: true);
        if (resolution.Found && resolution.PhysicalPath is { Length: > 0 } mapped)
        {
            physical = mapped;
            return true;
        }

        return false;
    }

    /// <summary>在候选路径里找出虚拟段（"Packages/" 或 "Assets/"）的起点；找不到返回 -1。</summary>
    private static int IndexOfVirtualSegment(string path)
    {
        foreach (var segment in VirtualSegments)
        {
            int at = path.IndexOf(segment, StringComparison.OrdinalIgnoreCase);
            if (at >= 0)
            {
                return at;
            }
        }

        return -1;
    }

    /// <summary>是否记录请求明细（排障用；生产路径保持 false，避免每次 include 都建字符串）。</summary>
    public static bool Trace
    {
        get => Volatile.Read(ref _trace) != 0;
        set => Volatile.Write(ref _trace, value ? 1 : 0);
    }

    private static int _trace;

    private static void Record(string message)
    {
        if (!Trace)
        {
            return;
        }

        lock (RecentRequests)
        {
            if (RecentRequests.Count < 512)
            {
                RecentRequests.Add(message);
            }
        }
    }
}
