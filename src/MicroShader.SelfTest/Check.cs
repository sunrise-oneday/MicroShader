namespace MicroShader.SelfTest;

/// <summary>断言失败。</summary>
internal sealed class AssertionException : Exception
{
    public AssertionException(string message) : base(message)
    {
    }
}

/// <summary>极简断言器（零外部依赖，便于 Native AOT 自检 CLI 复用）。</summary>
internal static class Check
{
    public static void True(bool condition, string message)
    {
        if (!condition)
        {
            throw new AssertionException(message);
        }
    }

    public static void False(bool condition, string message)
    {
        if (condition)
        {
            throw new AssertionException(message);
        }
    }

    public static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new AssertionException($"{message}\n  期望: {expected}\n  实际: {actual}");
        }
    }

    public static void NotNull(object? value, string message)
    {
        if (value is null)
        {
            throw new AssertionException(message + "（实际为 null）");
        }
    }

    /// <summary>断言两个字符串序列逐个相等（顺序敏感）。</summary>
    public static void SequenceEqual(IEnumerable<string> expected, IEnumerable<string> actual, string message)
    {
        var e = expected.ToArray();
        var a = actual.ToArray();
        if (e.Length != a.Length || !e.SequenceEqual(a, StringComparer.Ordinal))
        {
            throw new AssertionException($"{message}\n  期望: [{string.Join(", ", e)}]\n  实际: [{string.Join(", ", a)}]");
        }
    }

    /// <summary>断言集合包含给定元素。</summary>
    public static void Contains(IEnumerable<string> values, string expected, string message)
    {
        if (!values.Contains(expected, StringComparer.Ordinal))
        {
            throw new AssertionException($"{message}\n  期望包含: {expected}\n  实际: [{string.Join(", ", values)}]");
        }
    }

    /// <summary>断言集合不包含给定元素。</summary>
    public static void DoesNotContain(IEnumerable<string> values, string unexpected, string message)
    {
        if (values.Contains(unexpected, StringComparer.Ordinal))
        {
            throw new AssertionException($"{message}\n  不应包含: {unexpected}\n  实际: [{string.Join(", ", values)}]");
        }
    }

    /// <summary>断言集合中存在满足条件的元素，并返回该元素。</summary>
    public static T Single<T>(IEnumerable<T> values, Func<T, bool> predicate, string message)
    {
        var matches = values.Where(predicate).ToArray();
        if (matches.Length != 1)
        {
            throw new AssertionException($"{message}\n  期望恰好 1 个匹配，实际 {matches.Length} 个");
        }

        return matches[0];
    }
}
