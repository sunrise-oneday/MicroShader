using System.Runtime.InteropServices;

namespace MicroShader.DiagnosticEngine.Native;

/// <summary>
/// 手写出站 COM vtable 垫片（ADR-013 探针已验证：.NET 10 Native AOT 下驱动真实 dxcompiler.dll，
/// 零 IL3050/3051/3052，因为 NativeAOT 没有内置 COM 互操作）。
///
/// 纪律：
///  * 只用绝对路径 NativeLibrary/LoadLibraryW 加载 DLL，禁止 Assembly.Location / CodeBase / GetHINSTANCE；
///  * 全部为出站调用，无任何回调（pIncludeHandler 恒为 null）；
///  * 槽位索引对照 dxcapi.h 实数，改动前必须先跑探针重新核对。
/// </summary>
internal static unsafe class DxcApi
{
    // ── UUID（取自 dxcapi.h 原文） ──
    public static readonly Guid ClsidDxcCompiler = new("73e22d93-e6ce-47f3-b5bf-f0664f39c1b0");
    public static readonly Guid ClsidDxcUtils = new("6245d6af-66e0-48fd-80b4-4d271796748c");
    public static readonly Guid IidDxcCompiler3 = new("228B4687-5A6A-4730-900C-9702B2203F54");
    public static readonly Guid IidDxcUtils = new("4605C4CB-2019-492A-ADA4-65F20BB7D67F");
    public static readonly Guid IidDxcResult = new("58346CDA-DDE7-4497-9461-6F87AF5E0659");
    public static readonly Guid IidDxcVersionInfo = new("b04f5b50-2059-4f12-a8ff-a1e0cde1cc7e");

    // ── 编码 / 输出种类 ──
    public const uint CodepageUtf8 = 65001;
    public const uint CodepageUtf16 = 1200;
    public const uint OutObject = 1;
    public const uint OutErrors = 2;

    /// <summary>DXC 编译失败的权威返回值（探针实测；rc==0 不代表无诊断）。</summary>
    public const int HrFail = unchecked((int)0x80004005); // E_FAIL

    [StructLayout(LayoutKind.Sequential)]
    public struct DxcBuffer
    {
        public void* Ptr;
        public nuint Size;
        public uint Encoding;
    }

    // ── vtable 调用 helper：COM 对象首字段即 vtable 指针 ──
    public static void** Vtable(void* p) => *(void***)p;
    public static void* Slot(void* p, int i) => Vtable(p)[i];

    public static int AddRef(void* p) => ((delegate* unmanaged[Stdcall]<void*, int>)Slot(p, 1))(p);
    public static int Release(void* p) => ((delegate* unmanaged[Stdcall]<void*, int>)Slot(p, 2))(p);
    public static int QueryInterface(void* p, Guid* iid, void** ppv)
        => ((delegate* unmanaged[Stdcall]<void*, Guid*, void**, int>)Slot(p, 0))(p, iid, ppv);

    // IDxcUtils: 3 CreateBlobFromBlob, 4 CreateBlobFromPinned, 5 MoveToBlob, 6 CreateBlob,
    //            7 LoadFile, 8 CreateReadOnlyStreamFromBlob, 9 CreateDefaultIncludeHandler,
    //            10 GetBlobAsUtf8, 11 GetBlobAsWide, 12 GetDxilContainerPart, 13 CreateReflection
    public static int UtilsGetBlobAsUtf8(void* utils, void* blob, void** ppOut)
        => ((delegate* unmanaged[Stdcall]<void*, void*, void**, int>)Slot(utils, 10))(utils, blob, ppOut);

    // 槽位 6：CreateBlob —— 复制一份数据建 blob（对应 dxcapi.h:589，签名为 4 参）。
    // 注意：槽位 7 是 LoadFile，但它的签名是 3 参（LPCWSTR, UINT32* pCodePage, IDxcBlobEncoding**），
    // 少传一个参数会让 dxcompiler 往野指针里写 blob 头 —— 本机实测为 0xC0000005 直接崩进程。
    // 因此这里宁可自己读盘 + CreateBlob（复制语义，无需管理缓冲区生命周期）。
    public static int UtilsCreateBlob(void* utils, void* data, uint size, uint codePage, void** ppBlob)
        => ((delegate* unmanaged[Stdcall]<void*, void*, uint, uint, void**, int>)Slot(utils, 6))(utils, data, size, codePage, ppBlob);

    // 槽位 9：CreateDefaultIncludeHandler —— DXC 自带的默认 include 处理器。
    // 出站调用、无托管回调，因此不违反「无 IDxcIncludeHandler 回调」纪律。
    public static int UtilsCreateDefaultIncludeHandler(void* utils, void** ppOut)
        => ((delegate* unmanaged[Stdcall]<void*, void**, int>)Slot(utils, 9))(utils, ppOut);

    // IDxcCompiler3: 3 Compile, 4 Disassemble
    public static int Compile(void* compiler, DxcBuffer* source, nint* argv, uint argc,
                              void* includeHandler, Guid* riid, void** ppResult)
        => ((delegate* unmanaged[Stdcall]<void*, DxcBuffer*, nint*, uint, void*, Guid*, void**, int>)
            Slot(compiler, 3))(compiler, source, argv, argc, includeHandler, riid, ppResult);

    // IDxcOperationResult: 3 GetStatus, 4 GetResult, 5 GetErrorBuffer
    public static int ResultGetStatus(void* r, int* pStatus)
        => ((delegate* unmanaged[Stdcall]<void*, int*, int>)Slot(r, 3))(r, pStatus);
    public static int ResultGetResult(void* r, void** ppBlob)
        => ((delegate* unmanaged[Stdcall]<void*, void**, int>)Slot(r, 4))(r, ppBlob);

    // IDxcResult: 5 GetErrorBuffer, 6 HasOutput, 7 GetOutput
    public static int ResultGetErrorBuffer(void* r, void** ppErrors)
        => ((delegate* unmanaged[Stdcall]<void*, void**, int>)Slot(r, 5))(r, ppErrors);

    // IDxcBlob: 3 GetBufferPointer, 4 GetBufferSize
    public static void* BlobPtr(void* b) => ((delegate* unmanaged[Stdcall]<void*, void*>)Slot(b, 3))(b);
    public static nuint BlobSize(void* b) => ((delegate* unmanaged[Stdcall]<void*, nuint>)Slot(b, 4))(b);

    // IDxcBlobEncoding: 5 GetEncoding
    public static int BlobGetEncoding(void* b, int* pKnown, uint* pCodePage)
        => ((delegate* unmanaged[Stdcall]<void*, int*, uint*, int>)Slot(b, 5))(b, pKnown, pCodePage);

    // IDxcBlobUtf8: 6 GetStringPointer, 7 GetStringLength
    public static byte* BlobUtf8Ptr(void* b) => ((delegate* unmanaged[Stdcall]<void*, byte*>)Slot(b, 6))(b);
    public static nuint BlobUtf8Len(void* b) => ((delegate* unmanaged[Stdcall]<void*, nuint>)Slot(b, 7))(b);

    // IDxcVersionInfo: 3 GetVersion, 4 GetFlags
    public static int VersionInfoGetVersion(void* v, uint* major, uint* minor)
        => ((delegate* unmanaged[Stdcall]<void*, uint*, uint*, int>)Slot(v, 3))(v, major, minor);

    public static string Hex(int hr) => "0x" + ((uint)hr).ToString("X8");
}
