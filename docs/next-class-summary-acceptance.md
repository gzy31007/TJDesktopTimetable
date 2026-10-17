# Windows 下一次上课摘要：实施与验收记录

工单：[下一次上课摘要 #12](https://github.com/gzy31007/TJDesktopTimetable/issues/12)，父图 [#5](https://github.com/gzy31007/TJDesktopTimetable/issues/5)，路线 [#16](https://github.com/gzy31007/TJDesktopTimetable/issues/16)。

用户已明确授权从规划进入本票实现及完整验收。不启动后续 #13，不发版。2026-10-17 已完成真实 Windows 睡眠恢复，全部规格验收证据见下文，不以代码完成或编译通过替代真机放行。

## 实现范围

- `NextClasses.Find` 从原始课表枚举已知整学期，按完整绝对时刻严格选择开始时间大于现在的最早完整同刻组。不按课程名或 ID 去重；结束时间不参与选择。未知安排若仍可能更早或同刻，返回信息不足。
- 既有 `Layout.BuildBoard → BoardVisualBuilder.Build` 提供最终摘要模型，网格筛选不影响选组。保留纯调用 Today/Now 独立语义，Windows 外壳合成匹配的日历日、完整有效时刻和整数分钟。
- Windows 在原顶栏与星期表头之间固定增加单行，置于网格滚动区外。实际 WinUI 字体测量驱动辅助文字、课程名、教室的退让策略。Linux 外壳不增加摘要行。
- 全摘要悬停使用可移入、可滚动的 Popup；普通 ToolTip 不适合承载交互滚动。未变化摘要保留 owner，避免周期刷新关闭正在阅读的完整组；日期、组或主题变化仍更新内容。
- 正常推进、开课边界、午夜、重新显示、系统时间变化与恢复消息均重新核对有效时刻；隐藏暂停定时器，退出解绑。诊断 fixture+size 启动不修改开机自启、不执行真实更新检查，显式尺寸不持久化窗口位置。

## 自动验收

2026-10-16 首次全量通过，2026-10-17 恢复通知补丁后再次通过。WSL SDK 10.0.401：

```sh
DOTNET_ROOT="$PWD/.tools/dotnet" DOTNET_CLI_HOME="$PWD/.tools/dotnet-home" \
  .tools/dotnet/dotnet test dotnet/TjtTimetable.slnx --no-restore --verbosity quiet
```

当前结果：389 passed，0 failed，0 skipped。`git diff --check` 通过。

主组合矩阵位于 `dotnet/TjtCore.Tests/BoardVisualNextClassTests.cs`；公开核心接口边界位于 `NextClassesTests.cs`。覆盖严格开始前/相等/开始后、跨日跨周与超过十四天、开学前/学期后、相同课程不同教室不去重、不同节次同刻、空掩码、未知开始与同刻完整性、缺结束/缺教室、安全的未知信息、四种周次视图/隐藏周末、Today/Now 独立、时区日界、午夜今后转换、四种诊断时刻合成、非倒计时文案、长中文/西文与窄化优先级、旧顶栏/网格高度兼容。

这是模型与既有测试门禁的通过记录，不代表 WinUI 实际字体或交互已经全部通过。

## 独立审查及修正

- Core/Widget 规格复核未发现阻断。
- Windows 审查发现普通 ToolTip 内 ScrollViewer 后半组不可交互，改为悬停 Popup。
- 后续审查发现周期 Render 会卸载未变化摘要并中断 Popup，改为结构化比较并复用 owner；比较不能仅依赖有分隔符碰撞可能的 Tooltip 字符串。
- 无效诊断日期原先仅形状校验，可能使新严格时间合成抛异常；CLI 已改为真实日历校验。
- 真机发现已打开浮层后重载课表时，清空已绑定的 `Popup.XamlRoot` 会抛出 `0x8000FFFF`。移除该赋 null，保留关闭与事件/定时器解绑；原反例已在最终版重跑，无 fatal，重载、隐藏与重显可完成。
- 2026-10-17 人工睡眠复测补出 Modern Standby 缺口：原实现只监听 `WM_POWERBROADCAST`，未调用系统通知注册。已补 `RegisterSuspendResumeNotification(hwnd, DEVICE_NOTIFY_WINDOW_HANDLE)`，持有返回的注册句柄，在 Teardown 中先注销再移除托管订阅。按[微软 DAM 文档](https://learn.microsoft.com/en-us/windows/win32/w8cookbook/desktop-activity-moderator#solution)及[注册 API](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-registersuspendresumenotification)独立实现。
- 原探针只匹配 `reason=power-resume`，但外壳合并排队刷新时只保留首个 reason，缺少该标签不能证明原生消息没收到。已独立记录原始恢复消息，探针改为核对 OS 睡眠/恢复配对、原始消息之后的新采样，不改刷新合并行为。
- 修正已进行独立静态复核；最终构建及动态回归仍以以下原生记录为准。

## Windows 原生验收

本机 Windows SDK/.NET 10.0.401。只操作独立诊断进程与新建诊断目录；不终止安装版应用，不修改真实课表、配置、开机自启、系统时钟、系统 DPI 或 Explorer。脚本启动进程按 PID 与精确路径收尾。

2026-10-16 13:17:24（+08:00）本机构建包含浮层卸载修正，UI 与时钟专项证据如下。最终功能二进制于 2026-10-17 13:36:01（+08:00）重新构建并冒烟通过，0 warning、0 error、smoke exit 0，追加真实恢复所需的通知注册及带时间的原始消息日志。Core/Widget 指纹未变，最终指纹见后文。

### 固定布局、字体与交互

最终版 UI 专项脚本 exit 0，证据目录 `C:\tjt-tools\issue12-ui-final4-20261016`。全部使用合成课表，固定 `--today 2026-09-14 --now 07:59`，默认 Mica 控制器，实际 144 DPI（150%），并非估算字宽截图。

- 1080×700 DIP 浅色宽窗：课程/教室、绝对日期/开课时间和完整组数量常驻，原顶栏和星期表头可用。
- 320×240 DIP 深色窄窗：辅助日期/时间退到悬停，课名先于教室让位，今与数量保留，摘要不增高。实际滚动后网格从节次 1 移至节次 5 以后，摘要仍在相同位置，水平/垂直滚动未覆盖旧按钮。
- 实际鼠标按住右下边缘缩放至 480×320 DIP，重排后摘要仍为单行；不写真实用户尺寸。
- 实际点击刷新图标，诊断 fixture 从同刻 2 条改成 3 条，摘要和旧今日计数同步变为 3；已打开旧 Popup 的卸载无 fatal。另以定向回归目录 `C:\tjt-tools\issue12-ui-reload-regression2-20261016` 确认新浮层能重新打开，UI Automation 的可见完整文本包含新第三教室，且有截图。
- 实际菜单隐藏挂件，再向该诊断 PID 自己的托盘消息窗口发左键回调，确实经过 `HideWidget → ShowWidgetAgain`，日志出现 fresh `show-again`。这是合成托盘回调，不冒充人工物理点击，但不同于绕过应用状态的裸 ShowWindow。
- 30 条同刻组浮层可移入并滚至第 30 条；指针保持在浮层中，跨实际普通 `clock-tick` 后仍打开，底部内容与滚动位置保持。完整悬停与 UI Automation 文本均保留全部组。

代表截图：`wide-light.png`、`narrow-dark.png`、`narrow-dark-grid-scroll.png`、`narrow-dark-resized.png`、`narrow-dark-reloaded.png`、`narrow-dark-reshown.png`、`large-group-hover-bottom.png`、`large-group-hover-after-clock-tick.png`。关键截图已核对，脚本退出码不代替视觉判断。

2026-10-17 对恢复补丁后的最终二进制再次运行整套 UI 专项，`C:\tjt-tools\issue12-ui-power-final3-20261017`，exit 0。用户点击诊断窗口后获得真实输入焦点，宽浅/窄深、边缘缩放、重载 2→3 与新完整浮层、隐藏重显及 30 条浮层跨 clock-tick 均再次通过，关键截图已读取核对；副本三个 DLL 的 SHA256 与后文一致。此前两轮因其他前台程序占用输入或诊断窗口未获焦点而停止，不计通过，也未强行向其他进程投递鼠标。

本轮 `narrow-dark-grid-scroll.png` 仍有浮层遮挡，不能仅凭截图名称或脚本 exit 0 宣称已滚动网格。补跑独立定向回归 `C:\tjt-tools\issue12-ui-grid-final-20261017`，显式向浮层外投递真实指针移动后再滚动网格，并要求实际 UI Automation ScrollPattern 的纵向百分比增加。该轮 exit 0，真实纵向百分比由 0 增至 65.7821215031348；截图核对网格已移至节次 5～9，摘要仍在原位置且浮层已关闭。同时再次通过缩放、重载、新浮层和隐藏重显。

### 时间推进与诊断隔离

最终版时钟脚本 exit 0，`C:\tjt-tools\issue12-clock-final3-20261016\report.log`：

- 四种 today/now 合成、单次完整采样、同课双教室 N=2 均通过。
- 无效日 `2026-02-30`、无效月 `2026-13-01` 在入口忽略，不中断首次渲染。非法内部受控时钟明确失败退出，负例预期 exit 1，未静默退回真实钟。
- **真实系统时钟推进**：同一实例未传 today/now，目标开课 `2026-10-16T13:21:00+08:00`；`13:21:00.0108775+08:00` 已移出刚开始的完整双安排组，转为下一条，观察延迟 10.8775 ms。
- 三种合成恢复广播、系统时钟变化与原生显示消息均进入重新采样分支。
- **可推进受控午夜**：同一 PID 55308，从 `2026-09-13T23:59:55.1806617+08:00` 的 later，到真实 DispatcherTimer 唤醒时 `2026-09-14T00:00:00.0559028+08:00` 的 today；随后 `00:00:06.1736014` 继续推进。未传 today/now，不修改系统钟。内部环境钩子仅 fixture+size 实例读取，不是用户设置或另一条刷新管线。
- 真实 settings/timetable 的 SHA256 和 Run 值/类型前后不变。

真实墙钟午夜未等待；上述受控午夜满足规格允许的可推进受控钟路径，不声称曾修改系统时间或实际等待到午夜。合成 WM_POWERBROADCAST 只证明分支，不替代真实睡眠恢复。

本地构建脚本原先会按进程名停止全部实例，已限制为固定构建输出目录保护安装版，补充失败即停与 PowerShell 退出码句柄缓存。诊断读日志使用 FileShare.ReadWrite，避免默认只读共享与应用追加日志冲突、造成验收漏行。

2026-10-17 恢复补丁后的同一最终二进制重新运行时钟专项，`C:\tjt-tools\issue12-clock-power-final-20261017\report.log`，exit 0、failures=0。真实 `14:11:00` 开课后 12.1569 ms 移出双安排组；受控午夜在 `00:00:00.0583914+08:00` 从后切今，`00:00:06.0983629` 继续推进。诊断组合、负例和真实数据/自启检查均再次通过。

### 二进制指纹（SHA256）

在最终原生输出目录 `C:\tjt-tools\work\dotnet\Tjt.App\bin\Release\net10.0-windows10.0.22621.0\win-x64`：

- Tjt.App.dll：`3d289e85710f621e85cf1b09dc7720ede7666f61a55978fdb56f8d2b6d76d3ac`
- Tjt.Core.dll：`dbb363a0b62954fb627dc2933731a7989c6e920335d3f51e8a02c8b7d09829e0`
- Tjt.Widget.dll：`056adc05da357fe5ae46cf927c21627dd1f81d509e344a03baf909bfd7a11b93`

### 真实 Windows 睡眠恢复

2026-10-17，用户亲手通过 Windows 睡眠并唤醒，未使用合成恢复消息。最终探针 exit 0，证据目录 `C:\tjt-tools\issue12-real-resume-fixed-20261017-1404`，包含 `report.log`、`os-events.json`、`app.log` 和本次合成课表。

- 同一诊断 PID 25268，未传 `--today`、`--now`，清除内部受控时钟环境变量。初始完整同刻组 `Found count=2`，开课时刻为 `2026-10-17T14:07:00+08:00`。
- OS `Microsoft-Windows-Kernel-Power` 事件 506 为 `14:05:19.1517067+08:00`，事件 507 为 `14:08:10.7635994+08:00`，睡眠持续 171.6118927 秒。两端点实际跨过开课时刻，不是醒后才等待定时器越界。
- 原始恢复消息 `0x12`、`0x7` 分别记录于 `06:08:11.0160337Z`、`06:08:11.0381658Z`。其后第一次重新计算的采样为 `06:08:11.0546484Z`，`reason=power-resume source=system`，采样与有效现在一致，距 OS 恢复约 291 ms。
- 摘要更新为 `NoFuture count=0`，正确移出已开始的两条安排。实际走完注册、通知、全新采样、既有渲染管线与正常关闭反注册；真实 settings/timetable/autostart 校验前后不变。
- 探针同时要求最近一次合格 OS 睡眠/恢复配对、对应时间窗口内的原始通知、该通知之后的第一条新采样，拒绝用此前短睡眠的通知或旧 clock-tick 作为本轮证据。入睡必须早于开课，唤醒必须不早于开课。

此前一次等待于 `13:51:39` 超时，用户实际在 `14:00:43` 至 `14:03:14` 睡眠，当时诊断已退出，该轮明确记为 NOT-RUN，不用于通过依据。本次先确认用户准备好，再启动新的隔离实例并延长等待，未修改系统睡眠设置。

验收员已只读复核本轮 OS 事件、合成课表、原始应用日志及副本 DLL 指纹，确认上述证据链成立。这不是独立重做睡眠。证据边界也保留：本机验证的是 Modern Standby 路径，不声称覆盖 S3 或所有设备；报告记录了真实数据及单个 Run 项的前后 hash 比较结果，未留历史 hash 清单供复算；OS 导出未含 RecordId/XML，也未在执行时归档脚本快照。

本票规格内验收已完成。仅收尾 #12，不自动认领或启动 #13；功能提交与正式发版分开处理，本次不发版。
