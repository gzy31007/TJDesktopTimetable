# 周次视图：已定稿决策台账 + 实施清单

> **状态**：2026-09-21 一轮 `/grill-with-docs` grilling 收口，全部决策由用户逐条拍定（"全部按推荐"）。
> **给新会话**：按本文件实施，**不要再重新决策**；下面每条 decision 都是用户的选择，不是建议。
> **本文件的生命周期（Q23）**：实施完成后把结论并入 `README.md`（已知限制 + 路线图）与 `AGENTS.md`（一条），并**删除本文件**；ADR 与 `CONTEXT.md` 永久保留。

## 1. 术语（已定稿，glossary 见 `CONTEXT.md`）

本仓文档原先把「**周次过滤**」定义为"只看单周 / 双周"（README 已知限制 + 路线图那条 `[ ]`），而本次需求"按当前所在周实时更改课表"是**另一个**维度。定稿：

- 用户可见概念统一叫 **周次视图 (Week View)**，四态互斥：`全部 / 本周 / 单周 / 双周`。
- `周次过滤`、`单双周过滤`、`weekFilter` 进 glossary 的 `_Avoid_`。
- 枚举改名 `WeekFilter` → `WeekView`（C# 侧），`Weeks.ResolveFilter` 入参随之变。

## 2. 现状事实（实施前已核实）

- `dotnet/TjtCore/Layout.cs`：`BoardOptions.WeekFilter { All, Odd, Even }`（默认 `All`）+ `BuildBoard` 按 `Weeks.ResolveFilter` 生成掩码丢课，`weeks == 0` 也丢并计入 `HiddenSessions`。**核心逻辑已存在且有单测**，缺的只是 `Current` 态与全部 UI 入口。
- `BoardState` 有 `CurrentWeek`（`Time.TermWeekAt` 算，假期 `null`）与 `HiddenSessions`；`CurrentWeek` 目前**只**被 `Tjt.Widget/BoardVisual.BuildHeader` 用来显示「第 N 周 / 假期」，**不参与过滤**。
- 「今日 N 节」现在的口径是 `state.Blocks.Count(block => block.Day == 今天)`（`BoardVisual.BuildHeader`）——`All` 视图下会把别的周的课算进今天，是个既有错误。
- 两端 `TrimEmptySlots = true`（行范围 = 有课范围）；行高由 `BoardVisualBuilder.FitGeometry` 按可用高度压。
- 刷新：Windows `MainWindow` 的 `_nowTimer` 30s 无条件 `Render`；Linux `MainWindow` 的 `_clock` 1min + 跳帧签名 `(NowLineTop, WeekText, TodayText)`（已含「第 N 周」→ 跨周自动重建）。
- 内置学期表 `TongjiTerms.Presets`：`122` = 2026-2027 学年第 1 学期，开学 `2026-09-14`，16 周；`124` = 2027 学年。内置示例课表 `DemoData` 用 `calendarId 122`。
  → **权威验收日期 `--today 2026-09-21` = 第 2 周**（2026-09-21 是周一）。
- Windows `settings.json` 落盘：`SettingsStore.Options` 无 `JsonStringEnumConverter` → 现有枚举（`ThemeMode` / `MaterialMode`）都是**数字**。
- 托盘菜单：`Tjt.App/Win32/TrayIcon.cs` 用 `AppendMenu(flags = MfString | MfChecked)`，**不设菜单位图**；Win32 的 `MFT_RADIOCHECK` 在没有位图时不显示圆点。
- Linux 线**没有**设置窗口、**没有**托盘 → 它的入口只有挂件 `⋯` 菜单。
- `--smoke` 自检（`.tools/build-winui.ps1 -RunSmoke`）只带 `--fixture`，**不带日期** → 默认视图一变成本周，`[smoke] ok blocks=…` 会随当天日期漂。
- 现成诊断开关：`--fixture / --log / --no-backdrop / --desktop-layer / --weekend / --no-weekend / --now HH:mm / --material / --size`（见 `AppStartupOptions`）。`BoardOptions.Today` 字段本身已存在（测试在用）。

## 3. 决策台账（用户已拍定）

### 语义与范围
1. 新增「周次视图」一个维度，四态互斥 `All / Current / Odd / Even`；**不做**正交组合（避免"本周 ∩ 单周"这种能推出空课表的矛盾态）。
2. 默认 `Current`（只看本周）；设置里可切回全部。
3. 「本周」= 今天按 `开学日` 推出的教学周；周一为一周起点，第 1 周从开学日所在周的周一起算。
4. 冲突判定、今日高亮、"单双周错开不算冲突"一概**不受视图影响**：视图只决定画哪些。

### 兜底
5. `CurrentWeek == null`（开学前 / 学期结束后 / 开学日未知）→ **静默退回显示全部周次**，header 照旧「假期」。挂件绝不能空。
6. 本周一节课都没有（如 `Odd` 而本周是双周）→ **空网格**（行范围退回 1-11 节），不加「本周无课」文案。

