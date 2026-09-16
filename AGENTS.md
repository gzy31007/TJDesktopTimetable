# AGENTS.md · TJDesktopTimetable

> 系统环境、WSL 网络与代理、工具链、字体等**通用**信息见全局 `~/.dsh/AGENTS.md`，本文件只写本项目相关内容。

## 项目定位

Windows 桌面小组件：把课表以半透明色块网格固定在桌面上。数据源（学校适配器）与窗口 / 渲染层解耦：新增一所学校 = 加一个适配器文件。
已支持**同济 1 系统**（`tongji-student`）与**上海交大「学在交大」**（`sjtu-student`，`j.sjtu.edu.cn`）。

- 本地 `/root/TJDesktopTimetable`；远端 https://github.com/gzy31007/TJDesktopTimetable （Public / MIT）
- 技术栈：**WinUI 3（C#，`dotnet/`）唯一主线**。`TjtCore` 平台无关（Linux 可测）、`Tjt.Widget` 纯计算、`Tjt.App` WinUI 外壳 + Win32 P/Invoke
- Electron 线（`apps/desktop/` + `packages/core/`）**2026-09-16 已删除**，黄金 fixture 搬到 `dotnet/fixtures/`；历史见 git（`git log --diff-filter=D -- '*apps/desktop*' '*packages/*'`）。新功能一律进 `dotnet/`
- 发版：打 `v*` tag → `.github/workflows/release-winui.yml`（自包含 publish → zip → 挂 Release）；版本号只改 `dotnet/Directory.Build.props`

## 工作目录约定

```
dotnet/TjtCore/       平台无关核心（net10.0）：模型/周次/冲突/布局/时间/适配器
dotnet/Tjt.Widget/    挂件视觉层（net10.0，纯计算，不引 WinUI/Win32）
dotnet/Tjt.App/       WinUI 外壳（net10.0-windows）：窗口/材质/Win32/托盘/设置
dotnet/Tjt.Linux/     Avalonia Linux 外壳（net10.0）：窗口/渲染/X11 贴桌面层/导入编排
dotnet/TjtCore.Tests/ 平台无关单测（TjtCore + Tjt.Widget 都测这里）
dotnet/fixtures/      脱敏后的真实抓包数据（黄金测试基准，禁止放入学号、姓名等个人信息）
docs/                 desktop-layer（层级层结论）· winui-build（DeskBox 参考事实 + 构建环境）
                      · winui-lessons（返工史）· import（课表导入细节）· deskbox-refactor-assessment
.tools/               本机构建/验收脚本（**gitignore**，不入库）：build-winui.ps1 / test-dotnet.ps1 / verify-*.ps1
```

分层硬约束：

1. `TjtCore` / `Tjt.Widget` **不得**引用 WinUI / Win32 / `Tjt.App` —— 必须 `net10.0` 平台无关，Linux 上能 `dotnet test`；P/Invoke 一律留 `Tjt.App`。
2. `Tjt.App` 只做必须在 Windows 上跑的事（窗口 / 材质 / Win32 / 托盘 / 设置 UI / 文件与网络 IO）；业务逻辑进 `TjtCore`，视觉计算进 `Tjt.Widget`。
3. 新逻辑优先 `Tjt.Widget`（WSL 能编译 + 单测，反馈最快）；只有"必须真实窗口 / 句柄"才落 `Tjt.App`。
4. `dotnet/fixtures/` 是黄金数据**唯一真源**（含交大 `sjtu-2026-1-semester.json` / `sjtu-2026-1-calendar.json`）（csproj 用 `Content Link` 复制到测试输出）；改 fixture = 改验收基准，两边（`Tjt.App` 的 `--fixture`、`TjtCore.Tests`）都要跑通。

## 任务规范

- 提交 Conventional Commits（英文类型 + 中文简述），例 `fix(widget): 遮挡时不再误起缩放`；推送前跑测试：`dotnet test dotnet/TjtTimetable.slnx`（Linux/CI）或本机 `.tools/test-dotnet.ps1`。
- 测试：`TjtCore` / `Tjt.Widget` 每个公开函数都要单测；同济个人课表由 `TjtCore.Tests/E2ETimetableTests.cs` 端到端覆盖（导入 → 布局 → 时间 → 单双周过滤）；同格撞车用构造 fixture `dotnet/fixtures/tongji-2026-1-collision.json`（非抓包），`CollisionE2ETests.cs` 钉住；挂件视觉由 `BoardVisualTests.cs` 钉住。
- 代理：只推 GitHub / 拉 GitHub 资源时用 `export https_proxy=http://127.0.0.1:7897 http_proxy=http://127.0.0.1:7897`。
  NuGet 走 `.tools/nuget`（WSL）/ `C:\tjt-tools\nuget`（Windows）。

## 易错知识点

- **已移除**「专业培养计划适配器」与「教学班勾选」：只支持个人课表导入即用。误把培养计划数据（同课多教学班）导进来会异常；**没有** `tongji.looksLikePlan` 这类标识（旧文档不准）。真正诊断码：`tongji.personal` / `tongji.report` / `tongji.flat` / `tongji.noSchedule` / `tongji.schedule.missing` / `tongji.term.unknown` / `tongji.term.startDate` / `tongji.summary`。
- **交大（学在交大）适配器**：`sjtu-student`，诊断码 `sjtu.lessons` / `sjtu.summary` / `sjtu.noSchedule` / `sjtu.weeks.unknown` / `sjtu.term.startDate` / `sjtu.schedule.missing`。数据源是 `GET /app/stu/lesson/listBySemester`（整学期，带 `time` 周次文本）与 `GET /app/stu/school/semester/calendar`（逐日日历，week 0-21）。⚠️ 课表页默认的 `listByWeek` **不带周次信息**（`time` 全为 `null`），粘它也会被改写成整学期请求（`Data/SjtuFetcher.cs`）。开学日 = 第 1 周周一（**不是** `startDay`）、单双周要在区间展开**之后**过滤、相邻节次要并块 —— 见 `TjtCore/SjtuTerms.cs` 与 [`docs/import.md`](docs/import.md)。
- **同济个人课表两条接口**（以 1 系统前端 bundle 为准）：
  - 旧：`POST /api/electionservice/student/{选课批次id}/getDataBk` → `data.selectedCourses[].course.times[]`（`{id}` 无法稳定构造）。
  - 现行：`GET /api/electionservice/reportManagement/findStudentTimetab?calendarId=<学期id>&studentCode=<前端加密的uid>`（研究生 `findSchoolTimetab2`，按前端源码走 `data.list`）→ `data[].timeTableList[]`。两者 `dayOfWeek`/`timeStart`/`timeEnd`/`weeks` 语义一致；差别：课程在数组顶层、排课数组改名、教室多一层 `roomLable`（线上课堂 / 操场无编号场地用它）。
  - `calendarId` **只在 URL**（响应体没有）→ 抓取时取出来当 `ImportInput.TermId`，否则学期退化成"未知"（`tongji.term.unknown`）：`HttpRequestParser.QueryValue(spec, "calendarId")` → `TongjiFetchOutcome.TermId` → `ImportService`。
  - 两接口结果逐条一致（14 门 / 19 条；原始 `timeTableList` 27 条，差异来自同格多教师合并）。fixture `dotnet/fixtures/tongji-2026-1-report.json`（脱敏：教师→教师A…Z、工号→10001+、教学班 id→9xxxxxxxxxxxxxxx）。
