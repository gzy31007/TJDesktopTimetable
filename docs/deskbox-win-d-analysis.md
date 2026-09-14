# DeskBox「Win+D 后仍可见」逆向技术报告

> **来源声明**：本文是针对 MIT 项目 `TJDesktopTimetable` 独立整理的机制与事实摘要，只描述上游的行为、调用序列、参数语义与判定条件。
> 上游 DeskBox 的实现以 **GPL-3.0-only** 授权，**本文不包含其代码、注释或文档正文**（含译文），亦不构成对其表达的复制。
> 文中出现的 API / 类型 / 常量 / 消息名称与 `文件:行号` 仅为事实性定位信息；具体实现须由本项目独立编写。

- 对象：`/root/TJDesktopTimetable/.refs/DeskBox` @ `be2a9cf`（1.5.1 线）；**只读**，未改动、未做 git 操作。路径均相对仓库根。
- 一句话结论：**DeskBox 完全不监听 Win+D**。它靠一件事活下来——把格子窗口设成 Explorer 桌面图标视图 `SHELLDLL_DefView` 的 **owned window**（`GWLP_HWNDPARENT`，**非** `SetParent`）——外面再套一套「静息 / 唤起 / 租约」状态机管 z-order。
- 标记：**[E]** = 纯 Win32，Electron/koffi 可照抄；**[W]** = 依赖 WinUI 3 / WASDK。

---

## 1. 窗口宿主方式

**结论：是 (a) 顶层窗口 + `SetWindowLongPtr(GWLP_HWNDPARENT, <宿主>)` 设 owner。** 宿主 = **`SHELLDLL_DefView`**（不是 `Progman`，不是 `WorkerW`）。`SetParent` 只有声明、**零调用点**；**没有 0x052C**。

- `SetParent` 在 `Helpers/Win32Helper.cs:291` 只留了一条 P/Invoke 声明，全仓找不到调用点。
- 同文件 `:354` 给出常量 `GWLP_HWNDPARENT = -8`；`:372-373` 声明 `SetWindowLongPtrW`（目标 `user32.dll`，带 `SetLastError` 标志）。

宿主句柄取法见 `Services/WidgetLayerService.cs:978-1012` 与 `:1031-1034`，并配有一条「绝不主动催生 WorkerW」的注释规格（`:987-990`）：只用 Explorer 已经建好的 `SHELLDLL_DefView`；不要在这里强制创建 WorkerW——登录阶段这么做会和 Explorer 的图标布局恢复抢跑，可能把用户的桌面图标顺序搞乱。

伪代码序列（`:991-1001` → `:1031-1034`）：

1. `EnumWindows` 遍历所有顶层窗口；
2. 对每个窗口调用辅助函数 `FindDesktopIconViewChild`；
3. 该函数内部用 `FindWindowEx`，按类名 `SHELLDLL_DefView` 找子窗口；
4. 命中即取该子窗口作为宿主。

缓存自愈 `:980-983`：若缓存句柄非 0 且 `IsWindow` 仍为真，直接返回缓存；否则重新枚举。

挂载 + 读回校验 + 原 owner 存档 `:858-890`：

1. 取宿主句柄 `FindDesktopIconView()`；为 0 则记日志并返回失败（`:858-863`）；
2. 把窗口当前的 `GWLP_HWNDPARENT` 读出来存档，连同窗口句柄一起放进 `s_desktopLayerAttachments`（`:869-870`）；
3. 向 `GWLP_HWNDPARENT` 写入宿主句柄（`:876-879`）；
4. 立刻读回比对；若与写入值不符，记日志、调用还原函数、清空宿主缓存、返回失败（`:886-890`）。

还原 `:952-976`：把存档的原 owner 写回，再用 `SetWindowPos(HWND_NOTOPMOST)` 收尾。

**回退链**（三段，`:823-830` → `:71-90` → `:933-950`）：

