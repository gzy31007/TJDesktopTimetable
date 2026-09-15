using Tjt.Widget;
using Xunit;

namespace Tjt.Core.Tests;

/// <summary>
/// 静息落点策略的单测（对齐 TS 侧 <c>apps/desktop/src/main/win32/resting-policy.ts</c>
/// 及其 Electron 侧真机结论）。
///
/// 这是"贴桌面常驻"里唯一能在 Linux 上验证的部分，也恰恰是历史上出错最多的判断：
/// 挂件被压到桌面之下、或者一交互完就掉到所有窗口之后，根因都是这里的落点选错。
/// </summary>
public class RestingPolicyTests
{
    private static RestingDisposition Decide(
        bool hasForeground = true,
        bool desktopShell = false,
        bool self = false,
        bool ownApp = false) =>
        RestingPolicy.Decide(new RestingInputs(hasForeground, desktopShell, self, ownApp));

    [Fact]
    public void 没有前台窗口时回桌面层()
    {
        Assert.Equal(RestingDisposition.DesktopBottom, Decide(hasForeground: false));
    }

    [Fact]
    public void 前台是桌面壳时回桌面层()
    {
        // Win+D 之后前台就是桌面：挂件必须回到"桌面之上、其它窗口之下"
        Assert.Equal(RestingDisposition.DesktopBottom, Decide(desktopShell: true));
        // 桌面壳判定优先于其它标记（前台就是桌面时不可能同时是自己）
        Assert.Equal(RestingDisposition.DesktopBottom, Decide(desktopShell: true, ownApp: true));
    }

    [Fact]
    public void 前台是自己时不动全局层级()
    {
        // 用户刚点过挂件：这时若把它压到底，观感就是"点一下跳走了"
        Assert.Equal(RestingDisposition.PreservePeerOrder, Decide(self: true));
    }

    [Fact]
    public void 前台是本应用其它窗口时也不动全局层级()
    {
        // 将来会有设置窗口；本应用内部切换不应该影响挂件的全局层次
        Assert.Equal(RestingDisposition.PreservePeerOrder, Decide(ownApp: true));
    }

    [Fact]
    public void 前台是第三方应用时插到它之后()
    {
        Assert.Equal(RestingDisposition.BehindForeground, Decide());
    }

    [Fact]
    public void 桌面壳优先于本应用判定()
    {
        // 边界：Explorer 的 DefView 与本应用同进程是不可能的，但判定顺序必须稳定
        Assert.Equal(RestingDisposition.DesktopBottom, Decide(desktopShell: true, self: true));
    }

    [Fact]
    public void 尺寸非法时不可用()
    {
        Assert.True(new WindowBounds(100, 100, 1080, 700).IsUsable);
        Assert.False(new WindowBounds(0, 0, 0, 0).IsUsable);
        Assert.False(new WindowBounds(0, 0, 319, 700).IsUsable);
        Assert.False(new WindowBounds(0, 0, 1080, 239).IsUsable);
    }
}
