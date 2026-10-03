namespace MicroShader.CoordinationEngine.Documents;

/// <summary>
/// 一份"不可变"的影子文档快照。
/// </summary>
/// <remarks>
/// v2.0 修正清单第 8 条推翻了原设计的「共享字符缓冲 + 借用契约」：借用契约只约束借用方，
/// 不约束所有者 —— 读线程下一次 "didChange" 替换/复用同一块缓冲，会让正在扫描的
/// worker 读到"半新半旧"的文本（症状是随机冒出不存在的语法错误或漏报，极难复现）。
///
/// "本实现的取舍（必须承认）"：每次变更发布一份新快照，旧快照在最后一个持有者
/// 松开引用后由 GC 回收。因此本模块"不具备"「每次击键零堆分配」的性质。
/// 更关键的是：ADR-008 选定 "textDocumentSync: Full"，意味着"每一次 didChange 都会把
/// 整份文档文本送进来" —— 一次与文档等大的 string 分配是"协议选择本身带来的固有成本"，
/// 不是快照设计引入的。诚实的目标是「"有界"」而非「零」：合并调度保证只有最新快照会被派发，
/// 解析期的临时缓冲一律走 "ArrayPool"。
/// </remarks>
public sealed class DocumentSnapshot
{
    /// <summary>LSP 文档 URI。</summary>
    public required string Uri { get; init; }

    /// <summary>物理绝对路径。</summary>
    public required string FilePath { get; init; }

    /// <summary>LSP 文档版本号（由客户端递增）。</summary>
    public required int Version { get; init; }

    /// <summary>
    /// 本次 "didOpen" 的世代号。同一 URI 关闭再打开后客户端的 version 可能回到 1
    /// （VS Code 行为），因此「版本倒退」判断必须以 epoch 为界（v2.0 第 9 条）。
    /// </summary>
    public required int Epoch { get; init; }

    /// <summary>不可变全文。</summary>
    public required string Text { get; init; }

    // -1 表示尚未统计。属性 getter 原先每次都线性扫全文（LSP 的 didOpen 日志就会读它），
    // 改成首次访问算一次后缓存：Text 不可变，结果恒定；int 写入是原子的，并发重复计算无害。
    private int _lineCount = -1;

    /// <summary>行数（按 "\n" 计）。首次访问为 O(L)，之后 O(1)。</summary>
    public int LineCount
    {
        get
        {
            if (_lineCount >= 0)
            {
                return _lineCount;
            }

            var count = 1;
            foreach (var ch in Text)
            {
                if (ch == '\n') count++;
            }

            _lineCount = count;
            return count;
        }
    }
}
