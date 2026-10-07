using System.Diagnostics.CodeAnalysis;

namespace MicroShader.CoordinationEngine.Documents;

/// <summary>
/// 文档影子仓储：维系编辑器当前打开的每一份 ".shader" 的最新不可变快照。
/// </summary>
/// <remarks>
/// "并发模型（诚实版）"：写入方只有一个（Stdio 读线程），读取方是线程池上的
/// 若干分析 worker。原设计宣称此处「无锁」，但我们已经接受了「每次击键一次 O(文档) 分配」，
/// 在这种量级下为省一次无竞争 "lock"（约 20ns）而去冒数据竞争的风险，是在优化错误的东西。
/// 因此这里用最朴素的一把锁，换来的是可以推理的正确性。
/// 快照一旦发布就不再改写，worker 拿到的永远是稳定文本（torn read 免疫）。
/// </remarks>
public sealed class DocumentShadowStore
{
    private readonly Dictionary<string, DocumentSnapshot> _documents = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    /// <summary>
    /// 全局单调递增的 epoch 计数器。
    /// </summary>
    /// <remarks>
    /// 第一版把 epoch 存在 "uri → int" 表里并在 "Close" 时删除，结果是"重开后 epoch 又回到 1"
    /// —— 与旧会话完全撞号，epoch 门控形同虚设（自检 "Doc.EpochOnReopen" 抓到了这一点）。
    /// 改成全局计数器后：epoch 在进程生命周期内"永不复用"，也不需要为已关闭的文档保留任何状态。
    /// </remarks>
    private int _nextEpoch;

    /// <summary>
    /// 最近一次「活跃」文档。LSP 3.17 "不存在"任何光标/焦点通知
    /// （"didFocus"/"activeTextEditor"/"focused" 全文检索命中数为 0，v2.0 第 12 条），
    /// 因此这里只能是"启发式"：最近发生变更的文档；或由本服务私有通知
    /// "$/microshader/activeDocument" 显式设定。
    /// </summary>
    public string? ActiveDocumentUri { get; private set; }

    public int Count
    {
        get
        {
            lock (_gate) return _documents.Count;
        }
    }

    /// <summary>枚举当前所有打开的文档（uri + 快照），调用方拿到的是一份拷贝。</summary>
    public IEnumerable<KeyValuePair<string, DocumentSnapshot>> EnumerateOpen()
    {
        lock (_gate)
        {
            return _documents.ToArray();
        }
    }

    /// <summary>打开文档：分配新的 epoch 并发布首份快照。</summary>
    public DocumentSnapshot Open(string uri, string filePath, string text, int version)
    {
        ArgumentException.ThrowIfNullOrEmpty(uri);
        lock (_gate)
        {
            var epoch = ++_nextEpoch;

            var snapshot = new DocumentSnapshot
            {
                Uri = uri,
                FilePath = filePath,
                Version = version,
                Epoch = epoch,
                Text = text,
            };
            _documents[uri] = snapshot;
            ActiveDocumentUri = uri;
            return snapshot;
        }
    }

    /// <summary>变更文档：发布新快照。文档未打开时返回 "null"（协议层据此忽略该事件）。</summary>
    public DocumentSnapshot? Change(string uri, string text, int version)
    {
        lock (_gate)
        {
            if (!_documents.TryGetValue(uri, out var current)) return null;

            var snapshot = new DocumentSnapshot
            {
                Uri = uri,
                FilePath = current.FilePath,
                Version = version,
                Epoch = current.Epoch,
                Text = text,
            };
            _documents[uri] = snapshot;
            ActiveDocumentUri = uri;
            return snapshot;
        }
    }

    /// <summary>关闭文档并归还快照。</summary>
    public bool Close(string uri)
    {
        lock (_gate)
        {
            var removed = _documents.Remove(uri);
            if (string.Equals(ActiveDocumentUri, uri, StringComparison.Ordinal)) ActiveDocumentUri = null;
            return removed;
        }
    }

    /// <summary>取最新快照。</summary>
    public bool TryGet(string uri, [NotNullWhen(true)] out DocumentSnapshot? snapshot)
    {
        lock (_gate)
        {
            return _documents.TryGetValue(uri, out snapshot);
        }
    }

    /// <summary>
    /// 取"指定版本"的快照：只有客户端版本号完全吻合才返回。
    /// worker 回传结果时用它做「出队即校验」。
    /// </summary>
    public bool TryGetAtVersion(string uri, int version, [NotNullWhen(true)] out DocumentSnapshot? snapshot)
    {
        lock (_gate)
        {
            if (_documents.TryGetValue(uri, out var found) && found.Version == version)
            {
                snapshot = found;
                return true;
            }

            snapshot = null;
            return false;
        }
    }

    /// <summary>取当前 epoch（用于诊断门控）。文档未打开时返回 0。</summary>
    public int EpochOf(string uri)
    {
        lock (_gate)
        {
            return _documents.TryGetValue(uri, out var snapshot) ? snapshot.Epoch : 0;
        }
    }

    /// <summary>显式设定活跃文档（私有通知 "$/microshader/activeDocument" 使用）。</summary>
    public void SetActiveDocument(string uri)
    {
        lock (_gate)
        {
            ActiveDocumentUri = uri;
        }
    }

    /// <summary>枚举当前所有打开的 URI（自检/诊断用）。</summary>
    public string[] Uris()
    {
        lock (_gate)
        {
            var keys = new string[_documents.Count];
            _documents.Keys.CopyTo(keys, 0);
            Array.Sort(keys, StringComparer.Ordinal);
            return keys;
        }
    }
}