- `:823-830` 先判定「是否该挂桌面」= 处于 `DesktopPinned` 模式 **或** 用户开启了「Win+D 后保持可见」偏好（默认 true）；判定本身委托给 `RelativeLayerRestorePolicy.ShouldAttachToDesktop(...)`。
- `:79-90` 执行回落：若应挂桌面且 `TryAttachToDesktopIconLayer` 成功就直接返回；否则先摘掉桌面层归属、清掉置顶属性，再用 `HWND_BOTTOM` 压到底。
- 即「静态贴桌面」与「信 Win+D」共用**同一个开关**：`Services/RelativeLayerRestorePolicy.cs:12-17` 的返回值就是「pinned 模式 OR 该偏好」；默认值在 `Models/AppSettings.cs:346` 为 true，`Services/SettingsService.cs:466` 读取。

启动期 deferral（`:12-15`、`:22-32`、`:39-69`；调用序 `App.xaml.cs:1090-1122`）：`WaitForDesktopIconViewReadyAsync` 要求宿主**连续 5 次采样一致**（250ms × 最多 48 次），且**故意不 await**（契约见 `DesktopShellStartupSafetyTests.cs:67-75`）。

---

## 2. Win+D 存活的确切原因

**结论：不靠「被隐藏后拉回来」，靠「压根不被隐藏」+ 回落时重申 owner。**
全仓 **0 命中**：`WM_WINDOWPOSCHANGING` / `WM_WINDOWPOSCHANGED` / `WM_SYSCOMMAND` / `SC_MINIMIZE` / `GetLastActivePopup` / `SetWinEventHook` / `EVENT_SYSTEM_FOREGROUND` / `RegisterShellHookWindow`。即**没有任何 Win+D 专用钩子、子类化或恢复定时器**。

机制写在三处注释里（`Services/WidgetLayerService.cs:77-78`、同文件 `:937-938`、`Helpers/Win32Helper.cs:1024-1025`），其要点分别是：pinned 模式永远栖身在 Explorer 内部；动态模式只在用户希望挂件躲过 Win+D 时才沿用同一个 owner；挂到桌面图标层可以避免被 Win+D 隐藏，同时保留「可被交互抬起」的动态层级行为；`SetWindowToDesktopLevel`（现已成死代码）那处的注释表达的是同一个意图。

三条实际起作用的路径：① **owner 关系本身**（owned window 恒在 owner 之上，且不属于独立顶层窗口的最小化集合）——唯一根本原因；② **静息/回落反复重申 owner**（`:71-90`、`:123-128`、`:200-224`、`:507-522`）；③ **临时提升时主动摘 owner**，故「浮起」期间不受保护，回落再挂回（`HoldTemporaryTopMost`，`:254-274`）。

唤起不靠持久置顶，而是两步脉冲（`Win32Helper.cs:1057-1071`）：先以 `HWND_TOPMOST` 调一次 `SetWindowPos`，紧接着以 `HWND_NOTOPMOST` 再调一次；效果是窗口停在「普通层级带的顶部」，但不留下 TopMost 属性。

**定时器全表**（均 `DispatcherQueueTimer`，**[W]**）：唤起态回落监视器 200ms（QuickReveal 50ms）`WidgetManager.ZOrder.cs:694-702`；鼠标边沿采样 50ms `:728-734`；TopMost 逻辑安全网 2s `WidgetWindowBase.Interaction.cs:111-123`；空闲 peer 排序延迟 120ms `ZOrder.cs:137-160`；临时提升后延迟回落 2300ms `:393-396`；唤起后抑制窗 +160ms 一次性 `WidgetManager.TrayAnimation.cs:135`。

---

## 3. 「静息层 / 临时提升 / 租约」状态机

**三层概念，别混。**

1. **逻辑会话态**：`Services/WidgetSessionManager.cs:3-9` 定义枚举 `WidgetSessionState`，取值为 `DesktopResting`、`RaisedSession`、`InteractionActive`、`Hidden`；窗口侧有一个受保护的布尔字段 `IsAtDesktopLayer`（`Views/WidgetWindowBase.cs:100`）。
2. **静息层（物理落点）**：`Services/RelativeLayerRestorePolicy.cs:19-36` 三选一——无前台窗口、或前台是桌面壳 → `DesktopBottom`；前台是自己或 DeskBox 其它窗口 → `PreservePeerOrder`；前台是外部应用 → `BehindForeground`（插到该应用之后，**不是** `HWND_BOTTOM`）。
3. **两种租约**，都以 `Generation` 让过期回调失效：
   - `Services/WidgetTemporaryRaiseLeasePolicy.cs:3-11` —— 临时提升租约（一批多窗口）：内容是一组窗口句柄加一个 `Generation`；判活要求句柄列表非空且 `Generation > 0`；`:65-70` 的 `OwnsGeneration` 用「活跃 且 代际相等」判定归属。
   - `Services/WidgetExpandedLayerLeasePolicy.cs:3-8` —— 展开租约（同一时刻只允许一个 widget 占据桌面带顶）：内容是单个窗口句柄加一个 `Generation`；判活要求句柄非零且 `Generation > 0`。