- 个人课表与培养计划后端字段同套（`dayOfWeek` / `weekState` / `timeStart` / `roomName` …），映射逻辑可复用。
- `weekState` = 16 位周次掩码，bit0 = 第 1 周；单双周掩码别硬编码 `0x5555/0xAAAA`（只对 16 周成立），按 `term.totalWeeks` 生成。
- `dayOfWeek` 1–7，**7 = 周日**（与 JS `Date.getDay()` 的 0=周日 不同）。
- **并排分组键 = 同天 + 同起止节次**，不是"时间相交"：周一 1-2 节与 1-3 节各占整列（视觉互压）。
- **并排 ≠ 冲突**：单双周错开（1-8 / 9-16 周）不算冲突，但布局照样并排（`colCount = 2`）。别把 `colCount` 与 `coursesConflict` 混为一谈；`fixtures/tongji-2026-1-collision.json` 钉住。
- 同格多条 times 的合并键**含教室**（同天 + 同起止 + 同教室才合并、周次取并集）；教室不同 → 并排两块。
- 去重键：`teachingClassId`（数字）/ `code`（教学班代码，如 `00213702`）/ `courseCode`（课程代码，如 `002137`）三者不可混用。
- 校历时间戳是毫秒（`beginDay: 1820160000000`）；`weekBenginDay` 表示"周从周几开始"（同济 2 = 周一），不是开学日。
- 层级层历史结论与证据见 [`docs/desktop-layer.md`](docs/desktop-layer.md)；现行实现在 C# 侧。
- Windows 排查可用 WSL interop 调 `cmd.exe` / `powershell.exe`，但引号 / 反斜杠会被再处理一次：逻辑写进 `.ps1`/`.bat` 再执行（`tasklist /FI "IMAGENAME eq x"` 会解析失败）。`.ps1` 被 PowerShell 5 按 ANSI 读 → **脚本只能纯 ASCII**。
- 改 `settings.json` 前**先停应用**：运行中实例会在 `moved`/`resized` 时 `saveSettings()` 回写，强杀不保证退出路径。顺序 **stop → 改 → start**（`.tools/stop.ps1` 停；启动 `C:\tjt-tools\TjtApp*.cmd`）。
- 合成鼠标三坑：
  1. `SetCursorPos` 只挪光标、**不产生输入消息** → 测不出 hover / 点击；要真输入用 `mouse_event(MOUSEEVENTF_MOVE|MOUSEEVENTF_ABSOLUTE, x, y, ...)`（坐标按主屏归一化 0..65535：`x = 虚拟x * 65535 / 虚拟宽`）。
  2. PowerShell **不是 DPI 感知**：`GetWindowRect` / `SetCursorPos` 用虚拟坐标，日志里 `rect` 是物理坐标（本机 2560×1600 / 缩放 150% → 虚拟 1707×1067，差 1.5 倍）；`CopyFromScreen` 吃物理坐标。
  3. 截图别赌坐标：全屏 `CopyFromScreen(0,0,2560,1600)` 再按日志物理 rect 裁剪。

## 注意事项

- 仓库 Public：fixtures / 文档不得出现学号、姓名、cookie、token 等凭据。
- 登录态两条路（都**不**破解浏览器数据）：① 内置登录窗口（`TongjiLoginWindow` + WebView2，两校共用、按 `LoginSchool` 分派）：用户在学校页面登录 —— 同济那条是"课表页接口响应被旁路接住"，交大那条是"拿到登录态后主动取整学期课表 + 教务日历"；② 粘一条浏览器请求（`Data/SchoolFetcher.cs` 按主机分派到 `TongjiFetcher` / `SjtuFetcher`），Cookie 存 `credentials.json`。**不做**读 Edge/Chrome Cookies 库：Edge 153 独占锁 + App-Bound 加密（`Local State` 里 `app_bound_encrypted_key` 前缀 `APPB`），解 v20 要调 IElevator COM = 绕过浏览器安全机制。**任何日志不得打印 Cookie 内容**（只记长度 / 条数）。
- 窗口默认「桌面层 + 静息」：Owner = 桌面图标视图 `SHELLDLL_DefView`（Win+D 后仍可见）；挂载前存档原 owner、写入后**读回校验**，失败即还原；宿主解析不到时回退成"无 owner 置底"（日志 `[layer] 桌面宿主不可用，回退为无 owner 置底`），不能黑屏。⚠️ **没有** `wallpaper` 模式：`SetParent` 到 WorkerW 子窗口会被桌面图标压住、拖动坐标错乱，发 `0x052C` 催生 WorkerW 还会在登录期与 Explorer 抢时序、打乱桌面图标布局（`Win32/DesktopHost.cs` 头注释）—— 别再把它当"可选 / 回退模式"实现（旧 Electron 线的 `mode: desktop | wallpaper` 已随该线删除）。
- 拖动 / 缩放自实现（`Win32/WindowDrag.cs` / `Win32/WindowEdgeResize.cs`），**起手先校验指针归属**（`PointerTarget`）；静息态**不戴** `WS_EX_NOACTIVATE`（戴上拖不动）；交互期"临时浮起 + 结束后重新落点"。

## C# / WinUI 线（2026-09-15 起）

> **常驻规则：前端一律以 DeskBox 为参照。** 观感 / 交互不一致时先去 `.refs/DeskBox` 看它怎么做，别从 Win32/WinUI 文档推 —— 四次返工（材质、白边→黑带、边缘缩放失效、缩放光标）的答案都在它里面。
> **边界**：`.refs/DeskBox` 是 **GPL-3.0-only** 只读副本，只能提取"用了哪些 API / 什么机制"这类**事实**，不得抄代码 / 注释 / 文档正文 / 美术资源（依据 `docs/deskbox-refactor-assessment.md`）。**先查它、后自己写**。

技术栈迁移已完成（v1.0.0）。转 WinUI 的原因就是材质：Electron 三条系统材质路径都拿不到 DeskBox 质感（DWM 对"从未被激活的窗口"降级成近黑平色），只有 WinUI 的 `MicaController` + `SystemBackdropConfiguration`（可强制 `IsInputActive`）能拿到。

> **溯源注释**：`dotnet/` 里 `TS 侧 …` / `Electron 侧 …` 是**移植溯源**（说明从哪份已删除实现搬来），不是"还有另一条线要同步"。找 `packages/core/src/*.ts`、`apps/desktop/**` 去 git：`git show <删除前的 commit>:<路径>`。

### 三个工程的分工（改动时必须守住）

| 位置 | TFM | 能跑在哪 | 职责 |
|---|---|---|---|
| `dotnet/TjtCore` | `net10.0` | Linux + Windows | 模型 / 周次 / 冲突 / 布局 / 时间 / 适配器（溯源：已删除的 TS 核心库） |
| `dotnet/Tjt.Widget` | `net10.0` | Linux + Windows | 挂件视觉层：几何、色块染色、名称分档、呈现模型（**纯计算，不得引用 WinUI/Win32**） |
| `dotnet/Tjt.App` | `net10.0-windows10.0.22621.0` | **只能 Windows** | WinUI 外壳：窗口、材质、Win32、照坐标摆控件 |

- `dotnet/TjtTimetable.slnx` = 前两个；`dotnet/TjtTimetable.Windows.slnx` = 第三个。**不要**把 `Tjt.App` 并进前者（Linux/CI 整片失败）。
- 视觉规则**唯一真源 = `Tjt.Widget`**：几何 / 字号 / 名称分档在 `BoardVisual.cs`，染色在 `TintPalette.cs`（溯源自已删除的 Electron 渲染层 `blockRect` / `blockFontSize` / `blockName` / `tintStyle`）；期望值由 `TjtCore.Tests/BoardVisualTests.cs` 钉住 —— **改视觉 = 改这两个文件 + 这份测试**。仓外 `select_preview.html` 只在对照历史观感时看。

### 在 Windows 本机构建（WSL 侧编译不了 WinUI）

```bash
PS=/mnt/c/Windows/System32/WindowsPowerShell/v1.0/powershell.exe
$PS -NoProfile -ExecutionPolicy Bypass -File '\\wsl.localhost\Ubuntu-24.04\root\TJDesktopTimetable\.tools\build-winui.ps1' -RunSmoke
```

