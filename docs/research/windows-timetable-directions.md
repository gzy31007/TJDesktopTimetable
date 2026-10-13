# Windows 桌面课表后续方向证据

研究日期：2026-10-14，Asia/Shanghai。对应[研究票 #6](https://github.com/gzy31007/TJDesktopTimetable/issues/6)及[地图 #5](https://github.com/gzy31007/TJDesktopTimetable/issues/5)。

本轮只研究同济学生使用的 Windows 轻量桌面课表。交大保留现有支持，不主动扩校；不研究 Linux、通用待办、作业、成绩或班级管理。不实现功能，不决定路线顺序。

## 读法与限制

“事实”指官方文档或源码明确支持的行为；“反馈”指公开报告者提出的请求或遇到的问题，不代表普遍需求；“候选”是本研究对当前项目的推测，尚未获用户确认。

选择少量产品，不做功能数量排名。WakeUp 的大学生个人课表场景更接近本项目，但手机触控、后台权限、小部件刷新机制不等于 Windows。ClassIsland 与 Class Widgets 提供桌面显示和提醒的参考，但班级大屏的共用时间表、轮换课表、声音提醒不等于个人电脑需求。ClassIsland 的[官方入门](https://docs.classisland.tech/get-started/notification.html)明确指向班级大屏部署；Class Widgets 的[项目说明](https://github.com/Class-Widgets/Class-Widgets/tree/dd76c17cb807593c830cfda4746eaa5fc265dec6)提供班级桌面组件语境。

资料以官方站和项目仓库为主。搜索只用于发现，结论来自读取原文或 GitHub API。Class Widgets 老仓库获取时已归档，描述要求转用 Class Widgets 2；本文只使用旧版固定提交 `dd76c17cb807593c830cfda4746eaa5fc265dec6`，不声称代表新版。[Class Widgets 文档站](https://class-widgets.github.io/cw-docs/guide/schd/schdswap.html)页尾明确标为非官方，不把它当一手功能证明。WakeUp 的[Android 更新日志](https://wakeup.fun/doc/update/android.html)可读内容止于 2022 年，本文证明文档记载的能力，不保证与当前商业版本完全一致。

反馈样本主要来自 ClassIsland。未获得足够可核实的 WakeUp 公开原始反馈，也没有本项目同济用户访谈或使用频次数据。因此无法给候选排序、估算覆盖率或认定“学生普遍需要”。

## 当前仓库基线

源码基线为 `45b37dd8ff3677c92ea80b308fef0d046ab9e981`。下面链接固定到该提交，缺失能力是静态调查结论，不是本轮 Windows 真机验收结论。

- 四态周次视图已有，`All / Current / Odd / Even`，默认只看本周；没有任意第 N 教学周浏览入口。文案及兜底见 [WeekViewLabels.cs:16–32](https://github.com/gzy31007/TJDesktopTimetable/blob/45b37dd8ff3677c92ea80b308fef0d046ab9e981/dotnet/Tjt.Widget/WeekViewLabels.cs#L16-L32)。指定浏览周与真实今日必须分开，不能借改系统日期替代。
- 课程卡片已有悬停详情，见 [BoardRenderer.cs:552–557](https://github.com/gzy31007/TJDesktopTimetable/blob/45b37dd8ff3677c92ea80b308fef0d046ab9e981/dotnet/Tjt.App/Rendering/BoardRenderer.cs#L552-L557)。当前卡片没有点击详情入口。不能把“新增完整详情”当成从零开始的需求。
- 顶部刷新只调用本地重载，见 [MainWindow.xaml.cs:658](https://github.com/gzy31007/TJDesktopTimetable/blob/45b37dd8ff3677c92ea80b308fef0d046ab9e981/dotnet/Tjt.App/MainWindow.xaml.cs#L658)及 [MainWindow.xaml.cs:784–796](https://github.com/gzy31007/TJDesktopTimetable/blob/45b37dd8ff3677c92ea80b308fef0d046ab9e981/dotnet/Tjt.App/MainWindow.xaml.cs#L784-L796)。它重新读文件，不向教务请求最新课表。
- 下一节算法已在核心层，但 Windows 显示层未调用；它扫描今日起 14 天，含正在上课的课。见 [Time.cs:192–227](https://github.com/gzy31007/TJDesktopTimetable/blob/45b37dd8ff3677c92ea80b308fef0d046ab9e981/dotnet/TjtCore/Time.cs#L192-L227)。末尾 `return inProgressFallback` 会返回今日已结束的候选，`inProgressFallback ??= item` 实际保留首个候选，不是注释写的最后一节。现有[测试:245–250](https://github.com/gzy31007/TJDesktopTimetable/blob/45b37dd8ff3677c92ea80b308fef0d046ab9e981/dotnet/TjtCore.Tests/TimeTests.cs#L245-L250)还明确断言负倒计时被描述成“即将开始”。接 UI 前必须审语义，不能直接宣布已有可靠倒计时。
- 同济依赖内置学期校历快照，见 [TongjiTerms.cs:4–20](https://github.com/gzy31007/TJDesktopTimetable/blob/45b37dd8ff3677c92ea80b308fef0d046ab9e981/dotnet/TjtCore/TongjiTerms.cs#L4-L20)。已有文件导入路径，没有日期纠正 UI；交大导入取校历的现状不作为主动扩校理由。
- 多屏与 DPI 基础已有。本轮未发现课程提醒、图片或 ICS 导出、独立紧凑模式、日期例外调课入口；已有版本更新通知不是课程提醒。调查 Windows 外壳、核心模型、菜单、导入及测试，不运行会落盘的诊断，不改变真实课表或设置。

## 下一节与倒计时

事实：Class Widgets 固定版源码有 `get_countdown`、`get_next_lessons`，明确返回“目前课程已结束”；还保存通知去重状态。见 [main.py:430–588](https://github.com/Class-Widgets/Class-Widgets/blob/dd76c17cb807593c830cfda4746eaa5fc265dec6/main.py#L430-L588)。可借鉴的事实是它区分时段状态，而非只有一个不断递减的数字。

反馈：ClassIsland [#1527](https://github.com/ClassIsland/ClassIsland/issues/1527)是获取时仍开放的倒计时提醒请求，背景是放学后自动关机前的提示文案不更新。它不是下一节课程查询需求，也不能证明同济学生需要秒级倒计时。本轮没有可靠的直接“下一节入口”用户样本。

候选：在现有头部增加一行当前课或下一节摘要，包含课程、教室和距离开始的分钟；或只展示“下一节”而不含当前课。显示应读取真实教学日期，不受浏览周或 All/Odd/Even 过滤影响。需先决定今日无课时显示跨日下一节，还是“今日无课”；学期结束、无校历、扫描期内无课也需明确空态。

成本判断：中等，纯计算和现有头部可复用，但要修语义并钉住边界、同刻并行课程、午休、跨日和休眠恢复。秒级刷新、声音或通知不是这个候选的必要部分。此为工程估计，不是排期。

## 前后周或指定教学周

事实：WakeUp [主界面文档](https://wakeup.fun/doc/main_interface.html)记载周数轴可点按或滑动跳转指定周，非本周有标记并能回当前周；其[课表设置](https://wakeup.fun/doc/settings/schedule_settings.html)把学期开始日期、当前周和学期周数列为课表数据。跳到某周查看与纠正当前周是不同操作。

反馈：ClassIsland [#2005](https://github.com/ClassIsland/ClassIsland/issues/2005)获取时开放，报告第三周被分到双周且不同星期结果不同。它是报告者遇到的单双周问题，未在本轮复现，不能当成对本项目的 bug 证明，也不是指定周浏览请求。

候选：上一周、下一周、回本周，必要时再加第 N 周选择。必须显示浏览周及日期范围，并保持今日高亮、今日节数、冲突判定、下一节与提醒的真实时间语义。不能把现有四态周次视图改名后声称已支持任意周。

成本判断：中等。问题不在画一个下拉框，而在分离浏览周与真实周。需要定下假期能否浏览、跳转是否持久化、重新启动是否回本周；无开学日时不能悄悄捏造日期。本轮直接需求反馈不足。

## 详情入口与紧凑展示

事实：WakeUp [主界面文档](https://wakeup.fun/doc/main_interface.html)支持点击课程打开详情、长按复制信息，并能切换同一格中的冲突课程。其[小部件文档](https://wakeup.fun/doc/widget.html)列有今日、近日、一周和日视图。这是不同信息密度的展示方式，不是 Windows 上隐藏部分文字就等价实现。

事实：ClassIsland [组件文档](https://docs.classisland.tech/app/component/)支持分别添加、删除、排列组件，设置固定宽度或宽度限制，超长内容会截断。可以确认其显示可配置，不能因此要求本项目做组件框架。

反馈：ClassIsland [#466](https://github.com/ClassIsland/ClassIsland/issues/466)请求仅显示倒计时天数，理由是目标已知无需重复标题。获取时已以 completed 关闭；维护者[评论](https://github.com/ClassIsland/ClassIsland/issues/466#issuecomment-2443775321)指出空标题配紧凑模式能得到类似结果。它支持“重复信息可以省略”的具体场景，不证明整张周课表需要紧凑模式。[#2030](https://github.com/ClassIsland/ClassIsland/issues/2030)仍开放，报告编辑器表格对不同时间表显示不准确并请求按日分列，属于编辑档案视图的边界问题，不是点击详情的需求。

候选：保留悬停，再提供可停留、可复制教室和课名的点击详情；或增加“今日列表/下一节条”的可选紧凑展示。两者独立选择，不能一起包装成必需重构。点击课块还要与窗口拖动起手协调，避免查看详情时拖走挂件。

成本判断：点击详情低到中等，需要焦点、关闭和复制交互；新紧凑布局中等，会新增持久化、切换及无课空态的验收面。没有证据要求拖拽组件、插件平台或完整课程编辑器。

## 课程提醒

事实：WakeUp [导出课表文档](https://wakeup.fun/doc/export_schedule.html)记载 Android/鸿蒙日历文件默认提前 20 分钟提醒，可在上课提醒设置中调整后再导出。ClassIsland [提醒入门](https://docs.classisland.tech/get-started/notification.html)记载默认课前一分钟并可调整。两个默认值服务不同场景，不能拿任一值直接作为本项目默认。

事实：Class Widgets [main.py:430–449](https://github.com/Class-Widgets/Class-Widgets/blob/dd76c17cb807593c830cfda4746eaa5fc265dec6/main.py#L430-L449)有通知冷却及去重记录，说明提醒调度需要处理重复触发。源码存在不等于已证明它在所有运行环境可靠。

反馈：上述 #1527 是自动化倒计时请求，不是课程提醒迟到或漏发证据。本轮没有足够同济个人电脑用户反馈，也未核实手机提醒失败可对应 Windows 机制。

候选：用户主动开启“提前 N 分钟”的单次课前系统通知；也可不在本程序调度，只把提醒交给导出的日历。两条路径不必同时做。是否需要声音、是否点击打开详情、临时关闭方式、默认关闭与否需用户选择。

成本判断：中到高。既有版本通知只证明外壳能发通知，不能代替课程调度。跨周、休眠恢复、同刻多课、重复启动、修改课表后取消旧提醒、系统静音及勿扰均需验收。个人电脑关闭时不能承诺提醒到达。

## 图片与 ICS 导出

事实：WakeUp [导出课表文档](https://wakeup.fun/doc/export_schedule.html)明确有日历文件，并说明提醒参数；[Android 更新日志](https://wakeup.fun/doc/update/android.html)明确记载 ICS 导出、时区信息及提醒时间跟随课程提醒设置。此次已读取的官方资料没有足以确认图片导出的说明，不能用搜索摘要补成已实现事实。

反馈：本轮未获得可核实的图片或 ICS 导出原始用户请求。已有导出功能证明互通机制存在，不证明本项目用户需要它。

候选：图片供分享或留存，ICS 供导入手机或桌面日历。先问清使用目的。图片要明确导出本周、浏览周还是全部周次，是否隐藏教师等私人信息，是否使用稳定实底而不是依赖当前壁纸材质。ICS 要明确只导出课次还是带提醒、学期全部还是剩余课程、重复导入的事件身份及更新方式。

成本判断：图片中等，难点在清晰尺寸、长课名、并排课、周末及离屏渲染；ICS 中等，难点在周次展开、时区、节次起止、节假日或调课例外、转义和稳定 UID。导出可离线完成，不需要账号同步。图片和日历都含个人课表信息，应由用户主动选择保存路径，不自动上传。

## 校历纠正、教务更新与临时调整

事实：WakeUp [课表设置](https://wakeup.fun/doc/settings/schedule_settings.html)允许设置学期开始日期及当前周，[Android 更新日志](https://wakeup.fun/doc/update/android.html)记载新增调课工具。这只证明可以编辑，不能推导它会自动发现教务变更。

事实：ClassIsland [课表文档](https://docs.classisland.tech/app/profile/classplan.html)区分当日临时层与永久换课。临时层不修改原课表，第二天删除；换课默认写临时层，也可明确勾选永久换课。临时课表群另有可调的失效日期。不能把这几种生命周期混在一起。

反馈：ClassIsland [#1851](https://github.com/ClassIsland/ClassIsland/issues/1851)获取时仍开放，报告者希望加载临时课表后能在档案中建立临时层，方便大幅变动时编辑。这是入口和工作流诉求，不代表临时调整机制完全缺失，也不证明大学课表必须支持整套班级课表群。

候选需要拆开评估：

- 校历纠正：导入后校历未知或错误时，由用户明确校正第 1 周周一。不是“指定周浏览”，也不是自动拉取校历。
- 教务更新：保留本地重载，另设明确的“重新从教务获取”流程。需要处理登录失效、用户确认、失败保留旧数据及变更摘要。本轮没有一手证据可承诺同济接口长期稳定或免登录后台更新。
- 临时调整：按日期覆盖单次课程、教室或上课日，保留原始导入数据、显示有效期，并支持撤销。不能先改导入数据再寄望下次联网覆盖；下一节、提醒、浏览与 ICS 应消费同一份有效课次。

成本判断：校历纠正低到中等，前提是设计来源标记与恢复默认；联网更新中到高，认证和接口维护是持续成本；临时调整高，日期例外与重新导入的合并规则会改变领域模型。只做一个清晰入口不等于承担全部同步机制。本轮没有同济用户实际调课频次或校历错误率数据。

## 留给用户决定

上述主题都是候选，不构成排序或实施承诺。下一轮只需先决定哪些场景确实发生，再为选中的场景写验收标准：

- 看明天或某教学周，是偶尔预览还是日常使用？是否需要记住浏览位置？
- 下一节摘要应包含当前课吗？今日无课是否仍显示跨日下一节？分钟提示是否已足够？
- 悬停已经能读到详情，是否还需要点击停留和复制？紧凑展示会替换周网格还是并存？
- 提醒要由本程序承担，还是导出的日历承担？能否接受电脑关闭或系统勿扰时不提醒？
- 导出主要为分享图片，还是跨设备日历互通？是否需要更新已导入的事件？
- 校历纠正、重新从教务获取、一次性临时调课，哪一种是实际痛点？覆盖或重新导入时怎样处理手动调整？

## 验证与可信度

官方行为与源码基线的可信度较高，但没有安装对照产品做实测。公开反馈可信到“报告者提出了此请求或问题”，不能升格成普遍性或修复结论。成本与候选是工程推测，无时间预算估算。

来源获取时状态已在各段标出。GitHub 内容采用项目 API 读取正文与可用评论，不发布账号、诊断标识或私人数据。固定源码链接可复核；官方文档与 issue 状态会随时间变化。

本轮仅新增本文档。验证包括静态检查本仓库调用链、原始来源复核、Markdown 链接与敏感内容检查，以及在独立研究工作树使用本机 .NET SDK 执行 `dotnet test dotnet/TjtTimetable.slnx`。334 项通过，0 失败，0 跳过，退出码 0。未运行 Windows 窗口、学校登录、抓取落盘或通知诊断。基线测试通过不表示候选功能已经实现或通过验收。