触发与恢复：获取 `ZOrder.cs:162-174` / `:56-73`、`WidgetWindowBase.cs:69-79`、`Collapse.cs:4086-4090`；恢复（临时提升）`ZOrder.cs:206-280`，四道闸 `:218-221`（`_widgetsRaisedFromTray` / `_isTogglingWidgetsDesktopLayer` / `IsInteractionActive` / 展开租约仍在）→ 逐窗口 `ForceRestoreDesktopLayerFromManager()` → `QueueIdleWidgetZOrderNormalization`；恢复（整组，F7 主路径）`WidgetManager.cs:1946-1986`；静息 peer 排序 `ZOrder.cs:297-326` + `Services/IdleWidgetZOrderPolicy.cs:17-29`，**只重排 widget 之间，绝不置底**——`:317-319` 的注释交代了原因：当前最高的 widget 构成整组的全局边界，所以「整组恢复到某个被激活应用之后」不会被后续步骤压到 `HWND_BOTTOM`。

⚠️ **`WidgetSurfacePromotionTransaction` 与 z-order 无关**：`Services/WidgetSurfacePromotionTransaction.cs:3-7` 是「旧成员窗口 → 统一 Surface 宿主」的内容迁移事务（prepare → present → commit/rollback）。别当成层级提升。

**[E]** 策略全是纯 C# 静态类、零 Win32 依赖，可直译成 TS 纯函数。**[W]** 只有 `DispatcherQueueTimer`（换 `setInterval`）。

---

## 4. 鼠标交互与 owner

**结论：交互期临时摘 owner，结束后挂回；有 `MA_NOACTIVATE` 但仅 DesktopPinned 模式；全仓没有 `GetLastActivePopup` / 前台指针修复。**

- 抬升交互 `Views/WidgetWindowBase.Interaction.cs:48-67`：动态模式下先把 `IsAtDesktopLayer` 置 false，再调 `WidgetLayerService.HoldTemporaryTopMost(HWnd, showWindow)`；`:254-274` 内部先摘桌面层归属，再走 TOPMOST 脉冲。
- 回落 `:191-235`：`ClearTopMostOnly()` → `ClearTopMostPreservingForeground`（`WidgetLayerService.cs:92-144`）按 disposition 重挂 owner → `QueueIdleWidgetZOrderNormalization`。
- **DesktopPinned 的 `WM_MOUSEACTIVATE` 子类**（`WidgetWindowBase.Bounds.cs:332-361`）：
  1. 收到 `WM_MOUSEACTIVATE`，且 `WidgetLayerService.ShouldSuppressPointerActivation` 判真 → 先调 `RestoreDesktopPinnedBottomState("native-pointer-suppressed")` 顺手修 owner/z-order（`:347`），再返回 `MA_NOACTIVATE`（`:348`）；
  2. 收到 `WM_MOUSEACTIVATE`，且 `ShouldPreserveQuickRevealActivatingClick()` 判真 → 返回 `MA_ACTIVATE`（`:357`，只用于 QuickReveal）。
  抑制条件 `WidgetLayerService.cs:1097-1109`：处于 pinned 模式 且 存在前台窗口 且 前台不是桌面壳 且 前台不是 widget。
- `WS_EX_NOACTIVATE` **只在 DesktopPinned 静息态施加**（`:779-782` 按「是否 pinned 模式」决定是否给窗口加该样式）；键盘输入命中 `TextBox` / `RichEditBox` / `PasswordBox` 等控件时临时摘掉该样式，再 `base.Activate()` + `SetForegroundWindow`，随后**立刻回底**（`Bounds.cs:176-275`、`WidgetLayerService.cs:767-770`）；路由守卫**不吞事件**（契约禁止把 `args.Handled` 置 true）。
- **未找到证据**：`GetLastActivePopup` / 回填 shell last active popup / `SetForegroundWindow(Progman)`——0 命中。DeskBox 也**不在鼠标按下瞬间**摘 owner（摘除发生在抬升/交互开始）。

