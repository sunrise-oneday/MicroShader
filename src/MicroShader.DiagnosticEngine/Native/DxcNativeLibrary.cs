using System.Runtime.InteropServices;

namespace MicroShader.DiagnosticEngine.Native;

/// <summary>
/// dxcompiler.dll 的定位与加载（ADR-012 / ADR-007）。
///
/// 实测约束（probes/dxc-aot，ADR-013）：
///  * Native AOT 下 NATIVE_DLL_SEARCH_DIRECTORIES 为 null，裸名 LoadLibrary("dxcompiler.dll") 失败 err=126；
///  * 必须绝对路径 LoadLibraryW 后再 NativeLibrary.TryGetExport(handle, "DxcCreateInstance")；
///  * 只有 AppContext.BaseDirectory 是可靠定位点（Assembly.Location 为空串、CodeBase 抛异常）。
///
/// dxil.dll 自 1.8.2502 起非必需，但若随包提供则必须与 dxcompiler.dll 同目录成对送出。
/// </summary>
public sealed unsafe class DxcNativeLibrary : IDisposable
{
    private nint _module;
    private nint _dxilModule;
    private nint _createInstance;

    private DxcNativeLibrary(nint module, nint dxilModule, nint createInstance, string path, string? dxilPath)
    {
        _module = module;
        _dxilModule = dxilModule;
        _createInstance = createInstance;
        Path = path;
        DxilPath = dxilPath;
    }

    /// <summary>实际加载的 dxcompiler.dll 绝对路径。</summary>
    public string Path { get; }

    /// <summary>同目录成对的 dxil.dll 绝对路径（未提供则为 null）。</summary>
    public string? DxilPath { get; }

    public bool IsDisposed => _module == 0;

    /// <summary>DXC 版本号（从 compiler 实例的 IDxcVersionInfo 读取，与文档写的表层版本号无关）。</summary>
    public Version DxcVersion { get; private set; } = new(0, 0);

    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern nint LoadLibraryW(string lpFileName);

    [DllImport("kernel32", SetLastError = true)]
    private static extern int FreeLibrary(nint hModule);

    /// <summary>
    /// 在给定目录中加载 dxcompiler.dll（+ 可选 dxil.dll）。
    /// 失败原因写入 <paramref name="error"/>，绝不抛异常——模块加载失败是可恢复状态（无编译能力，但 LSP 仍活）。
    /// </summary>
    public static bool TryLoad(string directory, out DxcNativeLibrary? library, out string error)
    {
        library = null;
        error = string.Empty;
        if (string.IsNullOrEmpty(directory))
        {
            error = "未指定 dxcompiler.dll 所在目录";
            return false;
        }

        string compilerPath = System.IO.Path.Combine(directory, "dxcompiler.dll");
        if (!File.Exists(compilerPath))
        {
            error = $"未找到 {compilerPath}";
            return false;
        }

        nint module = LoadLibraryW(compilerPath);
        if (module == 0)
        {
            error = $"LoadLibraryW 失败：{compilerPath}（Win32 err={Marshal.GetLastWin32Error()}）";
            return false;
        }

        if (!NativeLibrary.TryGetExport(module, "DxcCreateInstance", out nint createInstance))
        {
            FreeLibrary(module);
            error = "dxcompiler.dll 中未导出 DxcCreateInstance";
            return false;
        }

        // dxil.dll 可选：自 DXC 1.8.2502 起非必需。存在才加载，且必须与 dxcompiler 同目录。
        nint dxil = 0;
        string? dxilPath = null;
        string candidateDxil = System.IO.Path.Combine(directory, "dxil.dll");
        if (File.Exists(candidateDxil))
        {
            dxil = LoadLibraryW(candidateDxil);
            if (dxil != 0)
            {
                dxilPath = candidateDxil;
            }
        }

        var lib = new DxcNativeLibrary(module, dxil, createInstance, compilerPath, dxilPath);

        // 版本必须来自 compiler 实例（IDxcUtils 不实现 IDxcVersionInfo，ADR-013 实测 E_NOINTERFACE）。
        // 注意：必须 QueryInterface 到 IDxcVersionInfo 之后才能调 GetVersion ——
        // IDxcCompiler3 的槽位 3 是 Compile，直接当版本接口调用会立刻 AV（本机已踩，见 Dxc.Load）。
        if (lib.TryCreateRaw(DxcApi.ClsidDxcCompiler, DxcApi.IidDxcCompiler3, out nint compiler))
        {
            Guid versionIid = DxcApi.IidDxcVersionInfo;
            void* versionInfo = null;
            if (DxcApi.QueryInterface((void*)compiler, &versionIid, &versionInfo) == 0 && versionInfo != null)
            {
                uint major = 0, minor = 0;
                if (DxcApi.VersionInfoGetVersion(versionInfo, &major, &minor) == 0)
                {
                    lib.DxcVersion = new Version((int)major, (int)minor);
                }

                DxcApi.Release(versionInfo);
            }

            DxcApi.Release((void*)compiler);
        }

        library = lib;
        return true;
    }

    public bool TryCreateCompiler(out nint instance) => TryCreateRaw(DxcApi.ClsidDxcCompiler, DxcApi.IidDxcCompiler3, out instance);

    public bool TryCreateUtils(out nint instance) => TryCreateRaw(DxcApi.ClsidDxcUtils, DxcApi.IidDxcUtils, out instance);

    private bool TryCreateRaw(Guid clsid, Guid iid, out nint instance)
    {
        instance = 0;
        if (_module == 0)
        {
            return false;
        }

        Guid c = clsid;
        Guid i = iid;
        void* pv = null;
        int hr = ((delegate* unmanaged[Stdcall]<Guid*, Guid*, void**, int>)_createInstance)(&c, &i, &pv);
        if (hr != 0 || pv == null)
        {
            return false;
        }

        instance = (nint)pv;
        return true;
    }

    public void Dispose()
    {
        if (_module == 0)
        {
            return;
        }

        // 先释放 dxil，再释放 dxcompiler（与加载顺序相反）。
        if (_dxilModule != 0)
        {
            FreeLibrary(_dxilModule);
            _dxilModule = 0;
        }

        FreeLibrary(_module);
        _module = 0;
        _createInstance = 0;
    }
}
