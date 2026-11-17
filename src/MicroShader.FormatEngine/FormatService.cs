using System.Text.Json;

namespace MicroShader.FormatEngine;

/// <summary>
/// 把 HlslFormatter 接到 LSP 的 textDocument/formatting 请求上的适配器。
/// </summary>
/// <remarks>
/// 与导航、补全引擎同构：本类不认识任何协议常量，只负责
/// 拿文本、出 TextEdit、写进 Utf8JsonWriter。
/// 方法返回 false 表示没有需要改动的编辑，调用方据此回 null
/// （与 FoldingRangeBuilder.TryWrite 的约定一致）。
/// </remarks>
public sealed class FormatService
{
    /// <summary>
    /// 写出格式化编辑。
    /// </summary>
    /// <returns>有编辑返回 true；无编辑返回 false。</returns>
    public bool ProvideFormatting(
        string uri,
        string filePath,
        string text,
        FormatOptions options,
        Utf8JsonWriter writer)
    {
        var edits = HlslFormatter.FormatEdits(text, options);
        if (edits.Count == 0)
        {
            return false;
        }

        writer.WriteStartArray();
        foreach (var edit in edits)
        {
            writer.WriteStartObject();
            writer.WritePropertyName("range");
            writer.WriteStartObject();
            writer.WritePropertyName("start");
            WritePosition(writer, edit.StartLine, edit.StartCharacter);
            writer.WritePropertyName("end");
            WritePosition(writer, edit.EndLine, edit.EndCharacter);
            writer.WriteEndObject();
            writer.WriteString("newText", edit.NewText);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        return true;
    }

    private static void WritePosition(Utf8JsonWriter writer, int line, int character)
    {
        writer.WriteStartObject();
        writer.WriteNumber("line", line);
        writer.WriteNumber("character", character);
        writer.WriteEndObject();
    }
}
