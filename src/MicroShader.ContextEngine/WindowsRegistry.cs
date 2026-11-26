using System.Runtime.InteropServices;

namespace MicroShader.ContextEngine;

/// <summary>
/// 极小注册表读取封装。
/// </summary>
/// <remarks>
/// 为什么不直接用 "Microsoft.Win32.Registry"：该 API 只在 "net*-windows" TFM 下可用，
/// 而本项目所有工程统一 "net10.0"（Native AOT 交付）。为了这一处兜底查询就把整条 TFM 链改成
/// "net10.0-windows" 不划算，引入 NuGet 包又违反「零第三方依赖」。
/// 直接 "LibraryImport" advapi32 是零依赖且 AOT 友好的做法。
/// 注册表只是"第三优先级"兜底（前两级是 Unity Hub 配置与 EditorInstance.json）。
/// </remarks>
internal static partial class WindowsRegistry
{
    internal const uint HkeyCurrentUser = 0x80000001;
    internal const uint HkeyLocalMachine = 0x80000002;

    private const uint KeyRead = 0x20019;
    private const uint RegSz = 1;
    private const uint RegExpandSz = 2;
    private const int ErrorSuccess = 0;

    /// <summary>在指定根键下读取一个字符串值；不存在或不可读时返回 null。</summary>
    internal static string? ReadString(uint rootKey, string subKey, string valueName)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        nint hive = 0;
        if (RegOpenKeyExW(rootKey, subKey, 0, KeyRead, out hive) != ErrorSuccess)
        {
            return null;
        }

        try
        {
            return QueryString(hive, valueName);
        }
        finally
        {
            RegCloseKey(hive);
        }
    }

    private static unsafe string? QueryString(nint hive, string valueName)
    {
        // 注册表值是 REG_SZ，长度上限远小于此；8KB 足够且不必走堆分配之外的处理。
        const int CapacityBytes = 8192;
        var buffer = new byte[CapacityBytes];
        var size = (uint)CapacityBytes;

        fixed (byte* p = buffer)
        {
            var rc = RegQueryValueExW(hive, valueName, 0, out var type, (nint)p, ref size);
            if (rc != ErrorSuccess || (type != RegSz && type != RegExpandSz) || size == 0)
            {
                return null;
            }

            // size 含结尾 NUL；按 UTF-16 截断。
            var charCount = (int)(size / 2);
            if (charCount > 0 && p[size - 2] == 0 && p[size - 1] == 0)
            {
                charCount--;
            }

            if (charCount <= 0)
            {
                return null;
            }

            var value = new string((char*)p, 0, charCount);

            if (type == RegExpandSz)
            {
                value = Environment.ExpandEnvironmentVariables(value);
            }

            return value.Length == 0 ? null : value;
        }
    }

    [LibraryImport("advapi32.dll", EntryPoint = "RegOpenKeyExW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial int RegOpenKeyExW(uint hKey, string lpSubKey, uint ulOptions, uint samDesired, out nint phkResult);

    [LibraryImport("advapi32.dll", EntryPoint = "RegQueryValueExW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial int RegQueryValueExW(nint hKey, string lpValueName, nint lpReserved, out uint lpType, nint lpData, ref uint lpcbData);

    [LibraryImport("advapi32.dll", EntryPoint = "RegCloseKey", SetLastError = true)]
    private static partial int RegCloseKey(nint hKey);
}
