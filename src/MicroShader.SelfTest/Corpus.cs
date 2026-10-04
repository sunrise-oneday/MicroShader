namespace MicroShader.SelfTest;

/// <summary>用例语料库定位与文本工具。</summary>
internal static class Corpus
{
    /// <summary>随产物复制的固定用例目录。</summary>
    public static string FixtureDirectory => Path.Combine(AppContext.BaseDirectory, "Fixtures");

    public static string PathOf(string fileName) => Path.Combine(FixtureDirectory, fileName);

    public static string Read(string fileName) => File.ReadAllText(PathOf(fileName));

    /// <summary>取 1-based 行号对应的整行文本（无行尾）。</summary>
    public static string LineAt(string text, int lineNumber)
    {
        var lines = text.Split('\n');
        if (lineNumber < 1 || lineNumber > lines.Length)
        {
            return string.Empty;
        }

        return lines[lineNumber - 1].TrimEnd('\r');
    }

    /// <summary>找第 <paramref name="occurrence"/> 次出现 <paramref name="needle"/> 的 1-based 行号。</summary>
    public static int LineNumberContaining(string text, string needle, int occurrence = 1)
    {
        var lines = text.Split('\n');
        var seen = 0;
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].Contains(needle, StringComparison.Ordinal))
            {
                seen++;
                if (seen == occurrence)
                {
                    return i + 1;
                }
            }
        }

        return -1;
    }
}
