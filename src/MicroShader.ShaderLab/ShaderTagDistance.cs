namespace MicroShader.ShaderLab;

/// <summary>
/// 有界编辑距离：只关心「是否 &lt;= k」，用于「你是不是想写 X」的近似命中判定。
/// </summary>
/// <remarks>
/// "为什么不是朴素 Levenshtein"：朴素实现要填满 n×m 整张矩阵（O(n·m)），
/// 而校验器是「每个标签值 × 每个已知值」都要算一次。真机上标签值最长 30 字符、
/// 合法集合 40+ 个，单文件最坏 10 个标签 → 1 万次以上矩阵填充。
/// "采用的算法（Ukkonen 带宽截断）"：距离超过 k 的对角线永远不可能回到 k 以内，
/// 因此只填 |i-j| &lt;= k 的带内单元格；再配合「整行最小值已 &gt; k 即早退」，
/// 单次比较从 O(n·m) 降到 O(k·n) —— k 固定为 2 时是 15 倍左右的常数级下降。
/// "为什么不用 BK-tree / Levenshtein 自动机"：那两者服务于上千规模的词典
/// （BK-tree 建树本身就要 O(N log N) 次距离计算，自动机要构造 Trie + 状态转移）。
/// 本场景候选集是 3~45 个短字符串，建树成本远高于线性扫描，属过度设计。
/// 长度分桶预过滤（|len 差| &gt; k 直接跳过）已能挡掉绝大多数候选。
/// 零堆分配：全部用 "stackalloc"，长度超界截断而非分配。
/// </remarks>
public static class ShaderTagDistance
{
    /// <summary>参与比较的最大字符数；超长部分截断（标签值实测最长 30 字符，64 有充足余量）。</summary>
    private const int MaxLength = 64;

    private const int Infinite = 1 << 20;

    /// <summary>
    /// 判断 <paramref name="a"/> 与 <paramref name="b"/> 的编辑距离是否 "&lt;= maxDistance"。
    /// </summary>
    /// <param name="distance">命中时给出确切距离；未命中时为 "maxDistance + 1"。</param>
    public static bool TryWithin(ReadOnlySpan<char> a, ReadOnlySpan<char> b, int maxDistance, out int distance)
    {
        distance = maxDistance + 1;

        if (a.Length > MaxLength) a = a[..MaxLength];
        if (b.Length > MaxLength) b = b[..MaxLength];

        // 长度分桶预过滤：长度差本身就超过阈值时，距离必然超界。
        if (Math.Abs(a.Length - b.Length) > maxDistance)
        {
            return false;
        }

        var n = a.Length;
        var m = b.Length;

        if (n == 0)
        {
            distance = m;
            return m <= maxDistance;
        }

        if (m == 0)
        {
            distance = n;
            return n <= maxDistance;
        }

        if (a.SequenceEqual(b))
        {
            distance = 0;
            return true;
        }

        Span<int> prev = stackalloc int[m + 1];
        Span<int> curr = stackalloc int[m + 1];

        for (var j = 0; j <= m; j++)
        {
            prev[j] = j <= maxDistance ? j : Infinite;
        }

        for (var i = 1; i <= n; i++)
        {
            var lo = Math.Max(1, i - maxDistance);
            var hi = Math.Min(m, i + maxDistance);

            curr[0] = i;
            if (lo > 1)
            {
                curr[lo - 1] = Infinite; // 带外左端
            }

            var rowMin = Infinite;

            for (var j = lo; j <= hi; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                var delete = prev[j] + 1;
                var insert = curr[j - 1] + 1;
                var substitute = prev[j - 1] + cost;

                var value = Math.Min(Math.Min(delete, insert), substitute);
                curr[j] = value;

                if (value < rowMin)
                {
                    rowMin = value;
                }
            }

            if (hi < m)
            {
                curr[hi + 1] = Infinite; // 带外右端：同时保证下一行读 prev[hi+1] 时不会读到陈旧值
            }

            // Ukkonen 早退：行最小值单调不减，一旦超过阈值，后面所有行也只会更大。
            if (rowMin > maxDistance)
            {
                distance = rowMin;
                return false;
            }

            // Span<int> 是 ref struct，无法走元组交换，用临时变量换手。
            var swap = prev;
            prev = curr;
            curr = swap;
        }

        distance = prev[m];
        return distance <= maxDistance;
    }
}