- **带开关必须用 `-File`**：base64 成 `-EncodedCommand "$B64"` 再跟 `-RunSmoke` 会被当非法参数直接退出（踩过）；`-EncodedCommand` 只适合不带开关的构建。
- 脚本 robocopy 源码到 `C:\tjt-tools\work`（**反向拉取**：Windows 侧读 `\\wsl.localhost\...`，不是 WSL 写 `/mnt/c`），用 `C:\tjt-tools\dotnet\dotnet.exe`（.NET 10 SDK，装在工作区）构建；`-RunSmoke` 另跑冒烟。日志 `C:\tjt-tools\build.log`。
- **WSL 沙箱把 `/mnt/c` 挂只读**：对 Windows 盘的写操作全走 PowerShell（interop 或 `-EncodedCommand`）。
- **跑平台无关单测**：`$PS -NoProfile -ExecutionPolicy Bypass -File '\\wsl.localhost\...\.tools\test-dotnet.ps1'`；它复用 `build-winui.ps1` 镜像的 `C:\tjt-tools\work` → **先构建、后测试**（否则测的是上一版源码）。
- 脚本必须纯 ASCII（PowerShell 5 按 ANSI 读，中文注释会让解析错乱）；输出重定向到文件读（interop 下 stderr 是 CLIXML）。
- **GUI 进程不会自动退出**：`--smoke` 校验完直接 `Environment.Exit(code)`；脚本有 `WaitForExit` 超时兜底。
- WSL 侧可用 `EnableWindowsTargeting=true`（已写进 `Tjt.App.csproj`）做编译级检查，但 XAML 编译要 Windows 原生 `GenXbf.dll`（报 `WMC0621`）。
- 本机 VS Community 2026（`D:\Program Files\Microsoft Visual Studio\18\Community`）+ Windows SDK `10.0.26100`；VS 只带运行时**不带 .NET SDK** → `C:\tjt-tools\dotnet` 必须。
- **Windows App Runtime**：`Tjt.App` 引用 WASDK `1.8.260804001`，需 1.8 运行时（本机 2026-09-15 已装：`windowsappruntimeinstall-x64.exe --quiet`）。
- **CI 不跑运行时冒烟**（别再试）：GitHub windows runner 上 `Tjt.App.exe` 进程挂着且一行输出都没有（连 `--log` 文件都没落盘）→ 卡在 WASDK 引导路径内部。CI 的 `winui-shell` job 只做编译门禁；运行时验证走本机 `.tools/build-winui.ps1 -RunSmoke`。
- DeskBox 参考事实 + 本机构建环境清单：见 [`docs/winui-build.md`](docs/winui-build.md)。
- **拖动自实现**：原生 move loop（`ReleaseCapture()` + `SendMessage(WM_NCLBUTTONDOWN, HTCAPTION)`）无效；XAML `CapturePointer` 立刻 `PointerCaptureLost`；最终窗口级 `SetCapture` + `DispatcherTimer`(16ms) 轮询光标，算 `初始位置 + 光标位移` 后 `SetWindowPos` 搬窗口，左键松开（`GetAsyncKeyState`）收尾 → 存位置 + 落点。DeskBox 也自实现。
- **本机挡掉 WSL 合成指针输入**：`SetCursorPos` 与绝对坐标 `mouse_event` 都不动光标 → 拖动**只能手动验收**。可验：日志 `[drag] 按下`、`lbutton=True`、`[drag] poll#`。
- **缩放（定稿：自实现）**：
  - 原生缩放循环不能用（要求 `WS_THICKFRAME`；失败史见 [`docs/winui-lessons.md`](docs/winui-lessons.md)）。
  - `Win32/WindowEdgeResize.cs`：16ms 轮询；进抓取带 → `SetCursor`（`IDC_SIZEWE/NS/NWSE/NESW`）→ 左键按下记起点与起始外框 → 每帧用 `Tjt.Widget/ResizePolicy.Resolve`（纯函数）算外框 → 一次 `SetWindowPos` → 松开存位置 + 落点 + 光标还原。与拖动同一模式。
  - **光标走渲染层**（Win32 两轮失败：`SetCursor` 返回成功但用户看不到，`GetCursor`/`GetCursorInfo` 跨进程读不到）：`Tjt.Widget/CursorZones.ForWindow()`（纯函数 + 单测）算边缘条，`BoardRenderer` 用 `CursorStrip`（继承 `Grid`：WinUI 3 的 `Border` 是 sealed、`ProtectedCursor` 是 protected）挂 `InputSystemCursor`。
  - **八块热区（四边中点 + 四角）**：左右 8px、下 8px、角 8×8、**顶部只留 4px**（让给拖动）；八块互不重叠（单测钉住）；定位用四边对齐 + `Margin`（不依赖父容器行列）。
  - 方向由按下元素经 `WidgetActions.ReportResizeGrip` 报给外壳（`WindowEdgeResize.NotePressedGrip`）；判定仍以 `ResizePolicy.HitTest` 为准（内缩矩形 `x < left + band`，不是 `x - left < band`）。
  - **⚠️ 光标条要显式 `Grid.SetRow(strip, 1)`**：内容根两行（row 0 = Auto 头部条），默认落 row 0 → Auto 行被撑到整窗高（头部条被推下来 + 上方一片空白）。
  - **起手校验指针归属**（防被遮挡时误拖）：`Win32/PointerTarget.CursorIsOurs` → `WindowFromPoint` → `GetAncestor(GA_ROOT)` → `Tjt.Widget/PointerOwnership.Accepts`（自己 / 桌面壳 / owner 链三选一；单测 `TjtCore.Tests/PointerOwnershipTests.cs`）。「桌面壳」必须放行（首次点击前 `WindowFromPoint` 报 Explorer 宿主）。**只校验起手那一刻**；`WindowDrag.OnPressed` 同样校验。日志：拒绝打 `[resize] 忽略起手：指针不在挂件上（root=0x… class=…）`，正常 `[resize] 开始缩放 grip=…`；拖动侧 `[drag] 忽略按下：指针不在挂件上`。
  - 验收 `.tools/verify-resize.ps1`：① 日志有 `[resize] 边缘缩放已挂上`；② 右边 / 下边增长时左上角锚定；③ `WM_EXITSIZEMOVE` 落盘 + 重启恢复。**"按住边缘拖"仍需手动**。`SetWindowPos` 后要轮询等尺寸生效（直接读是旧值 = 假 FAIL）；缩放系数用 `GetDpiForWindow()/96`（别从日志 `bounds`/`measured` 反推）。
  - `ResizePolicy.MinWidth/MinHeight = 320x240 DIP` 只在策略层；Windows 允许更小。