**[E]** 全为纯 Win32（`SetWindowSubclass` + `WM_MOUSEACTIVATE` + `MA_NOACTIVATE`）。

---

## 5. 窗口样式与材质

**结论：格子默认真的用 Mica（控制器路径）；未发现任何「材质与 Win+D 冲突」记录。**

样式 `Views/WidgetWindowBase.Bounds.cs:35-60`：调 `presenter.SetBorderAndTitleBar(false, false)` 关闭边框与标题栏、置 `IsResizable = false`（`:35-41`）；给扩展样式加上 `WS_EX_TOOLWINDOW`（`:43-45`）；清掉 `WS_CAPTION` / `WS_BORDER` / `WS_DLGFRAME` / `WS_THICKFRAME`（`:52`）；随后带 `SWP_FRAMECHANGED` 生效（`:54-58`）；`AppWindow.IsShownInSwitchers = false`（`:60`）。
→ `WS_EX_TOOLWINDOW` ✅ 恒设；`WS_EX_NOACTIVATE` ⚠️ 仅 pinned（§4）；`WS_EX_TOPMOST` ❌ 只脉冲不留存；`WS_EX_LAYERED` ❌ 格子未用（仅 `Views/WidgetDetachPlacementPreviewWindow.cs:110-114` 的预览层用）；`WS_THICKFRAME` ❌ 被清。圆角/边框走 DWM：`DWMWA_WINDOW_CORNER_PREFERENCE(33)`（`Win32Helper.cs:1253`、`Bounds.cs:638`）、`DWMWA_SYSTEMBACKDROP_TYPE(38)`（`:1254`）。

材质 `Views/WidgetWindowBase.Backdrop.cs:31-110`，决策顺序：

1. 先请 `WindowsCompatibilityService.ResolveWidgetMaterialType` 解析出实际可用的材质类型（`:41-42`）；
2. 若属于 Mica 系，调 `ApplyMicaController` 尝试接管（`:86-92`）；
3. 若上一步没成功且属于 Acrylic 系，再调 `ApplyAcrylicController`（`:94-101`）；
4. 控制器一旦接管成功，就把 `DWMWA_SYSTEMBACKDROP_TYPE` 写成 `DWMSBT_NONE`（`:108`），避免 DWM 再叠一层。

- Mica 控制器：`MicaController`，`Kind` 在 `MicaKind.Base` 与 `MicaKind.BaseAlt`（即 Mica Alt）之间二选一（`:483-486`）。
- Acrylic 控制器：`DesktopAcrylicController`，`Kind` 在 `DesktopAcrylicKind.Base` 与 `DesktopAcrylicKind.Thin` 之间二选一（`:583-586`）。
- Solid：走 `WinUIEx.TransparentTintBackdrop`（`:307`）。
- 默认 `Mica`（`Models/AppSettings.cs:275`、`Services/SettingsService.cs:454`），浓度 0.65；Win10 上 Mica / MicaAlt 降级为 Acrylic（`Services/WindowsCompatibilityService.cs:125-131`）。
- 设置/管理窗口走**另一条路径**（`SettingsWindow.xaml.cs:174` → `ApplySafeBackdrop`），固定按 `MicaBackdrop{BaseAlt}` → `DesktopAcrylicBackdrop` → 空 的顺序降级。

**材质 × Win+D 冲突：未找到证据。** `CHANGELOG.md`（全量）、`docs/`（releases/architecture/baselines/support/articles）、`README*.md`、`src/` 注释中没有任何「Mica/Acrylic 导致 Win+D 后窗口消失或 DWM 合成失效」的记录；`ExcludeFromCapture` / `SetWindowDisplayAffinity` 全仓 0 命中。DeskBox 的 Win+D 记录**全部归因于 z-order/owner**（`CHANGELOG.md:469/539/570/652/1331/1448`）。最近的材质问题只是「亚克力偶尔渲染成平灰」（`:1821` 靠 backdrop refresh 重试）与 Win10 降级，**与显示桌面无关**。

