# WinUI 线：DeskBox 参考事实与构建环境清单

> 本文件是 AGENTS.md 里**参考事实**的逐字搬运（2026-09-16 结构拆分）：DeskBox（GPL-3.0-only 只读副本）用了哪些 API / 机制，以及本机构建环境清单。许可边界见 `docs/deskbox-refactor-assessment.md`。

- **窗口 chrome 与拖动：DeskBox 是怎么做的（只记事实，不抄代码）**：`.refs/DeskBox` 是只读参考副本，
  **GPL-3.0-only**，按 `docs/deskbox-refactor-assessment.md` 的结论不能抄代码/注释/文档/美术资源，
  但可以提取"用了哪些 API、什么机制"这类事实。它的做法（`Views/WidgetWindowBase.Bounds.cs` 等）：
  - 窗口：`OverlappedPresenter.SetBorderAndTitleBar(false, false)` + `IsResizable/IsMaximizable/IsMinimizable = false`，
    再把 `GWL_STYLE` 里的 `WS_CAPTION | WS_BORDER | WS_DLGFRAME | WS_THICKFRAME` 全部清掉，
    最后 `SetWindowPos(..., SWP_FRAMECHANGED)` 让样式生效；`AppWindow.IsShownInSwitchers = false`。
    **即"完全没有系统边框/标题栏/窗口按钮"**，右上角不会再有最小化/最大化/关闭抄我们的按钮。
  - 拖动：**不用原生 move loop**，而是自实现 —— 根元素 `PointerPressed` 起，
    `CapturePointer` + `GetCursorPos()` 算位移，每帧把窗口 `SetWindowPos` 到
    `初始位置 + 位移`（有 4px 的起拖阈值，避免误触）。交互开始还会把窗口**临时提到最前**
    （`ElevateForInteraction`），结束后再落点 —— 与我们 `SuspendForInteraction` 的思路一致。
  - 缩放：因为它把 `WS_THICKFRAME` 也清了，所以缩放同样自实现（自己有 resize 边框的命中处理 +
    `ResizeGuideOverlay` 引导层）。**我们现在也走自实现**（圆角和缩放二选一时选了圆角，
    见下面"缩放（2026-09-15 二次定稿）"）；只是没做引导层与吸附，简单些。
  - 托盘：用 **`H.NotifyIcon.WinUI`**（社区库）承载，菜单直接是 WinUI 的 `MenuFlyout`
    （不是原生 `TrackPopupMenu`）。我们没引那个包，走的是 `Shell_NotifyIcon` + 原生菜单。
  - 它额外做的：`WS_EX_TOOLWINDOW`、`IsShownInSwitchers=false`（我们已做前者）。