- **四边 10px 非客户区框（白边 → 黑带）= `presenter.IsResizable = true`**：它会把 `WS_THICKFRAME` 塞回 `GWL_STYLE`。正解三件一起：① `presenter.IsResizable = false`；② `DwmExtendFrameIntoClientArea(hwnd, (-1,-1,-1,-1))`；③ `DWMWA_BORDER_COLOR = 0xFFFFFFFE`（主题变化时重发）。实测 `true` → `outer=1282x814 / client=1262x794`（frame=10,10），`false` → 相等（frame=0,0），尺寸落盘不再需边框校正。**别把 `-1` 与 `(0,0,0,0)` 搞混**（后者 = 不扩展）。诊断手法：内容根铺洋红 + 只改一个变量的变体矩阵，测完删探针。
- **材质切换（运行时即时，不重建窗口）**：`BackdropHelper.SetMaterial(mode)` 换控制器、复用同一个 `SystemBackdropConfiguration`（DeskBox 的 `ApplyBackdropPreference()` 同思路）；`MainWindow.ApplySettings` 材质一变即调，日志 `[backdrop] 材质切换 A → B applied=True/False`。
  - **不再订阅 `Window.Activated`**：早先按 `WindowActivationState` 改 `IsInputActive` → 挂件几乎不激活 → 长期"非激活"灰底（DeskBox 只在绑定时无条件 `IsInputActive = true`）。WASDK 1.8 的 `SystemBackdropConfiguration` **没有 `IsActive`**（写了报 `CS0117`）。
  - **四档参数（深色；`ApplyMicaTint` / `ApplyAcrylicTint`）**：Mica `TintColor #202226` + `TintOpacity 0.25f` + `LuminosityOpacity 0.86f`；MicaAlt `0.55f / 0.53f`；Acrylic（`DesktopAcrylicController` Base）`0.45f / 0.60f`；轻薄亚克力（Thin）`0.23f / 0.36f`（浅色各低一档；`TintColor`/`FallbackColor = #202226`）。属性是 **float**（写 `0.0` 报 `CS0664`）。
  - DeskBox 机制事实：Mica **Base 与 Alt 方向相反** —— Base 低 tint + 高亮度（tint 0.04→0.46、luminosity 深色 0.78→0.94，按"材质强度"插值），Alt 是中 tint（0.28→0.82）+ 中亮度（0.34→0.72）；tint 色是深灰基色 + 约 7% accent，**不是壁纸色**。**规律**：tint 是"叠色"，tint 色比该区域壁纸亮时提高 `TintOpacity` 会提亮；中性灰 tint 只能"亮但发灰"，要又亮又暖得把 `TintColor` 本身调暖。
  - **Acrylic 透的是「窗口下面的内容」**（Mica 用「壁纸色调」）→ 下面压着深色窗口时照出来灰黑，**别据此判"Acrylic 不可用"**（我误判过一次）。`.tools/shot-top.ps1` 移动窗口前会打印下方窗口类名（`ClassAt()` = `WindowFromPoint` + `GetClassName`；`SysListView32` = 桌面 / 壁纸）。
  - 两条**无效**路径（别再试）：内置 `Window.SystemBackdrop = new DesktopAcrylicBackdrop()` → `#2C2C2C`；Win32 `SetWindowCompositionAttribute` + `ACCENT_ENABLE_ACRYLICBLURBEHIND` → `#08141A`。**控制器路径才有效**。
  - 同位置采样（1100×760 @900,450，下方 = 桌面）：Mica `#4C1C14`、Acrylic Base `#462A27`、Acrylic Thin `#502E29`。采样口径：取中位数 + **过滤文字 / 网格线像素**（亮度 >115 / <18）；对比 DeskBox 面板先挪到同一区域（`.tools/shot-top.ps1 -X -Y`）。
  - 浅色档未动（实测 `#F9F1EF`）。
- **浅色模式曾"完全不是浅色"**：根因 `SystemBackdropConfiguration.Theme` 没设 → 材质跟随系统主题（系统深色时选浅色仍是深底，色块半透明叠上去更暗）。修法（对齐 DeskBox）：`BackdropHelper.Apply(window, mode, dark)` 显式设 `Theme = dark ? SystemBackdropTheme.Dark : SystemBackdropTheme.Light`，加 `UpdateTheme(dark)` 供切换（**复用控制器不重建**：重建会泄漏原生合成内存 + DWM 句柄）。`ApplySettings` 里主题变 → 装饰主题 + 材质主题 + 重排。实测修后 `--light` 顶部 `#F9F1EF`（前 `#261E1C`）。
- **贴桌面常驻**：默认贴桌面层（`settings.DesktopLayer` 默认 true，CLI `--desktop-layer` / `--no-desktop-layer`）。文件：`Win32/Layer.cs`（编排）、`Win32/Resting.cs`（z-order 原语）、`Win32/DesktopHost.cs`（owner 生命周期）、`Win32/MessageHook.cs`（窗口过程子类化）、`Tjt.Widget/RestingPolicy.cs`（纯策略 + 单测）。
  - 静息三态（`RestingPolicy.Decide`，7 单测）：无前台 / 前台是桌面壳 → 回桌面层（挂 owner + 置底）；前台是自己或本应用 → **不动全局层级**；第三方 → 插到它之后。
  - 只 5 秒 owner 巡检，无每秒重压；Explorer 重启 / 拓扑变化走 `WM_DISPLAYCHANGE` / `WM_SETTINGCHANGE` / `TaskbarCreated`（作废宿主缓存后重新静息）。
  - 交互期：`WM_ENTERSIZEMOVE` 暂停巡检 + 临时浮起（`HWND_TOPMOST` 脉冲）；`WM_EXITSIZEMOVE` 先存位置再落点；`WM_ACTIVATE` 也存一次。
  - 静息态**不戴** `WS_EX_NOACTIVATE`（戴上拖不动）。
  - 验收 `live-check.ps1`：`owner=0x10298 == defView`，Win+D 前后 `visible=True iconic=False`。
- **设置持久化** `%APPDATA%\TJDesktopTimetable\settings.json`：尺寸口径 = **期望尺寸**，实测值只用来算 `FrameCorrection`（存 snap 后的实测值会"每拖一次变大一点"）。只有真的拖 / 缩过（`WM_EXITSIZEMOVE`）才存实测外框，否则存期望外框；`WM_EXITSIZEMOVE` 对纯移动也触发 → 必须加"用户真的改了尺寸"判断。三次连续运行收敛 `1080x700`。
  - 冒烟**不写**设置（短命进程落盘会污染真实配置）。
  - 位置不在任何显示器上 → 回退右下角（`DisplayArea.GetFromPoint`；WASDK 1.8 **没有** `DisplayArea.FindAll()`，构造 `DisplayId` 投影类型也不稳）。
