namespace MicroShader.ContextEngine;

/// <summary>触发一次上下文重建的原因。</summary>
public enum VfsChangeReason : byte
{
    /// <summary>"Packages/manifest.json" 变化。</summary>
    ManifestChanged = 1,

    /// <summary>"Packages/packages-lock.json" 变化。</summary>
    LockChanged = 2,

    /// <summary>"ProjectSettings/ProjectVersion.txt" 变化（Unity 版本被切换）。</summary>
    ProjectVersionChanged = 3,

    /// <summary>"Library/MicroShader/endpoint.json" 出现/变化（Unity 桥接上线）。</summary>
    EndpointChanged = 4,

    /// <summary>"FileSystemWatcher" 内部缓冲区溢出（通知已整体丢失，必须全量重扫）。</summary>
    FileSystemOverflow = 5,

    /// <summary>周期心跳发现白名单文件指纹变化（watcher 漏报或目录当时还不存在）。</summary>
    HeartbeatDetected = 6,

    /// <summary>显式请求重扫。</summary>
    Manual = 7,
}

/// <summary>监视器选项。</summary>
public sealed record VfsWatcherOptions
{
    /// <summary>去抖窗口（设计区间 200–500ms）。</summary>
    public TimeSpan Debounce { get; init; } = TimeSpan.FromMilliseconds(350);

    /// <summary>周期心跳。Unity 会重建甚至删除 "Library/"，watcher 覆盖不到，只能靠心跳兜底。</summary>
    public TimeSpan Heartbeat { get; init; } = TimeSpan.FromMilliseconds(1500);
}

/// <summary>
/// 工程上下文变化监视器。
/// </summary>
/// <remarks>
/// "为什么只当 hint"："FileSystemWatcher" 在缓冲区溢出时会静默丢弃"全部"通知
/// （blanket notification）。若没有任何 "Error" 订阅者，这种丢失完全不可观测。
/// 因此这里必然订阅 "Error"，并且额外用周期心跳做全量指纹比对。
/// "白名单"：只关心 manifest / lock / ProjectVersion / endpoint 四个文件，
/// 绝不 watch 整个 "Library/" 或 "Temp/"（Temp 下每秒都有写入）。
/// </remarks>
public sealed class VfsWatcher : IDisposable
{
    private const int InternalBufferSize = 64 * 1024;

    private readonly UnityProjectLayout _layout;
    private readonly Action<VfsChangeReason> _onChanged;
    private readonly VfsWatcherOptions _options;
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly Dictionary<string, FileFingerprint> _fingerprints = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _gate = new();
    private readonly Timer _debounceTimer;
    private readonly Timer _heartbeatTimer;

    private VfsChangeReason _pendingReason;
    private bool _started;
    private bool _disposed;

    public VfsWatcher(UnityProjectLayout layout, Action<VfsChangeReason> onChanged, VfsWatcherOptions? options = null)
    {
        _layout = layout ?? throw new ArgumentNullException(nameof(layout));
        _onChanged = onChanged ?? throw new ArgumentNullException(nameof(onChanged));
        _options = options ?? new VfsWatcherOptions();

        _debounceTimer = new Timer(OnDebounceElapsed, null, Timeout.Infinite, Timeout.Infinite);
        _heartbeatTimer = new Timer(OnHeartbeatElapsed, null, Timeout.Infinite, Timeout.Infinite);

        lock (_gate)
        {
            CaptureFingerprints();
        }
    }

    /// <summary>开始监视。</summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_started)
        {
            return;
        }

        _started = true;

        // Packages/：manifest.json 与 packages-lock.json。IncludeSubdirectories=false，
        // 否则包目录里每次解压都会淹没缓冲区。
        AttachWatcher(_layout.PackagesDirectory, "*.json", subdirectories: false);
        AttachWatcher(VfsPath.Combine(_layout.ProjectRoot, "ProjectSettings"), "ProjectVersion.txt", subdirectories: false);