> ⚠️ 对照本项目 AGENTS.md 硬结论（Electron `backgroundMaterial` 在 Win+D 后 DWM 合成失效且无法感知）：DeskBox 用 Mica 控制器 + owner 且宣称共存，但**仓库内没有反向验证记录**，不可当作「材质与 Win+D 无冲突」的证明。**[W] ❌** 材质控制器、`SystemBackdropConfiguration`、`ICompositionSupportsSystemBackdrop` 全是 WASDK 专有——**本项目最大的不可移植点**。

---

## 6. Explorer 重启 / 显示器变化 / 唤醒

**结论：不重建宿主关系，只「作废宿主缓存 + 走一遍普通回落」。三条通道都是子类化消息。**

1. **显示器/DPI/任务栏** `Services/WidgetDisplayChangeWatcher.cs:87-100`：收到 `WM_DISPLAYCHANGE`、`WM_DPICHANGED`、`TaskbarCreated`（`RegisterWindowMessage` 取回的 ID），或与显示相关的 `WM_SETTINGCHANGE` 时 → 作废宿主缓存 `InvalidateDesktopIconViewCache()`；若当前未被抑制，则排队回落（`:91` / `:98` → 280ms）。
   `WM_SETTINGCHANGE` 只认 `SPI_SETWORKAREA(0x2F)`，或参数字符串中含 Display / Monitor / WorkArea / Taskbar 等关键词（`:109-132`）；拖拽/缩放期间调 `SuppressRestore()` 抑制回落（`:44-47`）。
2. **Explorer 重启 / 唤醒 / 会话** `Services/AppLifecycleRecoveryWatcher.cs` + `AppLifecycleRecoverySignalClassifier.cs:22-50`：分类器把窗口消息映射成信号名——`WM_POWERBROADCAST` 且事件值为 `PBT_APMRESUMEAUTOMATIC` / `PBT_APMRESUMESUSPEND` / `PBT_APMRESUMECRITICAL` → `"resume"`；`WM_WTSSESSION_CHANGE` 且事件值为解锁 / 登录 / 远程连接 → `"session-unlock"` 或 `"session-reconnect"`；`WM_DISPLAYCHANGE` / `WM_DPICHANGED` → `"display-message"`；`TaskbarCreated` → `"explorer-restart"`，其余返回空（`:47-49`）。
   `TaskbarCreated` 是 **Explorer 重启判定信号**；`App.xaml.cs:1222-1252` 收到后依次：作废宿主缓存 → `RequestDisplayTopologyRestore` → 重注册热键 → `ScheduleExternalStateRecovery()`；另有 420ms 去抖（`AppLifecycleRecoveryWatcher.cs:25`）。
3. **宿主句柄失效自愈**：不显式重建，靠 `FindDesktopIconView` 里的 `IsWindow` 检查 + 下次任意层级操作时重新枚举（`:980-1007`）。`Views/WidgetWindowBase.Collapse.cs:2224-2228` 的注释点明：Explorer 在显示桌面、显示器变化或自身重启之后，可能把 WorkerW / SHELLDLL_DefView 宿主整个换掉。

Rust 原生层**完全无关**：`native/` 下 `Progman` / `WorkerW` / `SHELLDLL_DefView` / `SetParent` / `SetWindowPos` / `EnumWindows` / 任何 hook 均 0 命中；`native/deskbox-native/Cargo.toml:10-25` 连 `Win32_UI_WindowsAndMessaging` 都未启用。Rust 只做 COM 工具层（快捷方式/音量/快速访问/回收站/Explorer 启动/缩略图菜单代理，`Helpers/ShortcutNativeBackend.cs:145,167-181`）。

**[E]** 通道 1/2 全纯 Win32（`SetWindowSubclass` + 消息 ID + `RegisterWindowMessage("TaskbarCreated")` + `WTSRegisterSessionNotification`）。

---

## 7. 测试即规格（不变量）

**[策略]** = 纯逻辑单测；**[契约]** = 读源码文本做包含/不包含断言，把「源码里不许出现某写法」当规格。