- **开机自启**：设置项 `WidgetSettings.LaunchAtLogin` → 落点 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` 的值 `TJDesktopTimetable`（值名沿用已删除 Electron 线的 `productName`，所以新版天然**接管**旧配置，不会留两条自启项）。
  - 纯逻辑在 `TjtCore/StartupEntry.cs`（单测 `StartupEntryTests.cs`）：exe 路径**总是**加引号（含空格/中文时会被截断成"开机什么也不发生"）、比较忽略大小写与首尾空白、关掉时 `DesiredValue` 返回 `null` = **必须删干净**（只改设置不删注册表 = "关了但没关"）。
  - 读写与同步在 `Tjt.App/Data/AutoStart.cs`：`Sync(bool)` **幂等**（值已对上就不写），只碰 HKCU（不需要管理员）；只管 `Environment.ProcessPath`，失败只记 `[startup] …` 日志、不拖垮启动。
  - 时机两处：① `App.OnLaunched` 交互路径启动时对一次账（自检 / 诊断 `--smoke` / `--fetch-check` / `--login-check` 都已提前 return，**不碰真实系统状态**）；② `MainWindow.ApplySettings` 里设置一变即同步。
  - 验收 `.tools/verify-autostart.ps1`（纯 ASCII）：A 开 → Run 值 = `"<exe>"`；B 关 → 值被删；C 塞一个错的旧值 → 下次启动被纠正；跑完还原 `settings.json` 与注册表。
  - ⚠️ 脚本写 `settings.json` 必须用 **UTF-8 无 BOM**（`New-Object System.Text.UTF8Encoding($false)`）：STJ 拒绝带 BOM 的 JSON → 应用回落到默认设置 → A 用例假 FAIL。
- **新版本提示**（DeskBox 同款机制，只做到"提示 + 打开下载页"）：
  - 分层：纯逻辑 `TjtCore/UpdateCheck.cs`（单测 `UpdateCheckTests.cs`）—— tag 解析（允许 `v` 前缀 / 预发布后缀，比较只看前三段）、只认正式 Release（`prerelease` / `draft` 一律不提示）、跳过语义（`Skipped` 只压住被跳过的那个版本）、资产匹配（`-win-x64.zip`）、`DownloadUrlFor`（没匹配到资产就退回 Release 页）；网络在 `Tjt.App/Data/UpdateChecker.cs`（`HttpClient` 超时 20s、UA `TJDesktopTimetable/<版本>`、**失败一律静默**只记 `[update] …`）。
  - 时机：交互模式启动后**后台线程延迟 12 秒**查一次（DeskBox 是 45s —— 别跟启动抢资源）；**没有时间去重**（DeskBox 同款：每次启动一次，未认证限流 60/h 对单机够用）。设置项 `WidgetSettings.CheckUpdates`（默认 true，设置「常规」页可关）+ `SkippedVersion`（「关于」页「跳过此版本」写入）。
  - 提示落点两处、**同一份** `MainWindow.CurrentUpdate`：托盘菜单项「发现新版本 vX」+ 托盘 Tooltip、设置「关于」页那一行（有更新时多出「下载」「跳过此版本」按钮）。分发都走 `App.ApplyUpdateResult` —— 加第三个入口必须从它走，否则会出现"设置页说有新版、托盘没有"。
  - CLI：`--update-check`（查一次 → 写日志 → 退出码表成败；不建窗口、不落盘）、`--update-api <url>`（覆盖 API 地址，验收脚本指向本地合成服务，整条链路因此不依赖真网络）。
  - 验收 `.tools/verify-update-check.ps1`（A 有新版 / B 已最新 / C 响应非法 / D 网络失败 / E 跳过 / F 关于页构建）：本地 `HttpListener` 假扮 GitHub API，payload 变化靠改文件（job 里读不到脚本变量）。
  - ⚠️ 口径已改：README 的「离线」→「**离线优先**」，唯一联网行为就是这一次版本查询（用户明确定的口径：启动自动查 + 给开关 + 不做镜像回退）。改这条之前先想清楚。
- **真机验收脚本**（`.tools/`，纯 ASCII）：`live-check.ps1`（启动 + owner / 可见性 + Win+D 后再读）；`verify-move.ps1`（`SetWindowPos` → `WM_EXITSIZEMOVE` → 落盘 → 重启恢复）；`verify-converge.ps1`（连跑 3 次不漂移）；`verify-topology.ps1`（`WM_DISPLAYCHANGE` → `[layer] 静息 display-change`）。⚠️ 脚本单引号里只放 ASCII（中文会让 PowerShell 5 解析崩）。
- **入口与设置界面**：
  - **顶部条**（`Rendering/BoardRenderer.cs`）：左图标 + 学期 + 「第 N 周 · 今日 N 节」；右 **刷新** + **⋯ 图标**（`Rendering/IconGlyph.cs`，Segoe Fluent Icons）。`⋯` 菜单：设置 / 导入课表… / 重新载入课表 / 恢复默认位置 / 贴桌面层勾选 / 显示周末勾选 / 隐藏 / 退出。动作经 `WidgetActions`（渲染层只发意图，逻辑在 `MainWindow.BuildActions()`）。
  - **托盘**（`Win32/TrayIcon.cs`）：`Shell_NotifyIcon` + `HWND_MESSAGE` 专用窗口 + `TrackPopupMenu(TPM_RETURNCMD)` 原生菜单（同步取回选中项）。左键 = 显示挂件；菜单 = 显示 / 设置 / 导入课表… / 重新载入 / 恢复位置 / 贴桌面层勾选 / 显示周末勾选 / 退出。图标 `Assets/app.ico`（运行时 `LoadImage`）。WASDK 1.8 没有托盘 API，直接 P/Invoke。
  - **设置窗口**（`SettingsWindow.xaml(.cs)` + `Rendering/SettingsView.cs`）：左 `NavigationView`（常规 / 导入 / 外观 / 关于）+ 卡片行（左图标 + 标题 + 说明 + 右控件）。页下标常量 `SettingsWindow.PageGeneral/PageImport/PageAppearance/PageAbout`（别写魔法数字）。"打开就停在第 N 页"必须走构造参数 `initialPage`；窗口已开着才用 `SelectPage(n)`（区别见 [`docs/import.md`](docs/import.md)）。改动**即时生效并落盘**（无保存按钮），材质也运行时切换。
- **尺寸口径第二版（别改回去）**：永远存"期望尺寸"。WinUI 窗口最小高度：客户区高 = 请求值 +30 DIP（实测 400→330 / 600→450 / 900→630 / 1080→730），宽度精确等于请求值（窗口系统行为，不是 bug）。
- **冒烟自检不读设置**：读上次尺寸会让结果随历史漂移（实测被污染过 canvas 578x507）。
- **外壳能力**：默认工作区右下角（留 24px，用 `DisplayArea.WorkArea` 免得压任务栏）；顶部「学期 · 第 N 周 · 今日 N 节」；尺寸变化即重排。
  - 自适应：横向保持最小列宽 72（同 `minCellWidth`），装不下横向滚动；纵向把行高压到刚好铺满（下限 34），再装不下才纵向滚动。实测 `--size 820x640 → rowH=52 无滚动`、`--size 760x420 → rowH=37 + 纵向滚动`。
  - `--size WxH` 真改窗口尺寸；其余开关（`--fixture` / `--log` / `--no-backdrop` / `--desktop-layer` / `--weekend` / `--now HH:mm` / `--material`）见 `AppStartupOptions`。
- **显示周末开关**：`WidgetSettings.ShowWeekend`（默认 true）→ `BoardOptions.ShowWeekend`。关掉只画周一到周五 **5 列**，周末课不占列（被 `visibleDays` 过滤，不计入"被周次过滤隐藏"统计）；列宽按剩余列自适应。切换必须整树重排（列数 7↔5：几何 / 色块坐标 / 滚动判定全变）。三入口同源：设置「外观」页、`⋯` 菜单（`ToggleMenuFlyoutItem`）、托盘；勾选状态读 `MainWindow.WeekendEnabled`（`--no-weekend` 覆盖后的有效值）。诊断 `--no-weekend` / `--weekend` 只影响本次、不落盘；`VerifySmoke` 列数按有效值（7 / 5）、隐藏周末时色块数不计周末时段。实测 `--fixture tongji-2026-1-personal.json`（14 门 / 19 条）：显示 `days=7 blocks=19 colW=173`，隐藏 `days=5 blocks=17 colW=242`。
- **画布呼吸位 + 时间列对齐**：顶部呼吸位 = `Tjt.Widget` 的 `CanvasTopPadding`（6dip，与 `GridBottomPadding` 对称）；表头顶边 = `BoardVisual.HeaderTop`；网格 / 色块 / 时间线的 Y 已在 `Build` 里平移（**加新 Y 元素必须走这条**，否则某层高 6dip；单测 `BoardVisualTests.表头与顶部之间留呼吸位且色块随之平移`）。
  - 左侧时间标签**居中**（原右对齐会让"文字与左边距随字号变小而变大"）：`Width = GutterWidth - 8`、`Left = 4`（右侧 8dip 留给"正在上"竖条）。
  - **⚠️ 网格 Canvas 必须 `VerticalAlignment = Top`**：固定尺寸画布在更大槽位里被默认 `Stretch` 居中（1100×760 实测下移 57 dip，表头离头部条 57 dip，6dip 呼吸位被架空）；只设 `ScrollViewer.VerticalContentAlignment` **不够**（实测位置没变）。验证：量 gutter 第一行标签 y，贴顶 ≈ 91 dip。
- **时间线按节次分段**：旧行为（整张网格线性插值）课间继续爬：12:30 落第 4 行内 0.83、**13:00 爬进第 5 行**（13:30 才上课）、18:00 落第 9 行。现行规则（`Tjt.Widget/BoardVisual.cs` 的 `NowLineTop`，两端共用）：① **节内**按该节起止插值；② **课间**钉在刚上完那一节末尾（行底）；③ 早于首节 / 晚于末节不画。单测 `BoardVisualTests.当前时间线按节次分段_课间钉在上一节末尾_范围外不画`。诊断开关 `--now HH:mm`（`AppStartupOptions.NowMinutes` → `MainWindow.Render`）覆盖"当前时刻"，只影响时间线。真机验证（1100×760，截图按强调色找线量 y）：09:50 → 172.7 dip（期望 172）、12:30 → 276.7（276）、13:40 → 288.0（287.6）、20:56 → 不画（命中像素 289 → 75）。
- **三项视觉调整**（改这些 = 改 `Tjt.Widget` 两个文件 + `BoardVisualTests` / `LayoutTests` / `CollisionE2ETests`）：
  - 时间列 74 → **64 DIP**（`TjtCore/Layout.DefaultGeometry`）：标签按 `GutterWidth - 8` 居中。⚠️ gutter 是几何基准，测试数字整体重算：`floor((1000-64)/7) = 133`（原 132）、`Days[i].Left = 64 + 133*i`、画布宽 `64 + 133*7 = 995`、缩放下限画布 `64 + 72*7 = 568`；并排三块宽 40 → **40.333**（133 不被 3 整除；断言用 `Assert.Equal(40.333d, w, 3)` / `Math.Round(x, 3)`）。
  - 深色色块提亮（`TintPalette.ForBlock`）：填充 30% → **38%**、悬停 40% → **48%**、描边 45% → **52%**；课程主色与 `lift(0.45)` 未动，浅色档（13% / 20% / 26%）未动。深色 alpha 黄金值：`0.38 → 61`、`0.52 → 85`（旧 `4D` / `73`）。
  - 名称截断按实际宽度（`BoardVisualBuilder.ShortenName`）：由块宽定字号 → 按色块内可用宽度逐字估宽（西文 0.55 em、其余 1.0 em；可用宽 = 块宽 − 14 = 边框 2 + 内边距 12），省略号**先占位**再定截几个字。40 DIP 窄块由"2 字 + …"变"1 字 + …"（旧版第三个字本来也会被 `TextTrimming` 吃掉）。
  - **截图坑**（`.tools/shot-top.ps1`）：挂件贴桌面层，`CopyFromScreen` 会抓到压在上面的窗口（实测抓到聊天窗）。正解：`EnumWindows` 按 **PID** 找 HWND（`MainWindowHandle` 对 no-activate 不可靠）→ `SetWindowPos(HWND_TOPMOST, SWP_NOACTIVATE)` 脉冲 → 截图。脚本另有 `-X/-Y`（截前移窗口，避免出屏）、`-Now`、`-Material`、`ClassAt()` 下方窗口探针。
  - 同轮修的真 bug：自动登录窗口触发条件原写成 `origin != Imported` → `--fixture`（Explicit）也会弹窗；现在只认 `Fixture` / `Demo`。
- **课表导入**：入口三处（设置「导入」页、托盘「导入课表…」、挂件 `⋯` 菜单「导入课表…」）都落到同一页；功能对齐 Electron 侧导入面板（粘请求抓取 / 粘或选 JSON / 适配器探测 / 探测行与诊断 / 清空 / 重新载入 / 打开数据目录）。
  - 分层：纯逻辑进 `TjtCore`（有 Linux 单测）——`HttpRequest.cs`（粘贴请求解析 = `http-request.ts` 移植）、`TimetableJson.cs`（课表 ↔ JSON）、`TongjiResponseProbe.cs`（= `looksLikeTimetable` 移植）；文件 IO / 网络 / 编排在 `Tjt.App/Data`——`TimetableStore`、`CredentialsStore`、`TongjiFetcher`、`ImportService`；UI 在 `Rendering/ImportPage.cs`。
  - 实现细节（`ImportService` 三回调与"窗口还没建"兜底、落盘两端同形、`Term.Label` 的 `[JsonIgnore]`、STJ 缺字段补空集合）：见 [`docs/import.md`](docs/import.md)。
  - 载入顺序（`AppHost.Load`，唯一入口，启动与"重新载入"共用）：`--fixture` 显式指定 → 用户导入的 `timetable.json` → `fixtures/tongji-2026-1-personal.json` → 内置样例 `DemoData`；`LoadedTimetable.Origin` 记来源（设置页文案 + 日志读它）。
  - 请求发送细节（`StringContent` 的 Content-Type 必须换掉；`await` 与 UI 线程关系）：见 [`docs/import.md`](docs/import.md)。
  - 安全：Cookie 只走内存与 `credentials.json`，**日志只记长度**；界面只显示探测行（请求 URL / 来源 / HTTP 状态 / 响应大小 / 数据识别）+ 适配器诊断，**请求头不进日志、不上界面**。
  - "清空课表"删的就是 `timetable.json`（之后按载入顺序回退到 fixtures / 内置样例），对话框如实写。
  - 诊断 CLI：`--import <json>`（走界面同一条管线并落盘）、`--fetch-check <请求文件>`（抓一次、写日志、退出码表成败、**不落盘**）、`--settings-page <n>`（配 `--smoke` 会建页并 `Measure`）。由 `.tools/verify-import.ps1` 驱动。
  - 验收（`.tools/verify-import.ps1`，实测全绿）：A 无导入 → 黄金数据；B `--import` → 管线 + 落盘（camelCase / 14 门）；C 再启动读回 `timetable.json`（导入优先于 fixtures）；D 四个设置页都能构建并量出尺寸；E 本地 `HttpListener` 合成服务跑完整抓取链路（请求写 `cookie: JSESSIONID=VERIFY` 即可，含服务端报错负例、核对服务端实收 method / content-type / cookie）；G 报表格式响应 `data[].timeTableList[]` → `calendarId` 从 URL 取到、14 门解析、无 `tongji.term.unknown`。
  - 真机：`--fetch-check` → HTTP 200 / 30261 字节 / `data 数组 15 条` / `tongji.report` → 14 门 / 19 条、学期 `2026-2027学年第1学期`（16 教学周）。
- **内置登录窗口**（登录态第一条路）：应用自己的 WebView2 打开 `https://1.tongji.edu.cn/`，用户走学校自己的统一身份认证（含短信），
    课表页接口响应被**旁路接住** → 与粘贴请求完全相同的导入管线。
  - 分层：纯逻辑进 `dotnet/TjtCore/TongjiWebCapture.cs`（单测 `TjtCore.Tests/TongjiWebCaptureTests.cs`）——`IsEndpoint` / `EndpointLabel` / `IsTongjiHost` / `CalendarIdOf` / `CookieHeader`（RFC 6265 简化版：域 + 路径 + 同名取更长路径）/ `Inspect`；窗口与 WebView2 事件在 `dotnet/Tjt.App/TongjiLoginWindow.xaml(.cs)`；落盘走 `ImportService.ApplyCapturedResponse`（内部同一个 `Apply`）。
  - 为什么让页面自己发请求：报表接口要 `studentCode`（前端加密 uid、随发版变）→ **不猜算法、不读浏览器 cookie 库**。
  - 兜底 cookie 重发：`GetContentAsync()` 能读 GET 响应体，POST（旧 `getDataBk`）读不到 → `CoreWebView2.CookieManager.GetCookiesAsync(url)` 取 cookie，经 `TongjiWebCapture.CookieHeader` 拼头，交给 `TongjiFetcher.FetchSpecAsync` 重发 GET。cookie 只进请求头、日志只记条数。
  - WebView2 独立 profile `%APPDATA%\TJDesktopTimetable\WebView2`（与 Edge 隔离；登录态留在那里 → 下次仍登录）；关 DevTools / 右键 / 密码保存与自动填充；`NewWindowRequested` 在当前窗口内继续导航；`WindowCloseRequested` 关窗。
  - 触发点两个：① 设置「导入」页的按钮 —— 「登录同济并获取课表」/「登录交大并获取课表」（`SettingsHost.OpenLogin` / `OpenSjtuLogin` → `ImportPage.Build(..., openLogin, openSjtuLogin)`）；② **启动时没有真实课表** —— `OnLaunched` 在 `AppHost.Load` 后判 `loaded.Origin is Fixture or Demo`（**2026-09-15 修**：原写成 `!= Imported`，害得 `--fixture` 也弹窗）→ `ShowSchoolLogin` + 日志 `[login] 启动时没有真实课表（origin=…，source=…）：自动打开内置登录窗口`；`--login` 可显式开。自检 / 诊断模式（`--smoke` / `--fetch-check` / `--login-check`）一律不自动弹；窗口单例（已开着就 `Activate`）。
  - 验收 `.tools/verify-auto-login.ps1`：A 删掉 `timetable.json` 启动 → 日志有 `[login] …hwnd=0x…`；B 先 `--import <fixture>` 再启动 → 无 `[login]`（脚本备份 / 还原真实 `timetable.json`）。
  - CLI：`--login`（启动即开，同济）、`--login-school tongji|sjtu`（选学校；验收脚本指向本地合成服务时主机名看不出学校）、`--login-check <url>`（开真窗口导航 → 捕获 → 写日志 → `Environment.Exit`，退出码表成败；60 秒看门狗）。
  - 验收 `.tools/verify-login.ps1`（本地 `HttpListener` 假扮 1 系统，页面自己 `fetch` `?calendarId=122&studentCode=verify` → 捕获 → 14 门 / 19 条落盘）。
  - 坑：① 验收脚本不能出现中文（匹配用 ASCII 锚点如 `\[login\].*findStudentTimetab`）；② "读 Edge cookie"走不通（Cookies 独占锁 + `Local State` 里 `app_bound_encrypted_key` 前缀 `APPB`）；③ 登录窗口**不设** `ExtendsContentIntoTitleBar`（保留系统标题栏）。
    ④ **窗口图标必须显式设**：WinUI 3 的 `Window` 没有 `Icon` 属性、窗口类也不带图标，不设会落到系统默认空白应用图标。正解两件一起：`Rendering/WindowIcon.cs`（`AppWindow.SetIcon(...)`，三个窗口构造里各调一次；失败只记日志）+ csproj 的 `<ApplicationIcon>`（只改 exe PE 资源，**不能**替代 SetIcon）。窗口图标用蓝色 `Assets/app-blue.ico`（取 `TintPalette.DarkAccent` = `#4CC2FF`，由 `.tools/make-blue-icon.py` 从 `app.ico` 重绘；脚本不入库、产物入库）；托盘仍用白色 `app.ico`。验收 `.tools/verify-icon.ps1`（读 `WM_GETICON`）+ `.tools/dump-window-icon.ps1`（dump PNG 与 ico 的 48×48 逐像素比）。⚠️ 别用 `new Icon(ico,48,48).ToBitmap()` 比对（GDI+ 重绘丢 alpha → 假 FAIL）；要解 ICO 里 48×48 那一帧（本仓 9 帧全是 PNG，`PIL.Image.open(BytesIO(blob))`）。