### 布局与统计
7. 接受每周重排：并排块数、列宽、行范围、行高都随本周变（"接受跳变"，不做占位/淡显）。
8. 被本周视图丢掉的时段**计入** `HiddenSessions`（与单双周同一口径）；「隐藏周末」那条既有例外（被 `visibleDays` 丢的不计）保持不变。
9. 「今日 N 节」**永远按当前教学周**算，与视图无关；假期/开学日未知时退回"今天有几条安排"。→ 需要 `BoardState` 暴露一个"当前周今日条数"，不能继续数 `Blocks`。

### 入口与 UI
10. Windows 三处同源：设置「外观」页 + 挂件 `⋯` 菜单 + 托盘。Linux 只有 `⋯` 菜单（无设置窗口/托盘）。
11. 设置页控件用 `ComboBox`（四项），说明文字**静态**写明"开学日未知时显示全部周次"。
12. 文案：子菜单「周次视图」，四项「全部周次 / 只看本周 / 只看单周 / 只看双周」。
13. `⋯` 菜单用 `RadioMenuFlyoutItem`（互斥）；托盘用**普通勾** `MF_CHECKED`（**不要** `MFT_RADIOCHECK`）。
14. 挂件上**不加**任何"当前非全部周次"的标记（header 的「第 N 周」就是信号）。

### 存取与诊断
15. `WidgetSettings.WeekView` 用**字符串**落盘（在枚举类型上挂 `[JsonConverter(typeof(JsonStringEnumConverter))]`，**不要**动全局 `Options`，别影响 `ThemeMode` 的既有数字格式）；未知取值 → 整份设置回落默认（与"坏 JSON 就回落"同一兜底）。Windows / Linux 两份 `WidgetSettings` 拷贝同形。
16. 新增 CLI：`--today YYYY-MM-DD`（覆盖"今日"，连带决定当前周 / 今日高亮 / 今日节数；**唯一真源**）+ `--week-view all|current|odd|even`（只影响本次运行、不落盘）。**不加** `--week N`。
17. **凡断言观感的验收/冒烟必须显式带 `--today`**：`build-winui.ps1` 的 `-RunSmoke` 加 `--today 2026-09-21`，并把期望数字改成"第 2 周过滤后"的值。

### 验收与文档
18. 平台无关单测（四态 × 跨周 × 假期兜底 × 今日节数口径）+ 新增 `.tools/verify-weekview.ps1`（纯 ASCII）：默认本周 / 切回全部 / `--today` 跨周 / 假期兜底 / 三入口同源。Linux 侧只跑 `dotnet test`（X11 脚本验的是贴桌面层，覆盖不到本功能）。
19. 文档：`CONTEXT.md`（**已建**）+ 两条 ADR（见下）+ README（已知限制改口径、路线图勾掉）+ AGENTS.md 加一条。**不**新建永久 `docs/week-view.md`（本文件实施后删除）。
20. 内置示例课表 `DemoData` **不动**（现有 1-8/9-16 交替 + 单双周 + 全周三种形态已够肉眼验收）。
21. 不改版本号、不触发发版（`v*` tag 才发版）。
22. 交付方式：分主题 Conventional Commits（英文类型 + 中文简述）提交到 `main`；单测 + 本机能跑的 Windows 冒烟全绿后**推送 origin/main**；若 WinUI 冒烟在本机跑不起来，如实报告哪几条没跑并**暂停推送**。

## 4. 实施清单（分层，按仓库硬约束）

**`dotnet/TjtCore/`（平台无关，先做，Linux 可测）**
- [ ] `Weeks.cs`：`enum WeekFilter` → `enum WeekView { All, Current, Odd, Even }`；`ResolveFilter(WeekView view, int? currentWeek, int totalWeeks)`（`Current` 且有当前周 → 单位掩码；`Current` 但当前周为 `null` → `null`（不过滤）；`All` → `null`）。
- [ ] `Layout.cs`：`BoardOptions.WeekFilter` → `WeekView`（默认 `Current`）；`BuildBoard` 用 `currentWeek` 生成掩码、计入 `HiddenSessions`；`BoardState` 新增"当前周今日条数"（建议 `TodaySessionCount`，语义 = 今天 × 当前周命中的上课安排数；假期退回"今天的全部安排"）。
- [ ] `Time.cs`：如需"某天在某一周命中哪些课"，已有 `SessionsOnDate`（按日期查周次）可直接复用；「今日节数」优先用它而不是数色块。
- [ ] 单测：`WeeksTests`（四态 + `null` 周 + totalWeeks 边界）、`LayoutTests`（`Current` 过滤 + 假期兜底 + `HiddenSessions` 计数）、`E2ETimetableTests`（真实 fixture 在 `--today 2026-09-21` 下的块数/隐藏数）。

**`dotnet/Tjt.Widget/`（纯计算）**
- [ ] `BoardVisual.BuildHeader`：今日节数改读新的 `TodaySessionCount`（不再 `state.Blocks.Count(...)`）；四态下 header 文案规则不变。
- [ ] `BoardVisualTests`：钉住"`All` 视图下今日节数仍按当前周"、"`Odd` 视图双周 → 今日 0 节（`TodayText == null`）"。