- **Win+D/层级**（`tests/DeskBox.Tests/WidgetZOrderRestoreContractTests.cs`，10 个全是契约）：idle 归一化只重排同类、**绝不置底** `:15-17`（断言 `MoveToDesktopBottom` 不出现在该方法文本中）；可见性守卫必须早于图层修复、**禁无参 `MoveToDesktopBottom(HWnd)`**（防把已隐藏窗口重新显示）`:226-241`；托盘抬起「贴底」与 capsule 展开「取带顶」两条入口严格分离 `:262-273`；pinned 点击必经 subclass + `WM_MOUSEACTIVATE` + `MA_NOACTIVATE` 回底，且禁 `HoldTemporaryTopMost` `:80-85`；raised 恢复必须整组重锚、禁走 idle 归一化 `:61`。
- **桌面宿主安全**（`DesktopShellStartupSafetyTests.cs`）：**禁止催生 WorkerW** `:39-44`（断言源码中不出现 `SpawnWorkerWMessage` 与 `0x052C`，并且必须含一条明确「只用 Explorer 已建好的 `SHELLDLL_DefView`」的说明）；宿主需连续 N 次采样一致、中途出现 `IntPtr.Zero` 则重置进度 `:14-18` / `:27-30`；启动先 defer、**不许 await 就绪**、恢复先于延迟完成 `:67-75`。
- **静息落点**（`RelativeLayerRestorePolicyTests.cs`）：pinned 无条件贴桌面 `:17-21`；外部前台 → `BehindForeground` `:33`；无前台/桌面壳 → `DesktopBottom` `:49`；自己/DeskBox → `PreservePeerOrder` `:65`。
- **租约**：过期延迟恢复**不能**释放更新的租约 `WidgetTemporaryRaiseLeasePolicyTests.cs:91-92`（断言过期恢复之后，交互开始时那份租约原样保留且仍为活跃）；同一时刻只一个展开租约、旧 generation 的归属判定必为 false `WidgetExpandedLayerLeasePolicyTests.cs:17-24` / `:43`；窗口关闭只 `Forget` 自己、不失效整批 `Temporary...:126-127`。
- **空闲排序**（`IdleWidgetZOrderPolicyTests.cs`）：下排恒在上排之上 `:21`；与会话枚举顺序无关 `:35-37`；句柄去重 `:68`。
- **指针激活**（`WidgetLayerPointerActivationPolicyTests.cs`）：仅「pinned 且前台是外来窗口」才抑制 `:20-26`；仅 QuickReveal + 托盘抬起才保留首次点击 `:40-43`。
- **Windows 兼容**（`Windows10WidgetMotionContractTests.cs` / `WindowsCompatibilityServiceTests.cs`）：Win10 用 `SWP_NOCOPYBITS|SWP_DEFERERASE` `:100-101`；禁 `DispatcherQueuePriority.High`、`TimeSpan.FromMilliseconds(15)`、`_windows10InteractiveResizeTimer` 等 legacy 写法 `:95-99` / `:215-222`；build 19045 圆角强制 Square、Mica→Acrylic、高对比度禁动画 `:22-26` / `:62-66` / `:80-85`。
- **桌面类链**（`ShellDesktopDropTargetTests.cs`）：白名单 `Progman` / `WorkerW` / `SHELLDLL_DefView`（大小写不敏感）+ `SysListView32 → SHELLDLL_DefView → WorkerW` 的祖先链 `:30-31` / `:60-61`；**显式排斥任务栏类**（即使祖先是 WorkerW）`:53-54`。
- **未找到证据**：没有任何测试断言真实 `GWLP_HWNDPARENT` 写入结果、真实 Win+D 后可见性、或 `GetLastActivePopup` 修复——只能在真机人工验收（`[重要勿删]widget_zorder_lifecycle.md:242-251` 有人工复现清单）。

---

## 8. Electron 可移植性汇总