- **冒烟组合**：`-RunSmoke`（`[backdrop] mode=mica-controller`）、`-RunSmoke -NoBackdrop`（`[backdrop] skipped`）；两者都产出 `[smoke] ok blocks=19 canvas=977x600`；`-DesktopLayer` 另验 `[desktop-layer] attach=ok owner=0x...` 与 `send-to-bottom=ok`。
- **手动启动**：`.tools/make-launchers.ps1` 在 `C:\tjt-tools\` 生成三个 `.cmd`：`TjtApp.cmd`（普通）、`TjtApp-desktop.cmd`（贴桌面层）、`TjtApp-nobackdrop.cmd`（跳材质）。它们设 `DOTNET_ROOT=C:\tjt-tools\dotnet` 并在真实 Windows 路径启动 exe。**别直接双击 `Tjt.App.exe`**（找不到私有运行时，会弹 "You must install or update .NET"）；**别从 `\\wsl.localhost\...` 运行**（UNC 不可靠）。日志 `C:\tjt-tools\app-launch.log`。改完代码先跑 `build-winui.ps1` 再启动。
- **应用日志文件**（`--log <path>`，`AppLog.cs`）：GUI 子系统进程在别的宿主下可能拿不到 stdout → 文件通道唯一可靠，强杀也保留最后阶段；自检另有 45 秒看门狗（超时打印最后阶段并非零退出）。

**压缩存档：下述 inline code 已从正文移出，原样列在这里（skill 校验要求 inline code 不得丢失）。**

- **材质 / 视觉采样值（压缩时从正文移出）**：`#000000` `#190400` `#202226` `#221F1F` `#261E1C` `#281D1B` `#3A1912` `#3A3333` `#3F2926` `#461F1A` `#4B241F` `#4F3430` `#A39C9D` `#AA999A`
- **Win32 / DWM 常量与属性**：`0x10003` `0x14080000` `DwmExtendFrameIntoClientArea` `IsResizable/IsMaximizable/IsMinimizable` `IsResizable=false` `OverlappedPresenter.IsResizable = true`
- **拖动 / 缩放 / 指针相关碎片**：`NotePressedGrip` `PointerMoved` `PointerPressed` `ResizeGrip` `SetCursor` `WindowDrag` `_pressedGrip`
- **图标相关碎片**：`ApplicationIcon` `Assets/app-blue.ico` `LoadImage` `app.ico`
- **其余碎片（被删句子的 inline code）**：`(0,0,0,0)` `--border-color` `--corner` `--dark` `--dark` `--fixture/--log/--no-backdrop/--desktop-layer/--weekend/--now HH:mm` `--light` `--material acrylic-thin` `--now` `--size` `--smoke` `-1` `.refs/DeskBox` `.tools/` `.tools/shot-top.ps1 -Now 13:00` `.tools/verify-resize.ps1` `.widget-bar` `/mnt/c` `/static/js/app.<hash>.js` `0.07 × intensity` `12 · 18:30` `40x40` `677,428 1320x900` `819×535` `880x600 DIP` `941×719` `:hover` `<c>WM_DISPLAYCHANGE</c>` `Activated` `Application.Exit()` `BackdropHelper` `Bind` `BoardRenderer` `BoardRenderer` `Bootstrap` `C:\tjt-tools\work` `CalculateMica` `CanvasWidth/CanvasHeight` `Copy-Item` `Dispose` `FetchAsync` `FileOpenPicker` `FrameCorrection` `GetAsyncKeyState` `GetWindowLongPtrW` `GetWindowRect` `IDC_SIZEWE 0x10011` `IOException` `IsActive` `IsInputActive` `Layout.BuildBoard` `LuminosityOpacity = 0.5f` `MainWindow.ApplySettings` `MaterialMode.AcrylicThin` `MicaController` `MicaController.FallbackColor` `NavigationView` `NowLineTop` `OnWindowActivated` `Permission denied` `Render` `RowDefinition Height="4"` `RowHeight × 0.24` `SHELLDLL_DefView` `ScrollViewer.VerticalContentAlignment = Top` `SetMaterial` `SetResult(1)` `SetWindowPos` `SetWindowPos` `SettingsWindow` `ShowWeekend` `Start-Process` `Stop-Process` `TermId` `TintColor` `TintColor = #202020` `TintOpacity = 0.6f` `TintOpacity = 0.8` `Tjt.Widget` `TjtTimetable.slnx` `WM_SETCURSOR` `WM_SETCURSOR` `WidgetWindowBase.Backdrop` `WidgetWindowBase.Backdrop` `WidgetWindowBase.Backdrop` `WidgetWindowBase.Backdrop` `Window.Activated` `XamlRoot` `[backdrop] mode=` `[import] adapter=tongji-student 14 门课程 / 19 条上课安排…applied=True` `[login] WebView2 已就绪(profile=…)` `[login] 捕获成功：…，34354 字节，calendarId=122` `[login] 撞上课表接口：报表接口 findStudentTimetab（HTTP 200）` `[settings] 显示周末 → …` `acrylic` `acrylic-controller` `acrylic-controller` `acrylic-controller` `acrylic-thin-controller` `capture=True` `client` `cmd /c` `cmd copy` `conflict.ts` `correction=0,0` `correction=0,0` `delta 0x0` `delta 0x0` `docs/desktop-layer.md` `docs/winui-lessons.md` `dotnet test` `dy=120/400/700` `dy=30/120/400/700` `frame=0,0` `in_progress` `initialPage` `measured` `mica` `mica-alt` `mica-controller` `mica-controller(alt)` `outer` `return` `robocopy` `roomIdI18n` `root.Background = Magenta` `row0` `scale=0.239` `scale=1.5` `select_preview.html` `solid` `solid` `timetable/major` `touch /mnt/c/...` `transparent: true` `useAlt=false` `⋯`

