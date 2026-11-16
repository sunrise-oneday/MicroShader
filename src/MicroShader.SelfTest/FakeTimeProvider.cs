using System;
using System.Collections.Generic;

namespace MicroShader.SelfTest;

/// <summary>
/// 可手动推进的时钟，供「时间敏感」用例使用。
/// </summary>
/// <remarks>
/// 只为测试而存在：生产代码注入的是 <see cref="TimeProvider.System"/>。
/// 支持一次性与周期性定时器，<see cref="Advance"/> 会把期间到期的定时器依次触发。
/// 时间戳频率取 <see cref="TimeSpan.TicksPerSecond"/>，与 <see cref="DateTimeOffset.UtcTicks"/> 同源，
/// 这样被测代码里「用 GetTimestamp 算间隔」的写法不需要额外换算。
/// </remarks>
internal sealed class FakeTimeProvider : TimeProvider
{
    private readonly Lock _gate = new();
    private readonly List<FakeTimer> _timers = [];
    private DateTimeOffset _now;
    private long _timestamp;

    public FakeTimeProvider(DateTimeOffset? start = null)
    {
        _now = start ?? new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        _timestamp = _now.UtcTicks;
    }

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
        {
            return _now;
        }
    }

    public override long GetTimestamp()
    {
        lock (_gate)
        {
            return _timestamp;
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        ArgumentNullException.ThrowIfNull(callback);

        var timer = new FakeTimer(this, callback, state);
        lock (_gate)
        {
            _timers.Add(timer);
            timer.Schedule(dueTime, period, _now);
        }

        return timer;
    }

    /// <summary>把时钟往前推进一段，并触发期间到期的定时器。</summary>
    public void Advance(TimeSpan delta)
    {
        if (delta < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(delta), "只能向前推进");
        }

        // 回调一律在锁外执行：被测代码的回调会去拿它自己的锁（调度器、watcher 都有），
        // 如果这里持着时钟锁回调，就会与「持业务锁 → 调 CreateTimer 拿时钟锁」形成反向锁序。
        // 现在所有用例都是单线程顺序推进，撞不上；但这是假时钟必须守住的契约。
        var due = new List<FakeTimer>();

        lock (_gate)
        {
            var target = _now + delta;

            // 每次挑最近的一个到期定时器，直到没有落在 [now, target] 里的。
            // 上限只是防呆：周期设成 0 之类的写法会让这里无限循环。
            for (var guard = 0; guard < 100_000; guard++)
            {
                FakeTimer? next = null;
                foreach (var timer in _timers)
                {
                    if (timer.DueAt is not { } candidate || candidate > target)
                    {
                        continue;
                    }

                    if (next is null || candidate < next.DueAt)
                    {
                        next = timer;
                    }
                }

                if (next is null)
                {
                    break;
                }

                _now = next.DueAt!.Value;
                _timestamp = _now.UtcTicks;
                next.PrepareFire();
                due.Add(next);
            }

            _now = target;
            _timestamp = _now.UtcTicks;
        }

        foreach (var timer in due)
        {
            timer.Invoke();
        }
    }

    /// <summary>最近一个到期时刻；没有已挂上的计时器时为 null。</summary>
    /// <remarks>
    /// 仅供用例把「异步通知已到达」变成可观察事实：通知本身与时钟无关，
    /// 唯一副作用是把去抖计时器挂上，于是这里就能看到它的到期时刻。
    /// </remarks>
    public DateTimeOffset? NextDueAt
    {
        get
        {
            lock (_gate)
            {
                DateTimeOffset? earliest = null;
                foreach (var timer in _timers)
                {
                    if (timer.DueAt is not { } due || (earliest is { } current && due >= current))
                    {
                        continue;
                    }

                    earliest = due;
                }

                return earliest;
            }
        }
    }

    private void Add(FakeTimer timer)
    {
        // 变更可能来自别的线程（例如文件系统通知线程），而 Advance 正在遍历这份名单，必须串行化。
        lock (_gate)
        {
            if (!_timers.Contains(timer))
            {
                _timers.Add(timer);
            }
        }
    }

    private void Remove(FakeTimer timer)
    {
        lock (_gate)
        {
            _timers.Remove(timer);
        }
    }

    private sealed class FakeTimer(FakeTimeProvider owner, TimerCallback callback, object? state) : ITimer
    {
        private TimeSpan? _period;

        /// <summary>下一次到期时刻；null 表示已停用。</summary>
        public DateTimeOffset? DueAt { get; private set; }

        public void Schedule(TimeSpan dueTime, TimeSpan period, DateTimeOffset now)
        {
            _period = period > TimeSpan.Zero && period != Timeout.InfiniteTimeSpan ? period : null;
            DueAt = dueTime == Timeout.InfiniteTimeSpan ? null : now + dueTime;
            if (DueAt is null)
            {
                owner.Remove(this);
            }
            else
            {
                // 创建时就是停用的计时器（很常见：先建好，首事件到来才 Change）已经不在名单里，
                // 重新挂到期时间时必须放回去，否则推进时钟永远不会触发它。
                owner.Add(this);
            }
        }

        /// <summary>推进到下一拍到期的状态（不加锁回调）。</summary>
        public void PrepareFire()
        {
            if (DueAt is not { } due)
            {
                return;
            }

            DueAt = _period is { } period ? due + period : null;
            if (DueAt is null)
            {
                owner.Remove(this);
            }
        }

        public void Invoke() => callback(state);

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            Schedule(dueTime, period, owner.GetUtcNow());
            return true;
        }

        public void Dispose()
        {
            DueAt = null;
            owner.Remove(this);
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