| 做法 | 可移植 | 说明 |
|---|---|---|
| `SetWindowLongPtr(GWLP_HWNDPARENT, host)` 设 owner | **[E] ✅** | 纯 Win32，koffi 直调；本项目用 Progman，DeskBox 用 `SHELLDLL_DefView` |
| `EnumWindows` + `FindWindowExW` 按类名 `SHELLDLL_DefView` 取宿主 | **[E] ✅** | 纯 Win32；必须配 `IsWindow` 缓存自愈 |
| 存档/还原原 owner；挂载后读回校验、失败回退 `HWND_BOTTOM` | **[E] ✅** | 纯 Win32，建议全盘照抄（`:869-870`、`:882-890`、`:952-976`） |
| `SetWindowPos` TOPMOST→NOTOPMOST 两步脉冲 | **[E] ✅** | 纯 Win32 |
| `SetWindowSubclass` + `WM_MOUSEACTIVATE` / `MA_NOACTIVATE` | **[E] ✅** | 纯 Win32（comctl32） |
| `SetWindowSubclass` + `WM_DISPLAYCHANGE` / `TaskbarCreated` / `WM_SETTINGCHANGE` | **[E] ✅** | 纯 Win32；`RegisterWindowMessage("TaskbarCreated")` 同样可用 |
| `WTSRegisterSessionNotification` 接唤醒/解锁 | **[E] ✅** | 纯 Win32（wtsapi32） |
| **不做** `SetParent` 成 WorkerW 子窗口；**不发** 0x052C | **[E] ✅** | 「不做」的部分也照抄，这是 DeskBox 的显式契约 |
| 租约/代际/静息落点策略类 | **[E] ✅** | 纯逻辑，可直译 TS |
| `WS_EX_TOOLWINDOW`、清 `WS_THICKFRAME`、DWM 圆角 | **[E] ✅** | 纯 Win32 + `DwmSetWindowAttribute` |
| `DispatcherQueueTimer` 周期任务 | **[W] ⚠️** | 换 `setInterval`，语义等价 |
| `MicaController` / `DesktopAcrylicController` / `SystemBackdropConfiguration` | **[W] ❌** | WASDK 专有；Electron 侧对应物已实测 Win+D 后失效 |
| WinUI 3 `AppWindow` / `OverlappedPresenter` 无边框设置 | **[W] ❌** | 换 Electron `frame:false` / `setResizable` |

**最小照抄三条**：① 宿主必须是 Explorer 已建的 `SHELLDLL_DefView`（`EnumWindows` + `FindWindowEx`），**不要** `SetParent`、不要发 `0x052C`；② owner 写入前存档、写入后读回校验、失败一律回退 `HWND_BOTTOM`，宿主句柄做 `IsWindow` 缓存自愈；③ 抬升时摘 owner，回落按「外部前台 → 插到它之后 / 桌面壳 → 回底 / 自己 → 只重排同类」三选一重挂，peer 排序**永不置底**。

---

## 9. 与设计文档不一致之处（重要）

`docs/architecture/[重要勿删]widget_zorder_lifecycle.md` 是设计手册，但**部分内容已过期**，引用前务必对照代码：

1. 文档 `:189` / `:220` / `:238` / `:267` 反复提到的 `RunInteractionLeakWatchdog` / `ForceResetInteractions` **在当前代码中已不存在**（`src/` + `tests/` 全仓 0 命中；仅剩 `AppDiagnosticsService` 的 UI 线程看门狗）。即「交互深度泄漏致死锁」这道看门狗**目前没有实现**——但 `WidgetManager.ZOrder.cs:218-221` 仍把它当闸门逻辑依赖的深度计数在用。
2. 文档 `:24` 说 `DesktopPinned` 模式「格子 attach 到 WorkerW 桌面容器」——**错误**。代码 attach 的是 `SHELLDLL_DefView`，且明确不催生 WorkerW。
3. 文档 §6 坑 #6 把 `SetWindowToDesktopLevel` 当现行机制——该函数（`Win32Helper.cs:1027-1032`）**零调用点，是死代码**；真正在用的是 `MoveToDesktopBottom`。
4. 两模式边界已变：**动态模式默认也挂 owner**（开关 `KeepWidgetsVisibleOnShowDesktop`，默认 true），与 `DesktopPinned` 的差别在抬升/回落行为，而非「挂不挂」。

---

## 10. 结论速查

Q1 owner（`GWLP_HWNDPARENT` = `SHELLDLL_DefView`）、无 `SetParent` / 无 `0x052C` → Q2 无 Win+D 监听、靠 owner + 回落重申 → Q3 会话态/静息落点/代际租约三件套 → Q4 交互期摘 owner、`MA_NOACTIVATE` 仅 pinned、**无** `GetLastActivePopup` → Q5 `WS_EX_TOOLWINDOW` 恒设、默认 Mica、**无材质×Win+D 记录** → Q6 三条子类化通道 + `IsWindow` 自愈 → Q7 源码契约测试 + 真机人工验收。
