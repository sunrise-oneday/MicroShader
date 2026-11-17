namespace MicroShader.CoordinationEngine.Session;

/// 空闲回收的动作档位。
public enum ReclaimAction
{
    /// 这一拍不该回收。
    None = 0,

    /// 浅度：只做一次轻量 GC 与实例池收缩，保留一个 DXC 实例给下一次编译。
    Shallow = 1,

    /// 深度：销毁全部 DXC 实例、压缩大对象堆并收缩工作集。
    Deep = 2,
}

/// 空闲内存回收的门槛判定：给定此刻的读数，回答「这一拍该做哪一档回收」。
/// 判定放在这里而不是宿主里，只有一个原因：它必须是纯函数，才能被无头自检直接覆盖。
/// 谁在什么时机调用它、回收动作本身做什么，仍然由组合根决定。
public static class IdleReclaimPolicy
{
    /// 浅度回收的门槛：连续静默这么久就值得做一次轻量 GC。
    public static readonly TimeSpan ShallowIdleThreshold = TimeSpan.FromSeconds(15);

    /// 深度回收的门槛：连续静默这么久，用户大概率已经切回 Unity 或去做别的事了。
    public static readonly TimeSpan DeepIdleThreshold = TimeSpan.FromMinutes(5);

    /// 两次浅度回收之间至少隔这么久，避免长时间挂机时反复空转。
    public static readonly TimeSpan ShallowGap = TimeSpan.FromSeconds(60);

    /// 两次深度回收之间至少隔这么久。
    public static readonly TimeSpan DeepGap = TimeSpan.FromMinutes(10);

    /// 低于这个私有提交量就不必浅度回收：已经很轻，再 GC 只是空转。
    public const long ShallowWatermarkBytes = 15L * 1024 * 1024;

    /// 低于这个私有提交量就不必深度回收。
    public const long DeepWatermarkBytes = 25L * 1024 * 1024;

    /// 判定这一拍该做哪一档回收。
    /// sinceLastActivity 是距上一次分析活动的时间，sinceLastShallowReclaim 与 sinceLastDeepReclaim
    /// 分别距上一次浅度与深度回收，isIdle 表示调度器此刻是否完全空闲，privateBytes 是进程私有提交量。
    public static ReclaimAction Decide(
        TimeSpan sinceLastActivity,
        TimeSpan sinceLastShallowReclaim,
        TimeSpan sinceLastDeepReclaim,
        bool isIdle,
        long privateBytes)
    {
        // 判闲：调度器里没有在飞与排队的一轮。
        if (!isIdle)
        {
            return ReclaimAction.None;
        }

        // 判静默：刚停手不久不动手，避免打断「看代码思考」的停顿。
        if (sinceLastActivity < ShallowIdleThreshold)
        {
            return ReclaimAction.None;
        }

        // 水位线：已经很轻就不再折腾，省掉空转的 GC。
        if (privateBytes < ShallowWatermarkBytes)
        {
            return ReclaimAction.None;
        }

        // 深度档：静默够久、内存够高、距上次深度够远。
        if (sinceLastActivity >= DeepIdleThreshold
            && privateBytes >= DeepWatermarkBytes
            && sinceLastDeepReclaim >= DeepGap)
        {
            return ReclaimAction.Deep;
        }

        // 浅度档：距上次浅度够远。
        if (sinceLastShallowReclaim >= ShallowGap)
        {
            return ReclaimAction.Shallow;
        }

        return ReclaimAction.None;
    }
}