**`dotnet/Tjt.App/`（Windows 外壳）**
- [ ] `Data/WidgetSettings.cs`：加 `public WeekView WeekView { get; init; } = WeekView.Current;`（注意默认值**必须**是 `Current`）。
- [ ] `AppStartupOptions`：加 `--today YYYY-MM-DD` 与 `--week-view <all|current|odd|even>`；`--today` 透传到 `BoardOptions.Today`，`NowMinutes` 既有路径不动。
- [ ] `MainWindow`：`WeekViewEnabled`（CLI 覆盖后的有效值，与 `WeekendEnabled` 同款）→ 喂 `BoardOptions`；`ApplySettings` 里视图一变即重排 + 落盘；`Render` 的日志加 `[weekview] view=… today=… week=…` 之类 ASCII 锚点。
- [ ] `Rendering/SettingsView.cs` + `SettingsWindow`：外观页加一行「周次视图」`ComboBox`（标题/说明静态）。
- [ ] `Rendering/BoardRenderer.cs`：`⋯` 菜单加「周次视图」子菜单，四项 `RadioMenuFlyoutItem`，选中态读有效值。
- [ ] `Win32/TrayIcon.cs`：菜单支持子菜单 + 勾选（`MF_CHECKED`）；四项单选语义靠"只有当前项打勾"。
- [ ] `App.xaml.cs`：自检输出加视图/日期，便于脚本断言。

**`dotnet/Tjt.Linux/`（Avalonia 外壳，同形但入口只有菜单）**
- [ ] `Data/WidgetSettings.cs` 加同名字段（字符串枚举，两端共用一份 `settings.json`）；`MainWindow` 喂 `BoardOptions`、`BuildActions` 加视图动作、`Rendering/BoardRenderer.cs` 的 `⋯` 菜单加四项；`AppStartupOptions` 同样支持 `--today` / `--week-view`。**不要**新增视觉规则。

**脚本 / 文档**
- [ ] `.tools/build-winui.ps1`：`-RunSmoke` 加 `--today 2026-09-21`；重算并更新基准数字；可选加 `-WeekView` / `-Today` 开关。
- [ ] `.tools/verify-weekview.ps1`（新建，纯 ASCII）：A 默认本周（第 2 周块数 < 全部）· B `--week-view all` 恢复旧块数 · C `--today 2026-09-28`（第 3 周）块数与 A 不同 · D 假期兜底（`--today` 给一个开学前的日期 → 退回全部 + header 假期）· E 三入口同源（设置页行可建、`⋯` 菜单项存在）。每条都显式带 `--today` 与 `--week-view`。
- [ ] `README.md`：已知限制那条改成新口径；路线图 `[ ] 周次过滤（只看单周 / 双周）` → `[x]` 并改写为「周次视图（本周 / 单周 / 双周）」。
- [ ] `AGENTS.md`：加一条（默认本周 + 假期兜底 + 今日节数口径 + `--today` 必须显式 + 三入口/托盘普通勾）。
- [ ] `docs/adr/0001-默认周次视图为本周.md`、`docs/adr/0002-周次视图用字符串枚举落盘.md`（已写好）。
- [ ] 删除本文件（结论并入上面两处）。

## 5. 验收命令

```bash
# 平台无关单测（推送前必跑）
DOTNET_CLI_HOME=$PWD/.tools/dotnet-home ./.tools/dotnet/dotnet test dotnet/TjtTimetable.slnx

# Windows 侧冒烟（WSL interop；-RunSmoke 已带 --today 2026-09-21）
PS=/mnt/c/Windows/System32/WindowsPowerShell/v1.0/powershell.exe
$PS -NoProfile -ExecutionPolicy Bypass -File '\\wsl.localhost\Ubuntu-24.04\root\TJDesktopTimetable\.tools\build-winui.ps1' -RunSmoke
$PS -NoProfile -ExecutionPolicy Bypass -File '\\wsl.localhost\Ubuntu-24.04\root\TJDesktopTimetable\.tools\verify-weekview.ps1'
```

## 6. 容易踩的坑（实施时再看一眼）

- 默认值写错成 `All` = 功能白做（Q2 明确要 `Current`）。
- 「今日 N 节」是**修正既有错误**，不是只对新视图生效：改完 `All` 视图的该数字也会变（要更新相关断言）。
- `--smoke` / 所有断言脚本漏 `--today` → 基准随日历漂，一周后周期性假 FAIL。
- 枚举字符串落盘**不要**动全局 `JsonSerializerOptions`（会改掉 `ThemeMode` / `MaterialMode` 的既有数字格式）。
- 托盘**别**试 `MFT_RADIOCHECK`（无位图不显示）。
- 周次掩码 bit0 = 第 1 周；单双周掩码按 `term.TotalWeeks` 生成，别硬编码 `0x5555/0xAAAA`；`dayOfWeek` 7 = 周日。
- 日志锚点保持 ASCII（验收脚本要匹配）；日志/文档不得出现学号、姓名、cookie、token。
- Linux 线没有设置窗口与托盘，别在那边"补齐"三入口。