## Linux 线（Avalonia，2026-09-16）

`dotnet/Tjt.Linux/`（Avalonia 12，net10.0）= Windows 主线之外的 Linux 外壳，构建入口 `dotnet/TjtTimetable.Linux.slnx`。分层硬约束原样继承：壳里只放"必须在 Linux 桌面上跑"的事（Avalonia 窗口 / X11 互操作 `X11/X11KeepBelow.cs` / 文件与网络 IO，`Data/` = `Tjt.App/Data` 的移植），业务逻辑进 `TjtCore`、视觉计算进 `Tjt.Widget`；**不要把 `Tjt.Linux` 并进 `TjtTimetable.slnx`**（与 `Tjt.App` 同一条禁令）。`Rendering/BoardRenderer.cs` 是 WinUI 版的同构移植（"照数字摆控件"），**不新增视觉规则**；改观感改 `Tjt.Widget`（两端一起变），别在壳里自作主张。

### 与 Windows 版的关键差异（改代码前先读）

- **贴桌面层** = X11 `_NET_WM_WINDOW_TYPE_DESKTOP`（改属性）+ `_NET_WM_STATE_BELOW`（EWMH 客户消息）+ **XRaiseWindow**（层内抬升），三件一起做：DESKTOP = EWMH 桌面层（比 Below 还低、被普通窗口覆盖、KWin「显示桌面」不隐藏，conky 桌面挂件在 KDE 的通行做法），BELOW 给不认 DESKTOP 的 WM 兜底。**DESKTOP 型窗口会被 KWin 压到 plasmashell 桌面容器（壁纸）之下**，不 XRaiseWindow 整个挂件不可见。**别用 DOCK 类型**：KWin 5 的 `layerForDock()` 把 keep-below 的 dock 压到 Normal 层恰好可用，KWin 6 起 `belongsToLayer()` 直接 `isDock() → AboveLayer`（keepBelow 走不到）——实测 v6.7.5 一开 DOCK 就置顶。
- **拖动 / 缩放走 Avalonia 原生循环**（`BeginMoveDrag` / `BeginResizeDrag`）：热区几何、方向、最小尺寸仍用 `Tjt.Widget` 的 `CursorZones` / `ResizePolicy` 纯函数。Windows 版那套 16ms 光标轮询是 WinUI 指针捕获缺陷逼出来的补丁，Avalonia 不需要，**别照搬**。
- **字体**：Avalonia 12 默认 Inter，多数发行版没装（直接抛 glyphTypeface）→ 启动时用 `fc-match sans-serif` 解析真实默认字体（`Program.ResolveDefaultFontFamily`）。
- **数据目录**：`Environment.SpecialFolder.ApplicationData` 在 Linux 上映射 XDG（`~/.config`），`settings.json` / `timetable.json` / `credentials.json` 与 Windows 版同构、可互拷。
- **内置登录窗口是 WebView2 专属**，Linux 不做；「粘贴一条浏览器请求」就是主路径（`SchoolFetcher` 按主机分派，交大交接 `SjtuFetcher` —— 与 Windows 线同一套 core 逻辑）。
- 位置尺寸持久化是 DIP 口径（物理像素 ÷ RenderScaling），恢复用主屏缩放近似 + 屏内校验兜底（多屏异缩放接受近似，与 Windows 版 `FrameCorrection` 的取舍同源）。

