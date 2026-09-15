namespace Tjt.Widget;

/// <summary>静息落点（TS 侧 <c>RestingDisposition</c>）。</summary>
public enum RestingDisposition
{
    /// <summary>回桌面层：挂 owner + 置底。</summary>
    DesktopBottom,

    /// <summary>只维护挂件内部顺序，不动全局层级。</summary>
    PreservePeerOrder,

    /// <summary>插到当前前台窗口之后（z-order 更低）。</summary>
    BehindForeground,
}

/// <summary>落点决策的输入（全部由 Win32 查询得来，见 <c>Layer</c>）。</summary>
/// <param name="HasForeground">存在有效的前台窗口。</param>
/// <param name="ForegroundIsDesktopShell">前台窗口属于桌面壳（Progman / WorkerW / SHELLDLL_DefView）。</param>
/// <param name="ForegroundIsSelf">前台窗口就是本挂件自己。</param>
/// <param name="ForegroundIsOwnApp">前台窗口是本应用的其它窗口（例如将来的设置窗口）。</param>
public sealed record RestingInputs(
    bool HasForeground,
    bool ForegroundIsDesktopShell,
    bool ForegroundIsSelf,
    bool ForegroundIsOwnApp);

/// <summary>
/// 静息落点策略 —— **纯函数，零依赖，可单测**（TS 侧 <c>resting-policy.ts</c> 的移植）。
///
/// <para>为什么单独成类：Win32 那一层（owner / z-order / 样式）在 WSL 上跑不起来，
/// 而"该落到哪一层"恰恰是最需要被测试锁定的判断 —— 它决定挂件会不会被压到桌面之下、
/// 交互结束后会不会从"紧贴当前前台窗口"掉到所有窗口之后。</para>
///
/// <list type="bullet">
/// <item><description>没有前台、或前台就是桌面 → 回桌面层（挂 owner + 置底）；</description></item>
/// <item><description>前台是自己或本应用其它窗口 → 只维护内部顺序，**不动全局层级**
/// （否则用户刚把挂件拖到某个位置，交互一结束就被砸到底部）；</description></item>
/// <item><description>前台是第三方应用 → 插到该应用之后，保持"紧贴当前页面的下沿"。</description></item>
/// </list>
/// </summary>
public static class RestingPolicy
{
    /// <summary>给定前台情况，决定静息落点。</summary>
    /// <param name="inputs">前台判定结果。</param>
    public static RestingDisposition Decide(RestingInputs inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        if (!inputs.HasForeground || inputs.ForegroundIsDesktopShell) return RestingDisposition.DesktopBottom;
        if (inputs.ForegroundIsSelf || inputs.ForegroundIsOwnApp) return RestingDisposition.PreservePeerOrder;
        return RestingDisposition.BehindForeground;
    }
}
