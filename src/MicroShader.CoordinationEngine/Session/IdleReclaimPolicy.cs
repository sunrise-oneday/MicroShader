namespace MicroShader.CoordinationEngine.Session;

/// <summary>
/// 空闲内存回收的门槛判定：给定此刻的各项读数，回答「这一拍该不该回收」。
/// </summary>
/// <remarks>
/// 判定放在这里而不是宿主里，只有一个原因：它必须是纯函数，才能被无头自检直接覆盖。
/// 改造前这段判定埋在宿主定时器的回调里，一条用例都碰不到 —— 而门槛（限次、限频、限时、
/// 判闲）恰恰是最容易写反的地方。
/// 谁在什么时机调用它、回收动作本身做什么，仍然由组合根决定。
/// </remarks>
public static class IdleReclaimPolicy
{
    /// <summary>连续多久没有分析活动才算「真的停手了」。</summary>
    public static readonly TimeSpan IdleThreshold = TimeSpan.FromSeconds(8);

    /// <summary>两次回收之间至少隔多久。</summary>
    public static readonly TimeSpan MinimumGap = TimeSpan.FromSeconds(30);

    /// <summary>只在启动后这段时间内回收，再往后用户已在稳定工作。</summary>
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(5);

    /// <summary>整个进程最多回收几次。</summary>
    public const int MaxReclaims = 3;

    /// <summary>判定这一拍是否该做一次空闲回收。</summary>
    /// <param name="sinceStart">距启动（回收窗口开启）过去了多久。</param>
    /// <param name="sinceLastActivity">距上一次分析活动（派发或发布）过去了多久。</param>
    /// <param name="sinceLastReclaim">距上一次回收过去了多久。</param>
    /// <param name="isIdle">调度器此刻是否完全空闲。</param>
    /// <param name="reclaimsSoFar">本进程已经回收过几次。</param>
    /// <returns>true 表示可以动手回收。</returns>
    public static bool ShouldReclaim(
        TimeSpan sinceStart,
        TimeSpan sinceLastActivity,
        TimeSpan sinceLastReclaim,
        bool isIdle,
        int reclaimsSoFar)
    {
        // 限次：先用最便宜的一条挡掉绝大多数节拍。
        if (reclaimsSoFar >= MaxReclaims) return false;

        // 限时：窗口关闭后不再动手，否则就成了长期存在的周期性停顿。
        if (sinceStart > Window) return false;

        // 限频：刚回收完紧接着再来一次没有新收益，只会连着卡两下。
        if (sinceLastReclaim < MinimumGap) return false;

        // 门槛一：必须真的空闲（调度器里没有在飞与排队的一轮）。
        if (!isIdle) return false;

        // 门槛二：必须连续静默够久，光「此刻没在跑」还不够。
        return sinceLastActivity >= IdleThreshold;
    }
}