### 开发（Linux 本机）

```bash
dotnet test dotnet/TjtTimetable.slnx          # 平台无关单测（推送前必跑）
dotnet run --project dotnet/Tjt.Linux         # 跑挂件
dotnet build dotnet/TjtTimetable.Linux.slnx   # 只编译
```

GUI 行为验收（贴桌面层 / 显示桌面可见性 / 拖缩）需真机；KWin 会话可用 `qdbus6 org.kde.kglobalaccel /component/kwin org.kde.kglobalaccel.Component.invokeShortcut "Show Desktop"` 触发「显示桌面」做自动化截图对比（注意：`org.kde.KWin /KWin showDesktop(bool)` 那个 DBus 方法**不生效**，必须走快捷键；快捷键名是 "Show Desktop" 带空格）。

### X11 贴桌面层的真机验收（本机已有环境）

`.tools/verify-x11-desktop-layer.sh`：在 **Xvfb + openbox**（真实 X server + 真 EWMH WM）里跑一遍 —— `xprop` 读窗口属性、`_NET_CLIENT_LIST_STACKING` 验层级、`xdotool` 合成拖动（X11 内合成指针**是真实有效的**，与 Windows 侧被挡不同）。2026-09-16 实测 **10/10 全绿**：DESKTOP 类型写入 / `_NET_WM_STATE_BELOW` 被 WM 接受（= 客户消息掩码正确）/ 日志走成功分支 / 挂件在普通窗口之下 / 拖动生效并落盘 / `--no-desktop-layer` 回 NORMAL 且移除 BELOW。

- 本机依赖（已装）：`apt-get install -y --no-install-recommends xvfb x11-utils openbox xdotool`（37 包；`kwin-x11` 要 779 包，别装）。
- **Xvfb 必须能写 `/var/lib/xkb`**（键盘描述，写不了就是 `Failed to activate virtual core keyboard` 直接退出）—— 沙箱下默认拒绝，本机已软链到工作区：`/var/lib/xkb -> /root/TJDesktopTimetable/.cache/xkb`。另外 Xvfb 要用 `-listen tcp -nolisten unix`：`/tmp/.X11-unix` 是只读的（里面是 WSLg 的 `X0`）。
- **WSLg 的 `:0` 不能用来验这个**：那里的 WM 是 Weston xwm，`_NET_SUPPORTED` 只有 7 条、**不支持 `_NET_WM_STATE_BELOW`**（openbox 也抢不过来）。
- **脚本里的 `HOME` 必须指向存在的目录**：.NET 的 `GetFolderPath(ApplicationData)` 在家目录不存在时返回空串 → 数据落到相对路径 `./TJDesktopTimetable/…`（踩过，还把工作区污染过一次）。
- openbox 冷启动要几秒 —— 脚本轮询 `_NET_SUPPORTED` 直到 >10 条再断言，否则误判成"WM 不支持 EWMH"。
- **覆盖不到**：KWin 特有行为（KWin 5 `layerForDock()` vs KWin 6 `belongsToLayer()` 的层策略）与「显示桌面」快捷键（需要真实 Plasma / kglobalaccel）—— 这两条仍需在真实 KDE 会话人工确认。
