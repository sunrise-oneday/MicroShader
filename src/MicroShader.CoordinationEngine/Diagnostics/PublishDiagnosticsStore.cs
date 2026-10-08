using MicroShader.Domain;

namespace MicroShader.CoordinationEngine.Diagnostics;

/// <summary>
/// 待发布的诊断快照（整份替换语义）。
/// </summary>
public sealed class PublishPayload
{
    public required string Uri { get; init; }

    public required int Version { get; init; }

    public required int Epoch { get; init; }

    /// <summary>合并后的整份诊断集合。</summary>
    public required IReadOnlyList<ShaderDiagnosticItem> Items { get; init; }

    /// <summary>参与合并的 Pass 槽位数量。</summary>
    public required int PassCount { get; init; }
}

/// <summary>
/// 按 Pass 维护、整份发布的诊断集合（ADR-008 / ADR-024 的落地形态）。
/// </summary>
/// <remarks>
/// "为什么必须按 Pass 维护再合并"（v2.0 第 9 条）："publishDiagnostics" 是
/// "整份替换"语义，而打字防抖只重算「聚焦的那个 Pass」。如果只把聚焦 Pass 的结果推出去，
/// 其余 Pass 的红线会被一起抹掉，用户看到的是反复闪烁。所以：每次分析只更新它覆盖的 Pass 槽位，
/// 发布时"合并全部槽位"再整份推送。
/// "epoch 门控"：同一 URI 关闭再打开后客户端的 version 会回到 1，
/// 因此丢弃条件必须是「(uri, epoch) 不匹配 "或" version 落后」，不能只看 version。
/// </remarks>
public sealed class PublishDiagnosticsStore
{
    private sealed class DocEntry
    {
        public int Epoch;
        public int Version;
        public readonly Dictionary<int, IReadOnlyList<ShaderDiagnosticItem>> Passes = new();
    }

    private readonly Dictionary<string, DocEntry> _documents = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public int DocumentCount
    {
        get
        {
            lock (_gate) return _documents.Count;
        }
    }

    /// <summary>开始跟踪一份文档（didOpen）。同 URI 重开会清空旧槽位并换 epoch。</summary>
    public void Open(string uri, int epoch, int version)
    {
        lock (_gate)
        {
            _documents[uri] = new DocEntry { Epoch = epoch, Version = version };
        }
    }

    /// <summary>
    /// 写入某个 Pass 的诊断槽位。返回 "false" 表示 epoch 不匹配（旧会话的迟到结果，应丢弃）。
    /// </summary>
    public bool SetPass(string uri, int epoch, int version, int passIndex, IReadOnlyList<ShaderDiagnosticItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        lock (_gate)
        {
            if (!_documents.TryGetValue(uri, out var entry) || entry.Epoch != epoch) return false;

            entry.Version = Math.Max(entry.Version, version);
            entry.Passes[passIndex] = items;
            return true;
        }
    }

    /// <summary>删除某个 Pass 槽位（Pass 被删掉时用，避免旧红线永久残留）。</summary>
    public bool RemovePass(string uri, int epoch, int passIndex)
    {
        lock (_gate)
        {
            if (!_documents.TryGetValue(uri, out var entry) || entry.Epoch != epoch) return false;
            return entry.Passes.Remove(passIndex);
        }
    }

    /// <summary>仅保留给定的 Pass 槽位集合（全量重算时用，清掉已消失的 Pass）。</summary>
    public bool RetainOnly(string uri, int epoch, IReadOnlyList<int> keep)
    {
        lock (_gate)
        {
            if (!_documents.TryGetValue(uri, out var entry) || entry.Epoch != epoch) return false;

            var keepSet = new HashSet<int>(keep);
            var stale = new List<int>();
            foreach (var key in entry.Passes.Keys)
            {
                if (!keepSet.Contains(key)) stale.Add(key);
            }

            foreach (var key in stale) entry.Passes.Remove(key);
            return true;
        }
    }

    /// <summary>停止跟踪（didClose）。调用方负责推一份空诊断清掉客户端的红线。</summary>
    public void Close(string uri)
    {
        lock (_gate)
        {
            _documents.Remove(uri);
        }
    }

    /// <summary>取当前记录的版本号。</summary>
    public int VersionOf(string uri)
    {
        lock (_gate)
        {
            return _documents.TryGetValue(uri, out var entry) ? entry.Version : 0;
        }
    }

    /// <summary>已占用的 Pass 槽位数。</summary>
    public int PassSlotCount(string uri)
    {
        lock (_gate)
        {
            return _documents.TryGetValue(uri, out var entry) ? entry.Passes.Count : 0;
        }
    }

    /// <summary>
    /// 合并全部 Pass 槽位，产出一份可整份推送的载荷。
    /// </summary>
    public bool TryBuildMerged(string uri, int epoch, out PublishPayload? payload)
    {
        lock (_gate)
        {
            payload = null;
            if (!_documents.TryGetValue(uri, out var entry) || entry.Epoch != epoch) return false;

            var merged = new List<ShaderDiagnosticItem>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var passIndex in entry.Passes.Keys.OrderBy(static k => k))
            {
                foreach (var item in entry.Passes[passIndex])
                {
                    // 共享块（HLSLINCLUDE）会被多个 Pass 单元重复报出，合并时必须按位置去重，
                    // 否则同一行会显示多条一模一样的红线。
                    var key = string.Concat(
                        item.Line, ":", item.Column, ":", (int)item.Severity, ":",
                        item.Code ?? string.Empty, ":", item.Message);
                    if (seen.Add(key)) merged.Add(item);
                }
            }

            merged.Sort(static (a, b) =>
            {
                var byLine = a.Line.CompareTo(b.Line);
                return byLine != 0 ? byLine : a.Column.CompareTo(b.Column);
            });

            payload = new PublishPayload
            {
                Uri = uri,
                Version = entry.Version,
                Epoch = entry.Epoch,
                Items = merged,
                PassCount = entry.Passes.Count,
            };
            return true;
        }
    }
}
