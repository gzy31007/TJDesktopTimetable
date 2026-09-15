using Tjt.Widget;
using Xunit;

namespace Tjt.Core.Tests;

/// <summary>
/// 指针归属策略的单测（驱动 <c>Tjt.App/Win32/PointerTarget.cs</c> 的起手校验）。
///
/// <para>这是"被遮挡时不要响应拖动/缩放"里唯一能在 Linux 上验证的部分：
/// 真机上出错的表现是"别的窗口上拖一下就带着挂件一起缩放"，而这类误触发很难在真机上复现定位。
/// 判据本身只有三条（自己 / 桌面壳 / owner 链），但**桌面壳那条不能少** ——
/// 漏掉它，贴桌面层的挂件在"首次点击之前"会被 <c>WindowFromPoint</c> 报成 Explorer 桌面宿主，
/// 表现为"边缘彻底拖不动"。</para>
/// </summary>
public class PointerOwnershipTests
{
    private static readonly nint Self = 0x1000;
    private static readonly nint OtherApp = 0x2000;
    private static readonly nint DesktopShell = 0x3000;
    private static readonly nint OwnerRoot = 0x4000;

    /// <summary>正常按下：光标下的根窗口就是挂件自己。</summary>
    [Fact]
    public void 光标在自己窗口上时接受()
    {
        Assert.True(PointerOwnership.Accepts(Self, Self, nint.Zero, false));
        Assert.True(PointerOwnership.Accepts(Self, Self, OwnerRoot, false));
    }

    /// <summary>被别的窗口遮挡：根窗口是别的应用 —— 必须拒绝（本 bug 的正例）。</summary>
    [Fact]
    public void 光标在别的窗口上时拒绝()
    {
        Assert.False(PointerOwnership.Accepts(Self, OtherApp, nint.Zero, false));
        Assert.False(PointerOwnership.Accepts(Self, OtherApp, OwnerRoot, false));
    }

    /// <summary>桌面壳放行：贴桌面层的 no-activate 窗口在首次点击前会被报成 Explorer 桌面宿主。</summary>
    [Fact]
    public void 桌面壳放行()
    {
        Assert.True(PointerOwnership.Accepts(Self, DesktopShell, nint.Zero, true));
        Assert.True(PointerOwnership.Accepts(Self, DesktopShell, OwnerRoot, true));
    }

    /// <summary>「是桌面壳」这个标志不能给空句柄背书。</summary>
    [Fact]
    public void 桌面壳标志不适用于空句柄()
    {
        Assert.False(PointerOwnership.Accepts(Self, nint.Zero, OwnerRoot, true));
    }

    /// <summary>owner 链上的根窗口也算自己（owned 窗口会以 owner 的身份被报出来）。</summary>
    [Fact]
    public void owner链放行()
    {
        Assert.True(PointerOwnership.Accepts(Self, OwnerRoot, OwnerRoot, false));
        Assert.False(PointerOwnership.Accepts(Self, OtherApp, nint.Zero, false));
    }

    /// <summary>没有窗口句柄时一律拒绝（拿不到光标 / 窗口已销毁）。</summary>
    [Fact]
    public void 句柄缺失时拒绝()
    {
        Assert.False(PointerOwnership.Accepts(nint.Zero, Self, OwnerRoot, false));
        Assert.False(PointerOwnership.Accepts(nint.Zero, nint.Zero, nint.Zero, false));
        Assert.False(PointerOwnership.Accepts(Self, nint.Zero, nint.Zero, false));
    }
}