        _heartbeatTimer.Change(_options.Heartbeat, _options.Heartbeat);
    }

    private void AttachWatcher(string directory, string filter, bool subdirectories)
    {
        var native = VfsPath.ToNative(directory);
        if (!Directory.Exists(native))
        {
            // 目录还不存在：交给心跳兜底，不主动创建（LSP 不该在用户工程里造目录）。
            return;
        }

        var watcher = new FileSystemWatcher(native)
        {
            Filter = filter,
            IncludeSubdirectories = subdirectories,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.DirectoryName,

            // 必须在 EnableRaisingEvents 之前设置：之后再改不会重建内核缓冲区。
            InternalBufferSize = InternalBufferSize,
        };

        watcher.Changed += OnWatched;
        watcher.Created += OnWatched;
        watcher.Deleted += OnWatched;
        watcher.Renamed += OnRenamed;
        watcher.Error += OnWatcherError;
        watcher.EnableRaisingEvents = true;

        _watchers.Add(watcher);
    }

    private void OnWatched(object sender, FileSystemEventArgs e) => Signal(Classify(e.FullPath));

    private void OnRenamed(object sender, RenamedEventArgs e)
    {
        // Renamed 回调里 Name/OldName 在极端情况下可能为 null；容忍而不是抛。
        var path = e.FullPath;
        if (string.IsNullOrEmpty(path))
        {
            Signal(VfsChangeReason.FileSystemOverflow);
            return;
        }

        Signal(Classify(path));
    }

    private void OnWatcherError(object sender, ErrorEventArgs e)
        => Signal(VfsChangeReason.FileSystemOverflow);

    private VfsChangeReason Classify(string fullPath)
    {
        var normalized = VfsPath.Normalize(fullPath);
        var name = VfsPath.GetFileName(normalized);

        if (name.Equals("manifest.json", StringComparison.OrdinalIgnoreCase))
        {
            return VfsChangeReason.ManifestChanged;
        }

        if (name.Equals("packages-lock.json", StringComparison.OrdinalIgnoreCase))
        {
            return VfsChangeReason.LockChanged;
        }

        if (name.Equals("ProjectVersion.txt", StringComparison.OrdinalIgnoreCase))
        {
            return VfsChangeReason.ProjectVersionChanged;
        }

        if (name.Equals("endpoint.json", StringComparison.OrdinalIgnoreCase))
        {
            return VfsChangeReason.EndpointChanged;
        }

        return VfsChangeReason.ManifestChanged;
    }

    private void Signal(VfsChangeReason reason)
    {
        if (_disposed)
        {
            return;
        }

        lock (_gate)
        {
            // 溢出优先级最高：它表示「有东西丢了」，任何更细的原因都不能掩盖它。
            if (_pendingReason == 0 || reason == VfsChangeReason.FileSystemOverflow)
            {
                _pendingReason = reason;
            }
        }

        try
        {
            _debounceTimer.Change(_options.Debounce, Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException)
        {
            // 与 Dispose 竞争，忽略。
        }
    }

    private void OnDebounceElapsed(object? state)
    {
        VfsChangeReason reason;
        lock (_gate)
        {
            reason = _pendingReason;
            _pendingReason = 0;
        }

        if (reason == 0)
        {
            return;
        }

        lock (_gate)
        {
            CaptureFingerprints();
        }

        Raise(reason);
    }

    private void OnHeartbeatElapsed(object? state)
    {
        if (_disposed)
        {
            return;
        }

        // 指纹表会被心跳与去抖两条计时器线程同时触碰，必须串行化；
        // 回调本身放在锁外触发，避免宿主逻辑反噬锁。
        bool changed;
        lock (_gate)
        {
            changed = HasFingerprintChanged();
            if (changed)
            {
                CaptureFingerprints();
            }
        }

        if (changed)
        {
            Raise(VfsChangeReason.HeartbeatDetected);
        }
    }

    private void Raise(VfsChangeReason reason)
    {
        try
        {
            _onChanged(reason);
        }
        catch (Exception)
        {
            // 回调是宿主逻辑；它抛异常不能把监视器计时器线程带崩。
        }
    }

    /// <remarks>调用方必须已持有 "_gate"。</remarks>
    private bool HasFingerprintChanged()
    {
        foreach (var (path, previous) in _fingerprints)
        {
            if (FileFingerprint.Capture(path) != previous)
            {
                return true;
            }
        }

        return false;
    }

    /// <remarks>调用方必须已持有 "_gate"。</remarks>
    private void CaptureFingerprints()
    {
        foreach (var path in WatchedPaths())
        {
            _fingerprints[path] = FileFingerprint.Capture(path);
        }
    }

    private IEnumerable<string> WatchedPaths()
    {
        yield return _layout.ManifestPath;
        yield return _layout.LockFilePath;
        yield return _layout.ProjectVersionPath;
        yield return _layout.EndpointFilePath;
        yield return _layout.UnityLockFilePath;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        foreach (var watcher in _watchers)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
        }

        _watchers.Clear();
        _debounceTimer.Dispose();
        _heartbeatTimer.Dispose();
    }

    private readonly record struct FileFingerprint(bool Exists, long Length, long LastWriteUtcTicks)
    {
        public static FileFingerprint Capture(string normalizedPath)
        {
            try
            {
                var info = new FileInfo(VfsPath.ToNative(normalizedPath));
                return info.Exists
                    ? new FileFingerprint(true, info.Length, info.LastWriteTimeUtc.Ticks)
                    : new FileFingerprint(false, 0, 0);
            }
            catch (IOException)
            {
                return new FileFingerprint(false, 0, 0);
            }
            catch (UnauthorizedAccessException)
            {
                return new FileFingerprint(false, 0, 0);
            }
        }
    }
}
