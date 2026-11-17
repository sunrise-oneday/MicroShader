using MicroShader.ContextEngine;

namespace MicroShader.DiagnosticEngine;

/// <summary>
/// 第一层来源：从 Editor.log 里抠出 Unity 自己打印的 "Platform defines:" 行（v2.0 清单第 15 条）。
/// </summary>
/// <remarks>
/// 两个候选位置"都要探"："&lt;Project&gt;/Logs/Editor.log"（Unity 6.x 起默认写工程级；
/// 2022.3 只在用 "-logFile" 时才出现）与 "%LOCALAPPDATA%/Unity/Editor/Editor.log"（全局日志）。
/// 日志文件可能非常大，因此这里"流式逐行"扫描并只保留最后一个命中，不把整个文件读进内存；
/// 另外设一个字节上限，避免病态日志把启动时间拖垮。
/// 已确认 "Platform defines:" 行"不含" "SHADER_STAGE_*"（全生态 grep 0 命中）——
/// 阶段维必须与平台维分开处理，绝不能指望日志把阶段宏也带出来。
/// </remarks>
public static class EditorLogPlatformProbe
{
    private const string Marker = "Platform defines:";
    private const long ScanLimitBytes = 64L * 1024 * 1024;

    /// <summary>尝试从两个候选日志位置读取平台宏。</summary>
    public static bool TryRead(UnityProjectLayout layout, out PlatformMacroSet set)
    {
        ArgumentNullException.ThrowIfNull(layout);

        set = new PlatformMacroSet();
        var projectLog = VfsPath.Combine(layout.ProjectRoot, "Logs/Editor.log");
        var globalLog = GlobalEditorLogPath();

        var found = TryReadFile(projectLog, set, out var projectLine);
        if (found)
        {
            set.AddNote($"来源：{projectLog}（工程级 Editor.log）");
            return true;
        }

        if (globalLog is not null && TryReadFile(globalLog, set, out _))
        {
            set.AddNote($"来源：{globalLog}（全局 Editor.log）");
            return true;
        }

        set.AddNote("两处 Editor.log 都没有 Platform defines 行（工程级：" +
                    projectLog + "；全局：" + (globalLog ?? "<不可用>") + "）");
        _ = projectLine;
        return false;
    }

    /// <summary>全局 Editor.log 路径（"%LOCALAPPDATA%/Unity/Editor/Editor.log"）。</summary>
    public static string? GlobalEditorLogPath()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return string.IsNullOrEmpty(localAppData)
            ? null
            : VfsPath.Combine(VfsPath.Normalize(localAppData), "Unity/Editor/Editor.log");
    }

    /// <summary>解析一行 "Platform defines: A B C"。</summary>
    public static bool TryParseLine(ReadOnlySpan<char> line, PlatformMacroSet into, out string source)
    {
        ArgumentNullException.ThrowIfNull(into);
        source = string.Empty;

        var index = line.IndexOf(Marker, StringComparison.Ordinal);
        if (index < 0)
        {
            return false;
        }

        var rest = line[(index + Marker.Length)..];
        var count = 0;
        foreach (var range in rest.Split(' '))
        {
            var token = rest[range].Trim();
            if (token.Length == 0)
            {
                continue;
            }

            var equals = token.IndexOf('=');
            if (equals > 0)
            {
                into.Add(token[..equals].ToString(), token[(equals + 1)..].ToString(), MacroProvenance.Measured, "Editor.log Platform defines");
            }
            else
            {
                into.Add(token.ToString(), "1", MacroProvenance.Measured, "Editor.log Platform defines");
            }

            count++;
        }

        source = count.ToString(System.Globalization.CultureInfo.InvariantCulture) + " 项";
        return count > 0;
    }

    private static bool TryReadFile(string normalizedPath, PlatformMacroSet set, out string source)
    {
        source = string.Empty;
        var native = VfsPath.ToNative(normalizedPath);
        if (!File.Exists(native))
        {
            return false;
        }

        try
        {
            string? lastMatch = null;
            long scanned = 0;

            using var stream = new FileStream(native, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);

            while (reader.ReadLine() is { } line)
            {
                scanned += line.Length + 2;
                if (line.Contains(Marker, StringComparison.Ordinal))
                {
                    lastMatch = line;
                }

                if (scanned > ScanLimitBytes)
                {
                    break;
                }
            }

            return lastMatch is not null && TryParseLine(lastMatch, set, out source);
        }
        catch (IOException)
        {
            // 日志正被 Unity 以独占方式追加写：探不到就探不到，绝不因此让整条链路失败。
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}
