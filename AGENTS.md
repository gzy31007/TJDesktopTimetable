# AGENTS.md · TJDesktopTimetable

> 系统环境、WSL 网络与代理、工具链、字体等**通用**信息见全局 `~/.dsh/AGENTS.md`，本文件只写本项目相关内容。

## 项目定位

Windows 桌面小组件：把同济大学课表以半透明色块网格固定在桌面上。核心诉求是**架构分明、可扩展**——数据源（学校适配器）与窗口/渲染层彻底解耦，新增一所学校只需加一个适配器文件。

- 本地目录：`/root/TJDesktopTimetable`（仓库根）
- 远程仓库：https://github.com/gzy31007/TJDesktopTimetable （Public / MIT）
- 技术栈：**WinUI 3（C#，`dotnet/`）是唯一开发主线**；`TjtCore` 平台无关（Linux 可测）、`Tjt.Widget` 纯计算、`Tjt.App` 是 WinUI 外壳，Win32 直接 P/Invoke
- **状态（2026-09-16，v1.0.0 已发布）**：Electron 线（`apps/desktop/` + `packages/core/`，含 TS 工具链与它的 CI）
  **已于 2026-09-16 删除**（先冻结、后按用户决定砍掉）：黄金 fixture 搬到 `dotnet/fixtures/`，
  其余历史见 git（`git log --diff-filter=D -- '*apps/desktop*' '*packages/*'`）。
  **仓库里只有 `dotnet/` 一条线**，新功能一律进它。发版：打了 `v*` tag 就走
  `.github/workflows/release-winui.yml`（自包含 publish → zip → 挂到 Release），
  版本号只在 `dotnet/Directory.Build.props` 改一处。

## 工作目录约定

```
dotnet/TjtCore/       平台无关核心（net10.0）：模型/周次/冲突/布局/时间/适配器
dotnet/Tjt.Widget/    挂件视觉层（net10.0，纯计算，不引 WinUI/Win32）
dotnet/Tjt.App/       WinUI 外壳（net10.0-windows）：窗口/材质/Win32/托盘/设置
dotnet/TjtCore.Tests/ 平台无关单测（TjtCore + Tjt.Widget 都测这里）
dotnet/fixtures/      脱敏后的真实抓包数据（黄金测试基准，禁止放入学号、姓名等个人信息）
docs/                 desktop-layer（层级层结论）· winui-build（DeskBox 参考事实 + 构建环境）
                      · winui-lessons（返工史）· import（课表导入细节）· deskbox-refactor-assessment
.tools/               本机构建/验收脚本（**gitignore**，不入库）：build-winui.ps1 / test-dotnet.ps1 / verify-*.ps1
```

分层硬约束（改代码时必须遵守）：

1. `TjtCore` / `Tjt.Widget` **不得**引用 WinUI / Win32 / `Tjt.App` —— 它们必须是 `net10.0` 平台无关，
   在 Linux（CI 的 ubuntu job）上就能 `dotnet test`。需要 P/Invoke 的东西一律下沉不了，留在 `Tjt.App`。
2. `Tjt.App` 只做"必须在 Windows 上跑"的事：窗口、材质、Win32、托盘、设置界面与文件/网络 IO；
   业务逻辑一律进 `TjtCore`，视觉计算一律进 `Tjt.Widget`。
3. 新逻辑优先写进 `Tjt.Widget`（能在本机快速单测），只有"必须真实窗口/句柄"的才落 `Tjt.App`。
4. `dotnet/fixtures/` 是黄金数据的**唯一真源**（csproj 用 `Content Link` 复制到测试输出目录），
   改 fixture 等于改验收基准 —— 必须两边（`Tjt.App` 的 `--fixture`、`TjtCore.Tests`）都能跑通。

## 任务规范

- 提交：Conventional Commits（英文类型 + 中文简述），例：`fix(widget): 遮挡时不再误起缩放`；推送前必须跑测试：
  `dotnet test dotnet/TjtTimetable.slnx`（Linux/CI 原生命令）或本机 `.tools/test-dotnet.ps1`（见下节）。
- 测试：`TjtCore` / `Tjt.Widget` 的每个公开函数都要有单测；同济个人课表适配器由
  `TjtCore.Tests/E2ETimetableTests.cs` 端到端覆盖（导入 → 布局 → 时间 → 单双周过滤）；
  **同格撞车**由构造 fixture `dotnet/fixtures/tongji-2026-1-collision.json`（非抓包）覆盖，
  `CollisionE2ETests.cs` 钉住；挂件视觉由 `BoardVisualTests.cs` 钉住。
- 代理：只有推送到 GitHub / 拉 GitHub 资源时才用代理：
  `export https_proxy=http://127.0.0.1:7897 http_proxy=http://127.0.0.1:7897`。
  本机 NuGet 走 `.tools/nuget`（WSL）/ `C:\tjt-tools\nuget`（Windows 工作区）缓存，不需要额外镜像配置。

## 易错知识点

- **已移除**「专业培养计划（`timetable/major`）适配器」与「教学班勾选」流程：现在只支持个人课表导入即用。若用户误把培养计划数据导进来（同一门课多个教学班），会表现为导入结果异常（**没有** `tongji.looksLikePlan` 这类专门警告——2026-09-15 全仓 grep 确认该标识在 TS/C# 源码里都不存在，旧文档此条不准；适配器层里真正的诊断码是 `tongji.personal` / `tongji.report` / `tongji.flat` / `tongji.noSchedule` / `tongji.schedule.missing` / `tongji.term.unknown` / `tongji.term.startDate` / `tongji.summary`）。
- **同济个人课表有两条接口、两种"包法"（2026-09-16 补第二条）**：以 1 系统前端 bundle（`/static/js/app.<hash>.js` + 懒加载 chunk）为准：
  - 选课服务 `POST /api/electionservice/student/{选课批次id}/getDataBk` → `data.selectedCourses[].course.times[]`（旧路径，`{id}` 无法稳定构造）；
  - **课表页真正调的那条**：`GET /api/electionservice/reportManagement/findStudentTimetab?calendarId=<学期id>&studentCode=<前端加密的uid>`（研究生 `findSchoolTimetab2`，按前端源码走 `data.list`）→ `data[].timeTableList[]`。
    两者 `dayOfWeek`/`timeStart`/`timeEnd`/`weeks` 数组语义**完全一致**，只是课程在数组顶层、排课数组改名、教室多一层 `roomLable`（线上课堂/操场这类没有教室编号的场地，`roomIdI18n` 为空时才用它）。
  - **`calendarId` 只在请求 URL 上**（报表响应体里没有），所以抓取时要从粘贴的请求里把它取出来当 `ImportInput.TermId`，否则学期退化成"未知"（`tongji.term.unknown`）：C# 侧 `HttpRequestParser.QueryValue(spec, "calendarId")` → `TongjiFetchOutcome.TermId` → `ImportService`。
  - 两条接口对同一个人给出的课表**逐条一致**（实测：14 门 / 19 条，条数一致是"同格多教师合并"后的结果，原始 `timeTableList` 有 27 条）。fixture：`dotnet/fixtures/tongji-2026-1-report.json`（脱敏：教师姓名→教师A…Z、工号→10001+、教学班 id→9xxxxxxxxxxxxxxx）。
- 个人课表与培养计划是**同一套后端字段**（`dayOfWeek` / `weekState` / `timeStart` / `roomName` …），区别只在数据范围，所以字段映射逻辑可复用。
- `weekState` 是 16 位周次掩码，bit0 = 第 1 周；单双周掩码不要硬编码 `0x5555/0xAAAA`（只对 16 周成立），按 `term.totalWeeks` 生成。
- `dayOfWeek` 取值 1–7，**7 = 周日**（注意与 JS `Date.getDay()` 的 0=周日 区分）。
- **并排分组键是「同天 + 同起止节次」，不是"时间段相交"**：周一 1-2 节与周一 1-3 节算两格，各自独占整列（视觉上互相压住）。布局不做跨块几何排布，这是与 `select_preview.html` 一致的既有语义。
- **并排 ≠ 冲突**：单双周错开的两门课（如 1-8 周 / 9-16 周）在 `conflict.ts` 里不算冲突，但布局照样把同一格的两块并排（`colCount = 2`）。改布局时别把 `colCount` 和 `coursesConflict` 混为一谈；两端都有黄金用例钉住（`fixtures/tongji-2026-1-collision.json`）。
- **同格多条 times 的合并键含教室**（适配器层：同天 + 同起止节次 + 同教室才合并、周次取并集）；同格不同教室不合并，成为并排的两块。
- 教学班去重键用 `teachingClassId`（数字），`code` 是教学班代码字符串（如 `00213702`），`courseCode` 是课程代码（如 `002137`），三者不可混用。
- 校历时间戳是毫秒（如 `beginDay: 1820160000000`），且 `weekBenginDay` 表示"周从周几开始"（同济为 2 = 周一），不是开学日。
- **层级层的历史结论与证据**（`SHELLDLL_DefView`、静息落点三态、5 秒巡检、拖动/缩放真机结论）：见 [`docs/desktop-layer.md`](docs/desktop-layer.md)。**现行实现在 C# 侧**（见下节「贴桌面常驻」）。
- Windows 侧排查可用 WSL interop 直接调 `cmd.exe` / `powershell.exe`，但**参数里的引号与反斜杠会被 interop 再处理一次**：把逻辑写进 `.ps1`/`.bat` 再执行，不要在 `cmd /c` 里堆嵌套引号（`tasklist /FI "IMAGENAME eq x"` 这种就会解析失败）。`.ps1` 用 Windows PowerShell 5 执行时按 ANSI 读取，**脚本内容必须是纯 ASCII**（含中文注释会因引号配对错乱而解析失败）。
- **改用户 `settings.json` 之前必须先停掉应用**：运行中的实例会在 `moved`/`resized` 等时机 `saveSettings()` 回写，而 `Stop-Process` 是强杀、退出路径不保证执行 —— 先改文件再杀进程，改动会被旧实例的内存值覆盖（实测："恢复挂件位置"这一步就这么白做了一次，`941×719` 被写回成 `819×535`）。正确顺序：**stop → 改 → start**（停用 `.tools/stop.ps1`；启动走 `C:\tjt-tools\TjtApp*.cmd`，见下文"手动启动"）。
- **合成鼠标输入的三个坑**（本机验收反复踩到，用 `.tools/` 下的脚本时注意）：
  1. `SetCursorPos` 只挪光标、**不产生鼠标输入消息** —— 所以它测不出 hover（`:hover` 不触发）、也测不出点击；要真实输入得用 `mouse_event(MOUSEEVENTF_MOVE|MOUSEEVENTF_ABSOLUTE, x, y, ...)`，坐标是按主屏归一化到 0..65535 的（`x = 虚拟x * 65535 / 虚拟宽`）。
  2. **PowerShell 不是 DPI 感知进程**：`GetWindowRect` / `SetCursorPos` 用的是虚拟坐标，而应用日志里的 `rect` 是物理坐标（本机 2560×1600 / 缩放 150% → 虚拟 1707×1067，差 1.5 倍）；混用会得到"窗口没动/hover 没反应"的假结论。`CopyFromScreen` 反而吃物理坐标。
  3. 截图别赌坐标：直接全屏 `CopyFromScreen(0,0,2560,1600)`，再按日志里的物理 rect 裁剪 —— 局部截图一旦坐标偏一点，就会截到别的窗口而误判。

## 注意事项

- 仓库 Public：fixtures 与文档中不得出现学号、姓名、cookie、token 等任何个人凭据。
- **登录态有两条路，都不是"破解浏览器数据"**（2026-09-16 更新；旧口径"不做 1 系统自动登录、只手动粘 Cookie"已作废）：
  ① **推荐**：内置登录窗口（`TongjiLoginWindow` + WebView2，见下节「内置登录窗口」）—— 用户在学校自己的页面上登录，
  课表页那条接口的响应被我们**旁路接住**；② 粘贴一条浏览器请求（`Data/TongjiFetcher.cs`），Cookie 存 `credentials.json`。
  **不做**"读 Edge/Chrome 的 Cookies 库"：实测 Edge 153 的 Cookies 被独占锁（Edge 运行时连复制都失败）且启用了
  App-Bound 加密（`Local State` 里 `app_bound_encrypted_key` 前缀 `APPB`），要解 v20 就得调它的 IElevator COM ——
  那是绕过浏览器安全机制，收益也不如方案 ①。**任何日志都不得打印 Cookie 内容**（只记长度/条数）。
- 窗口默认「桌面层 + 静息」：Owner 设为桌面图标视图 `SHELLDLL_DefView`（Win+D 后仍可见），静息落点按前台窗口三选一（机制与证据见 [`docs/desktop-layer.md`](docs/desktop-layer.md)；现行实现见下节「贴桌面常驻」）；`wallpaper`（WorkerW 子窗口）与纯置底作为可切换/回退模式保留，切换失败必须自动回退，不能黑屏。
- 拖动与缩放都是自实现（`Win32/WindowDrag.cs` / `Win32/WindowEdgeResize.cs`），**起手先校验指针归属**（`PointerTarget`）；静息态**不戴** `WS_EX_NOACTIVATE`（戴了拖不动，见"易错知识点"），交互期只做"临时浮起 + 结束后重新落点"。

## C# / WinUI 线（2026-09-15 起）

> **常驻规则（2026-09-15 用户明确定下）：今后前端一律以 DeskBox 为参照实现。**
> 遇到"观感/交互与 DeskBox 不一致"时，默认动作是**去 `.refs/DeskBox` 找它怎么做的**，
> 而不是自己从 Win32/WinUI 文档推。本轮四次返工（材质、白边→黑带、边缘缩放失效、缩放光标）
> 全都是"自己想当然"导致的，而四次的答案都写在 DeskBox 里。
> **边界仍是许可**：`.refs/DeskBox` 是 **GPL-3.0-only** 只读副本，只能提取
> "用了哪些 API / 什么机制"这类**事实**，不得抄代码、注释、文档正文、美术资源
> （依据 `docs/deskbox-refactor-assessment.md`）。实践口径：**先查它、后自己写**。

技术栈迁移**已完成**（2026-09-16 v1.0.0）：Electron 线已删除，WinUI 线是唯一实现（见"项目定位"）。
当初转过来的原因是材质：Electron 的三条系统材质路径实测全拿不到 DeskBox 那种质感（DWM 对"从未被激活的窗口"一律降级成近黑平色），只有 WinUI 的 `MicaController` + `SystemBackdropConfiguration`（可强制 `IsInputActive`）能拿到。

> **读代码时注意「溯源注释」**：`dotnet/` 里大量 `TS 侧 …` / `Electron 侧 …` 的注释是**移植溯源**
> （说明这段 C# 是从哪份已删除的 TS/Electron 实现搬过来的），不是"还有另一条线要同步"。
> 遇到 `packages/core/src/*.ts`、`apps/desktop/**` 这类路径，去 git 历史里找
> （`git show <删除前的 commit>:<路径>`），别在仓库里找 —— 它们已经不存在了。

### 三个工程的分工（改动时必须守住）

| 位置 | TFM | 能跑在哪 | 职责 |
|---|---|---|---|
| `dotnet/TjtCore` | `net10.0` | Linux + Windows | 模型 / 周次 / 冲突 / 布局 / 时间 / 适配器（溯源：已删除的 TS 核心库） |
| `dotnet/Tjt.Widget` | `net10.0` | Linux + Windows | 挂件视觉层：几何、色块染色、名称分档、呈现模型（**纯计算，不得引用 WinUI/Win32**） |
| `dotnet/Tjt.App` | `net10.0-windows10.0.22621.0` | **只能 Windows** | WinUI 外壳：窗口、材质、Win32、照坐标摆控件 |

- `dotnet/TjtTimetable.slnx` = 前两个（Linux 也要能 `dotnet test`）；`dotnet/TjtTimetable.Windows.slnx` = 第三个。
  **不要**把 `Tjt.App` 并进前者，否则 Linux/CI 的构建整片失败。
- 新逻辑优先下沉到 `Tjt.Widget`：那里能在 WSL 上编译 + 单测，反馈最快；外壳里只留"必须在 Windows 上跑"的东西。
- 视觉规则的**唯一真源就是 `Tjt.Widget`**：几何 / 字号 / 名称分档在 `BoardVisual.cs`，染色在 `TintPalette.cs`
  （规则溯源自 Electron 渲染层的 `blockRect` / `blockFontSize` / `blockName` / `tintStyle`，该文件已随 Electron 线删除）；
  期望值由 `TjtCore.Tests/BoardVisualTests.cs` 钉住 —— **改视觉 = 改这两个文件 + 这份测试**，不必再同步别处。
  仓外的 `select_preview.html` 只在需要对照历史观感时看一眼。

### 在 Windows 本机构建（WSL 侧编译不了 WinUI）

```bash
PS=/mnt/c/Windows/System32/WindowsPowerShell/v1.0/powershell.exe
$PS -NoProfile -ExecutionPolicy Bypass -File '\\wsl.localhost\Ubuntu-24.04\root\TJDesktopTimetable\.tools\build-winui.ps1' -RunSmoke
```

- **带开关时必须用 `-File` 这种形式**：把脚本 base64 成 `-EncodedCommand "$B64"` 之后**再跟 `-RunSmoke`**，
  会被 PowerShell 当成非法参数、直接打印用法提示就退出（实测踩过，白跑一轮）；
  `-EncodedCommand` 只适合"不带开关的构建"。
- 脚本把源码 robocopy 到 `C:\tjt-tools\work`（**反向拉取**：Windows 侧读 `\\wsl.localhost\...`，不是 WSL 写 `/mnt/c`），
  再用 `C:\tjt-tools\dotnet\dotnet.exe`（.NET 10 SDK，装在工作区里，不污染系统）构建；加 `-RunSmoke` 还会跑冒烟自检。
  日志在 `C:\tjt-tools\build.log`。
- **WSL 沙箱把 `/mnt/c` 挂成只读**：`touch /mnt/c/...` 会 `Permission denied`，robocopy 到 `/mnt/c` 也失败。
  一切对 Windows 盘的写操作都要走 PowerShell（interop 或 `-EncodedCommand`），别从 WSL 直接写。
- **跑平台无关单测**（`TjtTimetable.slnx` = TjtCore + Tjt.Widget + TjtCore.Tests；WSL 里**没有** dotnet SDK，
  只有 Windows 侧那份工作区 SDK）：`$PS -NoProfile -ExecutionPolicy Bypass -File '\\wsl.localhost\...\.tools\test-dotnet.ps1'`。
  它复用 `build-winui.ps1` 镜像出来的 `C:\tjt-tools\work`，所以**先构建、后测试**（否则测的是上一版源码）。
  实测 203 项全绿。
- 脚本内容**必须是纯 ASCII**（Windows PowerShell 5 按 ANSI 读，中文注释会让解析错乱）；所有输出重定向到文件读，
  因为 interop 下 stderr 会被序列化成 CLIXML 没法看。
- **GUI 进程不会自动退出**：冒烟自检一度把 CI job 挂成无限 `in_progress`（`Application.Exit()` 在"窗口从未激活"的路径上
  不推进消息循环）。现在 `--smoke` 做完校验直接 `Environment.Exit(code)`，CI 与本机脚本都有 `WaitForExit` 超时兜底。
- WSL 侧可以用 `EnableWindowsTargeting=true`（已写进 `Tjt.App.csproj`）做**编译级**检查（C# 类型/签名错误一次暴露），
  但构建必然停在 XAML：XAML 编译器依赖 Windows 原生 `GenXbf.dll`（报 `WMC0621`）。
- 本机装的是 **Visual Studio Community 2026**（`D:\Program Files\Microsoft Visual Studio\18\Community`）+
  Windows SDK `10.0.26100`；VS 只带 .NET **运行时**，不带 SDK —— 所以 `C:\tjt-tools\dotnet` 是必须的。
- **Windows App Runtime**：`Tjt.App` 引用 WASDK `1.8.260804001`，`Bootstrap` 需要 1.8 运行时。
  本机原先只有 1.5 / 1.7，**已在 2026-09-15 装上 1.8**（`windowsappruntimeinstall-x64.exe --quiet`），
  所以本机可以正常跑 GUI 与冒烟。
- **CI 上不跑运行时冒烟（重要结论，别再试）**：GitHub 的 windows runner 上 `Tjt.App.exe`
  表现为"进程挂着 + **一行输出都没有**"——连写在 App 构造函数第一行的文件日志（`--log`）都没落盘，
  说明卡点在 **WASDK 引导路径内部**，与我们的代码、材质、GPU 都无关（装过 1.8 运行时、加过
  `--no-backdrop`、换过日志通道，三次实验都停在同一处）。
  所以 CI 的 `winui-shell` job 只做**编译门禁**（真实 Windows SDK + XAML 编译器），
  运行时验证一律走本机 `.tools/build-winui.ps1 -RunSmoke`。
- **DeskBox 参考事实**（它的窗口 chrome / 拖动 / 缩放 / 托盘用了哪些 API 与机制；只记事实、不抄代码）与**本机构建环境清单**（VS / Windows SDK / WASDK / CI 编译门禁）：见 [`docs/winui-build.md`](docs/winui-build.md)。
- **拖动是自实现的，且合成输入验不了（2026-09-15 实测结论）**：三条路都试过并记录在案：
  1. **原生 move loop 不生效** —— `ReleaseCapture()` + `SendMessage(WM_NCLBUTTONDOWN, HTCAPTION)`
     窗口纹丝不动（与 Electron 侧当年同一条结论）；
  2. **XAML `CapturePointer` 也不行** —— 按下时 `capture=True`，紧接着就收到 `PointerCaptureLost`
     （Windows 向失去捕获的窗口发消息 → 元素被重建 → 捕获作废），`PointerMoved` 一条都没有；
  3. 最终走 **窗口级 `SetCapture` + `DispatcherTimer`(16ms) 轮询光标**：算 `初始位置 + 光标位移`，
     `SetWindowPos` 搬窗口；左键松开（`GetAsyncKeyState`）即收尾 → 存位置 + 重新落点。
     DeskBox 也是自实现（它连 `WS_THICKFRAME` 都清了），只是它挂在 XAML 指针事件上。
- **这台机器挡掉了 WSL 发起的合成指针输入**：`SetCursorPos` 与绝对坐标 `mouse_event` 都**不动光标**
  （实测光标始终停在原点），所以"拖动"这一条**只能手动验收** —— 别再用合成鼠标去验拖动，
  否则结论会是假的 FAIL（试过三轮，全是环境问题不是代码问题）。可验的是：按下事件到达
  （日志 `[drag] 按下`）、左键状态可读（`lbutton=True`）、以及渲染层 `[drag] poll#` 心跳。
- **缩放（2026-09-15 二次定稿：原生循环 → 自实现）**：
  - **为什么不能用系统原生缩放循环**（第一版失败史）：见 [`docs/winui-lessons.md`](docs/winui-lessons.md)。**结论：缩放必须自实现。**
  - **现在与 DeskBox 同构：缩放自实现**（`Win32/WindowEdgeResize.cs`，16ms 轮询光标）：
    <list type="bullet">
    <item><description>光标进边缘抓取带 → `SetCursor` 显示对应缩放光标（`IDC_SIZEWE/NS/NWSE/NESW`）；</description></item>
    <item><description>左键按下且落在带内 → 记起点与起始外框，开始拖拽；</description></item>
    <item><description>每帧用 `Tjt.Widget/ResizePolicy.Resolve`（纯函数）算新外框 → 一次 `SetWindowPos`；</description></item>
    <item><description>左键松开（`GetAsyncKeyState`）→ 存位置 + 重新落点 + 光标还原。</description></item>
    </list>
    与拖动同一套轮询机制（`WindowDrag` 是同一个模式），所以"这台机器挡掉合成指针输入"这条限制同样适用。
  - **光标最后改走渲染层（Win32 那条路两轮都没成）**：先是只在 16ms 轮询里 `SetCursor`
    → 真机"能缩放但看不到缩放光标"；再改成 `WM_SETCURSOR` 里设 + `SetResult(1)` → 用户仍看不到。
    实测证据：`SetCursor` **返回成功**（旧光标从箭头 `0x10003` 变成 `IDC_SIZEWE 0x10011`）、
    `WM_SETCURSOR` 也确实到了窗口过程（计数从 0 涨到 2），但用户看不到 —— 而
    `GetCursor`/`GetCursorInfo` **从别的进程读不到当前光标**（前者只报本线程拥有的，
    后者在这个宿主里恒为 0），所以"到底谁赢了"没法用程序判定。
    **结论：不要在挂件上跟系统光标缠斗**，改用渲染层光标 ——
    `Tjt.Widget/CursorZones.ForWindow()` 算出四条边缘条（纯函数、有单测），
    `BoardRenderer` 用 `CursorStrip`（继承 `Grid`，因为 WinUI 3 的 `Border` 是 **sealed**
    而 `ProtectedCursor` 是 **protected**）挂 `InputSystemCursor`。光标由框架按元素算，
    不参与"谁最后 SetCursor"的竞争。Win32 侧只留判定与搬窗口。
  - **热区几何对齐 DeskBox（2026-09-15 三次修正）**：原先只有"左右 + 上下"四条带，
    结果真机反馈**"没有边角的缩放"** —— 四角被其中一条带盖住，按下去只能单轴缩放。
    现在照 DeskBox 的 3×3 网格改成**八块热区（四边中点 + 四角）**：
    左右带 8px、下方带 8px、四角 8×8，但**顶部只留 4px**（那 4px 让给顶部条拖动 ——
    DeskBox 的 `RowDefinition Height="4"` 就是这个意思）。
    - 八块**互不重叠**（有单测钉住）：角被边盖住就退化成单轴，这是本轮的正因。
    - 定位改用**四边对齐 + `Margin`**，不用父容器的行列 —— 从根上避免"元素落错行把布局撑坏"。
  - **方向由按下元素直接报告**：热区在 `PointerPressed` 里把 `ResizeGrip` 通过
    `WidgetActions.ReportResizeGrip` 报给外壳（`WindowEdgeResize.NotePressedGrip`），
    起拖时优先用它。理由：**元素自己知道"按在角上"**，而外壳靠坐标反推在角上要靠分支勉强对；
    而且用户"移到边上立刻按下"时轮询还没更新，报告能补齐这一拍（顺带解决"不灵敏"的一半）。
  - 判定仍以 `ResizePolicy.HitTest` 为准（内缩矩形：`x < left + band`，不是 `x - left < band`，
    后者会把窗口左侧外面整片桌面算成抓取带 —— 有单测钉住），报告只用于"按下那一刻的方向"。
  - **⚠️ 光标条必须显式 `Grid.SetRow(strip, 1)`**：`BoardRenderer` 的内容根是两行 Grid
    （row 0 = Auto 头部条，row 1 = Star 滚动区），子元素**默认落在 row 0**。
    把"底部对齐的下光标条"放进 Auto 行，Auto 行为了容纳它会被迫长到整窗高
    （真机逐子元素探针实测：`row0` 长到 606，Star 行只剩 138）——表现为**头部条被推到下方、
    上面一大片空白**。这是本轮"布局忽然坏了"的真凶，靠"去掉条对比截图"+"逐子元素打印几何"
    两步定位。教训：往有两行的 Grid 里加覆盖元素，先想清楚它落在哪一行。
  - **起手必须校验「指针归谁」（2026-09-16 修：被遮挡时误拖）** —— 用户报的
    「课表被其他窗口遮挡时，拖动仍然生效」。**根因**：抓取带判定只用「光标坐标 + 左键状态」，
    这个判据**看不见窗口上面压着谁** —— 挂件被别的窗口遮挡时，用户在那个窗口上按住左键拖动，
    光标扫过挂件边缘带 → 起手条件成立 → 挂件跟着一起缩放。
    **修法（照 DeskBox 的判据）**：起手前走 `Win32/PointerTarget.CursorIsOurs` ——
    光标下窗口 → `WindowFromPoint` → `GetAncestor(GA_ROOT)`，再交给纯策略
    `Tjt.Widget/PointerOwnership.Accepts`（**自己 / 桌面壳 / owner 链**三选一接受，
    其余一律拒绝；单测 `TjtCore.Tests/PointerOwnershipTests.cs`）。
    `WindowDrag.OnPressed` 也加了同一条校验（防御性：指针事件本不该在被遮挡时到达）。
    - **「桌面壳」那条不能少**：DeskBox 的实测结论是"首次点击之前 `WindowFromPoint` 会报成
      Explorer 桌面宿主，而不是 no-activate 的 WinUI HWND"，不放行就会从"遮挡时误拖"变成
      "边缘彻底拖不动"（另一个极端）。
    - **只校验起手那一刻**，拖动/缩放进行中不校验：那时挂件是临时 topmost 且持有鼠标捕获，
      光标本来就会移出窗口，再校验会把正常交互打断。
    - 顺带修掉两个同源缺陷：① **左键没按下时作废 `_pressedGrip`** —— 否则"在边缘点一下"的
      残留方向会在下一次"按在窗口中间"时被当成边缘方向，凭空起缩放；② 起手分支原来的提前
      `return` 只看"抓取边是否变化"，使 `NotePressedGrip`（渲染层报来的按下方向）**形同虚设** ——
      "先移到边缘、再按下"根本起不了手，现在把报告纳入条件。
    - 排查用日志：被拒绝时打一行 `[resize] 忽略起手：指针不在挂件上（root=0x… class=…）`，
      正常起手仍是 `[resize] 开始缩放 grip=…`；拖动侧对应 `[drag] 忽略按下：指针不在挂件上`。
  - **验收状态（2026-09-15 四次定稿，全绿）**：自实现缩放 + 渲染层光标 + **四角斜向缩放**
    均已真机确认（用户语："太完美了"）。**"顶部 4px 让给拖动"这条取舍也随之消失** ——
    顶带从 6px 收到 4px 后，贴着最上沿照样能拖窗口。
    回归时按三步走，别一上来改代码 ——
    ① `.tools/verify-resize.ps1`（几何/落盘/恢复）；② 真机试**四角**与四条边；
    ③ 版式不对时先怀疑"新加的元素落在了哪一行"（本轮就是这么坏的）。
  - **验收**（`.tools/verify-resize.ps1`，**不用合成鼠标**）：验三件能自动化的 ——
    ① 日志里有 `[resize] 边缘缩放已挂上`（说明自实现循环挂上了）；
    ② 几何：右边/下边增长时左上角锚定不动（`SetWindowPos` + 轮询等待尺寸生效）；
    ③ `WM_EXITSIZEMOVE` 落盘 + 重启恢复。**"按住边缘拖"本身仍需手动**。
    真机实测（150% 缩放）：`scale=1.5`、落盘 `880x600 DIP`（`correction=0,0`、`delta 0x0`）、
    重启恢复 `677,428 1320x900` 与预期完全一致。
  - **脚本踩过的坑**：`SetWindowPos` 之后应用是**下一个消息循环才应用尺寸**的，
    直接读 `GetWindowRect` 会读到旧值（表现为假 FAIL）—— 必须轮询等待；
    缩放系数要用 `GetDpiForWindow()/96`，**不要**从日志里的 `bounds`/`measured` 反推
    （那条 `measured` 可能是"上一次"事件的，会算出 `scale=0.239` 这种鬼值）。
  - **`ResizePolicy.MinWidth/MinHeight = 320x240 DIP` 只在策略层生效**：
    真机实测把窗口强行设到 `40x40` 物理像素时 Windows 仍会接受（没有夹到 320x240 DIP），
    因为窗口系统自己有更小的下限。别把它当成系统的硬约束。
- **挂件四边那 10px 非客户区框（白边 → 黑带）——根因是 `presenter.IsResizable = true`（2026-09-15 结案）**：
  - **白边/黑带的症状与「铺洋红」探针定位手法**：见 [`docs/winui-lessons.md`](docs/winui-lessons.md)。
  - **根因**：`OverlappedPresenter.IsResizable = true` 会让 WinUI 把 **`WS_THICKFRAME` 塞回
    `GWL_STYLE`**（我们手动清过也没用，presenter 之后又加回去），而这个样式位正是那 10px
    非客户区框的来源。真机实测同一版代码只改这一个值：
    - `true` → `outer=1282x814 / client=1262x794`（frame=10,10，四边一圈框）；
    - `false` → **`outer` 与 `client` 完全相等（frame=0,0）**，内容直接压到窗口边缘。
  - **正解 = 三件一起**（DeskBox 的取法，实测它的窗口就是 `frame=0,0`、style `0x14080000`）：
    1. `presenter.IsResizable = false`（它 `IsResizable/IsMaximizable/IsMinimizable` 全 false）；
    2. `DwmExtendFrameIntoClientArea(hwnd, (-1,-1,-1,-1))` —— "sheet of glass"；
    3. `DWMWA_BORDER_COLOR = 0xFFFFFFFE` —— 透明描边（它自己用 XAML 画边框）。
    三者都在主题变化时重发（DeskBox 是在 `WidgetWindowBase.Backdrop` 里按主题签名重发）。
  - **缩放怎么办**：`IsResizable=false` 之后原生缩放循环起不来（真机验收过），
    所以缩放改成了自实现 —— 见上面"缩放（2026-09-15 二次定稿）"条目。
    附带收获：尺寸落盘**不再需要边框校正**（`correction=0,0`，`delta 0x0`），因为客户区就是外框。
  - **⚠️ 别把 `DwmExtendFrameIntoClientArea` 的 `-1` 和 `(0,0,0,0)` 搞混**：`(0,0,0,0)` 是
    "**不扩展**"，等于什么都没做（曾经因此误判"这个 API 没用"）。`-1` 才是扩展。
  - **诊断手法（值得复用）**：给内容根铺一层洋红（`root.Background = Magenta`）跑一次，
    就能一刀切开"这层像素是内容画的还是窗口装饰画的"；再配合"只改一个变量"的变体矩阵
    （`--border-color` / `--corner` 这类临时 CLI），两次测量就能锁定根因，
    比读文档猜快得多。**测完把探针和诊断开关删掉**。
- **材质切换（2026-09-15 完成：运行时即时生效，不重建窗口）**：
  - 界面上的「窗口材质」下拉**不再写"重启后生效"**：`BackdropHelper.SetMaterial(mode)` 换控制器、
    复用同一个 `SystemBackdropConfiguration`（与 DeskBox 的 `ApplyBackdropPreference()` 同思路 ——
    它在 `WidgetWindowBase.Backdrop` 里就是运行时按材质签名重绑控制器）。
    `MainWindow.ApplySettings` 里材质一变就调它，并打
    `[backdrop] 材质切换 A → B applied=True/False`。
  - **`Dispose()` 必须解绑 `Activated`**：`SetMaterial` 每次 `Bind` 都会挂一次
    `OnWindowActivated`，不解绑会随切换次数累积。
  - **Acrylic 的固有代价仍在**：它要求窗口 `transparent: true`，我们按 mica/solid 创建（非透明），
    所以运行时切到 Acrylic 拿到的是 `acrylic-controller` 但糊感可能不如专用透明窗口。
    `SetMaterial` 会按实际结果返回 true/false 并记日志，**不假装成功**。
  - **真机实测四种材质各不一样**（同尺寸同位置采样 `dy=120/400/700`）：
    `solid` 中心 `#000000`（实色底）、`mica` `#281D1B`、`mica-alt` `#190400`（比 mica 更深、
    分层更明显 —— 这是 BaseAlt 的预期观感）、`acrylic` `#3A1912`（透出更多壁纸）。
    日志里 `[backdrop] mode=` 分别对应 `solid` / `mica-controller` / `mica-controller(alt)` / `acrylic-controller`。
- **浅色模式"完全不是浅色"——材质主题没接主题（2026-09-15 结案）**：
  - **症状（真机采样）**：`--dark` 与 `--light` 两种模式的**底色像素一模一样**（顶部都是 `#261E1C`），
    更怪的是浅色模式的色块比深色的**更暗**。
  - **根因**：`SystemBackdropConfiguration.Theme` 我们从来没设过 → 材质跟随**系统**主题。
    系统是深色时，哪怕用户在设置/CLI 里选浅色，mica 仍是深底；而色块是**半透明**的
    （浅色 13%、深色 30%），浅色低透明度叠在深底上 → 透不过来、反而更暗。
  - **修法（对齐 DeskBox）**：`BackdropHelper.Apply(window, mode, dark)` 显式设
    `Theme = dark ? SystemBackdropTheme.Dark : SystemBackdropTheme.Light`
    （DeskBox 在 `WidgetWindowBase.Backdrop` 里就是这么做的），
    并加 `UpdateTheme(dark)` 供主题切换时更新 —— **复用控制器不重建**
    （DeskBox 注释：重建系统材质控制器会泄漏原生合成内存与 DWM 句柄，GC 都收不回）。
    `ApplySettings` 里主题一变就同时更新三件：装饰主题、材质主题、重排。
  - **实测**：修后 `--light` 顶部底色 `#F9F1EF`（修前 `#261E1C`），色块变成粉彩；
    `--dark` 不变。**验证方式**：同尺寸、同位置各截一张，比对 `dy=30/120/400/700` 的像素 ——
    两种模式必须明显不同（这条比肉眼可靠）。
- **贴桌面常驻（2026-09-15 完成）**：默认就是贴桌面层（`settings.DesktopLayer` 默认 true，
  CLI 用 `--desktop-layer` / `--no-desktop-layer` 覆盖）。移植自 Electron 那套已验收的编排，
  文件与职责一一对应：`Win32/Layer.cs`（编排）、`Win32/Resting.cs`（z-order 原语）、
  `Win32/DesktopHost.cs`（owner 生命周期）、`Win32/MessageHook.cs`（窗口过程子类化）、
  `Tjt.Widget/RestingPolicy.cs`（**纯策略 + 单测**）。
  - **静息落点三态**（`RestingPolicy.Decide`，7 条单测）：无前台/前台是桌面壳 → 回桌面层（挂 owner + 置底）；
    前台是自己或本应用 → **不动全局层级**；前台是第三方 → 插到它之后。
  - **只有 5 秒 owner 巡检，没有每秒重压**：owner 正常时只做一次 `GetWindowLongPtrW` 读、不产生 z-order 变化。
    Explorer 重启 / 显示拓扑变化走 `<c>WM_DISPLAYCHANGE</c>` / `WM_SETTINGCHANGE` / `TaskbarCreated`
    的事件通道（作废宿主缓存后重新静息）。
  - **交互期**：`WM_ENTERSIZEMOVE` → 暂停巡检 + 临时浮起（`HWND_TOPMOST` 脉冲）；
    `WM_EXITSIZEMOVE` → 先存位置再落点。`WM_ACTIVATE` 也顺手存一次位置。
  - **静息态不戴 `WS_EX_NOACTIVATE`**（戴上就拖不动，Electron 侧的硬结论，这里同样适用）。
  - 真机验收（`live-check.ps1`）：`owner=0x10298 == defView`，Win+D 前后都
    `visible=True iconic=False`，前台切到桌面壳 → 挂件既没被隐藏也没被最小化。
- **设置持久化**：`%APPDATA%\TJDesktopTimetable\settings.json`（与 Electron 侧 userData 同名目录）。
  **尺寸口径是"外框 + 实测边框校正"**：Windows 会把窗口 snap 到最小尺寸，若把 snap 后的尺寸
  当"用户尺寸"存回去，下次恢复会更大一点 —— 实测每跑一次长 30 DIP（正反馈）。
  现在只有用户真的拖过/缩放过（`WM_EXITSIZEMOVE`）才存实测外框，否则存**期望外框**并把
  "外框 - 客户区"记成 `FrameCorrection` 供下次恢复换算。三次连续运行实测收敛在 `1080x700`。
  - 冒烟自检**不写**设置（短命进程落盘只会污染真实配置）。
  - 保存的位置若不在任何显示器上（拔了外接屏），回退右下角 —— 用 `DisplayArea.GetFromPoint`
    判定：WASDK 1.8 **没有** `DisplayArea.FindAll()`，构造 `DisplayId` 的投影类型也不稳。
  - **只有"用户真的改了尺寸"才存实测尺寸**：`WM_EXITSIZEMOVE` 对**纯移动**也会触发，
    若不加这层判断，一次拖动就会把 snap 出来的尺寸写成用户尺寸，又回到正反馈（实测如此）。
- **真机验收脚本**（都在 `.tools/`，纯 ASCII）：
  - `live-check.ps1`：启动 + 读 owner/可见性 + 按 Win+D 再读一遍；
  - `verify-move.ps1`：`SetWindowPos` 移动窗口 → 发 `WM_EXITSIZEMOVE` → 检查落盘坐标 → 重启检查恢复；
  - `verify-converge.ps1`：连续跑 3 次，确认尺寸不漂移（实测稳定在 1080x700）；
  - `verify-topology.ps1`：发 `WM_DISPLAYCHANGE` → 检查 `[layer] 静息 display-change` 是否出现（即事件通道生效）。
  - ⚠️ 这些脚本**曾经踩坑**：把中文写进单引号字符串会让 Windows PowerShell 5 解析直接崩
    （按 ANSI 读，引号配对错乱）—— 单引号里只放 ASCII。
- **入口与设置界面（2026-09-15）**：WinUI 侧现在有两个入口，都是照 DeskBox / 资源管理器的语言做的。
  - **挂件顶部条**（`Rendering/BoardRenderer.cs`）：左边应用图标 + 学期 + 「第 N 周 · 今日 N 节」，
    右边 **刷新** 与 **⋯ 溢出菜单**（设置 / 导入课表… / 重新载入课表 / 恢复默认位置 / 贴桌面层勾选 / 隐藏 / 退出）。
    图标用 Segoe Fluent Icons 字形（`Rendering/IconGlyph.cs`），跟主题色走、任意 DPI 都清晰。
    动作经 `WidgetActions`（渲染层只发意图，逻辑留在 `MainWindow.BuildActions()`）。
  - **托盘图标**（`Win32/TrayIcon.cs`）：`Shell_NotifyIcon` + 消息专用窗口（`HWND_MESSAGE`，离屏、
    不参与层级）+ 原生右键菜单（`TrackPopupMenu(TPM_RETURNCMD)` 同步取回选中项，不需要消息分发）。
    左键单击 = 显示挂件；菜单 = 显示 / 设置 / 导入课表… / 重新载入 / 恢复位置 / 贴桌面层勾选 / 退出。
    WASDK 1.8 没有托盘 API，所以直接 P/Invoke；图标是 `Assets/app.ico`（运行时 `LoadImage` 加载）。
  - **设置窗口**（`SettingsWindow.xaml(.cs)` + `Rendering/SettingsView.cs`）：左侧 `NavigationView`
    导航（常规 / 导入 / 外观 / 关于）+ 卡片行（左图标、标题、说明，右控件），照 DeskBox 那套。
    页下标是常量 `SettingsWindow.PageGeneral/PageImport/PageAppearance/PageAbout`（别写魔法数字）。
    外部要"打开就停在第 N 页"必须走构造参数 `initialPage`；窗口已经开着才用 `SelectPage(n)`
    （两者的区别见下面"课表导入"里的实测坑）。
    改动**即时生效并落盘**（没有保存按钮）；**材质也是运行时即时切换**
    （换控制器不重建窗口，见上面"材质切换"条目）—— 界面文案里已经没有"重启后生效"这种说法了，
    若见到就是旧文档/旧产物。
- **尺寸口径的第二版（重要，别再改回去）**：保存的**永远是"期望尺寸"**，实测尺寸只用来算
  `FrameCorrection`。之前两版都踩了同一个坑——把 Windows snap 后的实测尺寸当期望值存回去，
  于是"用户每拖一次窗口就变大一点"（实测 552x497 就是这么来的）。用户拖过之后，
  用"实测 - 校正"**反推**出他想要的尺寸再存。
  - WinUI 窗口有**最小高度**：客户区高度恒等于请求值 +30 DIP（实测 400→330 / 600→450 / 900→630 /
    1080→730），宽度则精确等于请求值。这是窗口系统行为，不是 bug。
- **冒烟自检不读设置**：它验证的是"默认状态下的渲染与层级"，读到上次运行的窗口尺寸会让结果
  随历史漂移（实测被污染过一次：canvas 578x507）。
- **外壳已经有的能力（2026-09-15）**：默认摆在**工作区右下角**（留 24px，用 `DisplayArea.WorkArea`
  而不是屏幕尺寸，免得压到任务栏）；顶部信息条显示「学期 · 第 N 周 · 今日 N 节」（对应渲染层
  `.widget-bar` 的三段）；窗口尺寸变化即重排。
  - 自适应规则：**横向**保持最小列宽 72（沿用渲染层 `minCellWidth`），装不下就横向滚动；
    **纵向**把行高压到刚好铺满（下限 34），再装不下才纵向滚动 —— 也就是缩窗口先压行高、再出滚动条，
    不会把格子压成一条缝。实测：`--size 820x640 → rowH=52 无滚动`、`--size 760x420 → rowH=37 + 纵向滚动`。
  - `--size WxH` 会真的改窗口尺寸（专门为验证自适应加的）；`--fixture/--log/--no-backdrop/--desktop-layer`
    见 `AppStartupOptions`。
- **画布呼吸位与左侧时间列的对齐（2026-09-16，观感修复）** —— 用户反馈两条：
  "高度减小时第一列时间与窗口的边距过大"、"周一到周日那一行与上面的边距过小"。
  - **顶部呼吸位 = `Tjt.Widget` 的 `CanvasTopPadding`（6dip）**，与底部的 `GridBottomPadding` 对称；
    表头顶边由 `BoardVisual.HeaderTop` 表达，网格 / 色块 / 时间线的 Y **都已在 `Build` 里平移过**，
    渲染层照坐标摆即可。**加任何新的 Y 元素都要走这条口径**，否则会出现"某一层比别人高 6dip"的错位
    （单测 `BoardVisualTests.表头与顶部之间留呼吸位且色块随之平移` 钉住）。
  - **左侧时间标签改成居中**（原来是右对齐）：行高被压小时字号跟着变小（`RowHeight × 0.24`），
    右对齐会让"文字与窗口左边缘的空白"随字号变小而**变大** —— 这正是用户看到的现象。
    现在 `Width = GutterWidth - 8`、`Left = 4`（左右对称居中，右侧 8dip 留给"正在上"的竖条标记）。
  - 教训：这类"边距/留白"问题的根因经常是**对齐方式 × 字号自适应**的组合，而不是列宽本身；
    只调列宽（gutter）会改掉几何基准、牵动全部视觉断言，代价大且治不了根。
- **课表导入（2026-09-16 完成：真实课表导入搬到了 WinUI 线）**：入口三处（设置窗口「导入」页、托盘「导入课表…」、
  挂件 `⋯` 菜单「导入课表…」），三处都落到同一页。功能与 Electron 侧导入面板对齐：粘贴浏览器请求抓取、
  粘贴/选择本地 JSON、选适配器、显示探测行与适配器诊断、清空/重新载入/打开数据目录。
  - **分层**：纯逻辑进 `TjtCore`（有 Linux 单测）——`HttpRequest.cs`（粘贴请求解析 = `http-request.ts` 的移植）、
    `TimetableJson.cs`（课表 ↔ JSON）、`TongjiResponseProbe.cs`（响应像不像课表 = `looksLikeTimetable` 的移植）；
    文件 IO / 网络 / 编排留在 `Tjt.App/Data`——`TimetableStore`、`CredentialsStore`、`TongjiFetcher`、`ImportService`；
    UI 在 `Rendering/ImportPage.cs`（`SettingsWindow` 只把这一页塞进 `NavigationView`）。
  - **实现细节**（`ImportService` 三回调与「窗口还没建」兜底、落盘两端同形、`Term.Label` 的 `[JsonIgnore]`、STJ 缺字段补空集合）：见 [`docs/import.md`](docs/import.md)。
  - **载入顺序（`AppHost.Load`，唯一入口，启动与"重新载入"共用）**：`--fixture` 显式指定 → 用户导入的
    `timetable.json` → `fixtures/tongji-2026-1-personal.json` → 内置样例 `DemoData`。
    `LoadedTimetable.Origin` 记来源，设置页文案与日志读它。**"重新载入"就是再走一遍这个顺序**，
    所以"导入过之后按刷新会不会退回样例"这类问题只有一处答案。
  - **请求发送细节**（`StringContent` 的 Content-Type 必须换掉；`await` 与 UI 线程的关系）：见 [`docs/import.md`](docs/import.md)。
  - **抓取的安全口径**：Cookie 只走内存与 `credentials.json`，**日志只记长度**；显示给用户的是探测行
    （请求 URL / 来源 / HTTP 状态 / 响应大小 / 数据识别）与适配器诊断，**请求头一律不进日志、不上界面**。
  - **"清空课表"删的就是 `timetable.json`**，之后按载入顺序回退到 fixtures / 内置样例 —— 确认对话框里
    如实这么写（不要写成"课表会变空"，那不是它的行为）。
  - **诊断用 CLI（照 `--size` 的先例加的，不点界面也能验整条链路）**：
    `--import <json>`（走界面同一条导入管线并落盘）、`--fetch-check <请求文件>`（抓一次、写日志、退出码表成败、
    **不落盘**）、`--settings-page <n>`（启动直接开设置窗口第 N 页；配 `--smoke` 时会把该页建出来并 `Measure`，
    用来钉住"导入页能构建且尺寸算得出来"）。三个开关合起来由 `.tools/verify-import.ps1` 驱动。
  - **设置窗口与接口细节**（`FileOpenPicker`/`XamlRoot`、`initialPage` 与「第一次点开落错页」、主操作放标题行、DPI 折算、两种接口的 `TermId` 来源）：见 [`docs/import.md`](docs/import.md)。
  - **验收**（`.tools/verify-import.ps1`，纯 ASCII，实测全绿）：A 无导入 → 黄金数据；B `--import` → 管线 + 落盘
    （并检查文件是 camelCase 且 14 门）；C 再启动 → 读回 `timetable.json`（导入优先于 fixtures）；
    D 四个设置页都能构建并量出尺寸；E 用**本地 `HttpListener` 合成服务**跑完整抓取链路（该方法不需要用户的
    Cookie：请求里写 `cookie: JSESSIONID=VERIFY` 就够走通"解析 → 发请求 → 探测 → 适配器"），
    外加服务端报错的负例，并核对服务端**实际收到**的 method / content-type / cookie；
    G 同一台合成服务再喂**报表格式**的响应（`data[].timeTableList[]`），验证 `calendarId` 从 URL 取到、
    14 门课解析出来、且**没有** `tongji.term.unknown`（学期命中内置表）。
  - **真机实测（用用户自己的那条报表请求）**：`--fetch-check` → HTTP 200 / 30261 字节 / `data 数组 15 条` /
    `tongji.report` → **14 门 / 19 条**、学期 `2026-2027学年第1学期`（16 教学周）。
- **内置登录窗口（2026-09-16，「登录同济获取课表」）** —— 登录态的第一条路（见「注意事项」第一条）：
  在应用自己的 WebView2 里打开 `https://1.tongji.edu.cn/`，用户走学校自己的统一身份认证（含短信），
  课表页那条接口的响应由我们**旁路接住**，再走与粘贴请求**完全相同**的导入管线。
  - **分层**：纯逻辑进 `dotnet/TjtCore/TongjiWebCapture.cs`（单测 `TjtCore.Tests/TongjiWebCaptureTests.cs`）——
    `IsEndpoint`（三条接口路径特征）/ `EndpointLabel` / `IsTongjiHost` / `CalendarIdOf` /
    `CookieHeader`（RFC 6265 简化版：域 + 路径 + 同名取更长路径）/ `Inspect`（URL 是端点 且 响应像课表）；
    窗口与 WebView2 事件留在 `dotnet/Tjt.App/TongjiLoginWindow.xaml(.cs)`；落盘走
    `ImportService.ApplyCapturedResponse`（内部仍是同一个 `Apply`，诊断/探测行/落盘不会分叉）。
  - **为什么让页面自己发请求**：报表接口要 `studentCode`（前端加密的 uid，算法在 bundle 里、随发版变），
    所以**不猜算法、也不读浏览器的 cookie 库** —— 页面自己去请求，我们只旁观，前端怎么改都抓得到。
  - **兜底：cookie 重发**。`GetContentAsync()` 读得到 GET 的响应体（实测 34 KB 一次成功），
    但 POST（旧 `getDataBk`）读不到 → 这时用 `CoreWebView2.CookieManager.GetCookiesAsync(url)` 取 cookie，
    经 `TongjiWebCapture.CookieHeader` 拼头，交给 **`TongjiFetcher.FetchSpecAsync`**（本轮新抽出的
    "已解析好的请求直接发"，`FetchAsync` 也走它）重发一次 GET。**cookie 只进请求头，日志只记条数**。
  - **WebView2 用独立 user data folder**：`%APPDATA%\TJDesktopTimetable\WebView2`（与用户的 Edge /
    其它 WebView2 应用隔离；登录态就留在那里，所以"下次打开还是登录状态"是自然结果）。
    关掉了 DevTools、右键菜单、**密码保存与自动填充**；`NewWindowRequested` 一律在当前窗口内继续导航
    （SSO / 短信页弹窗场景，否则用户会觉得"点了没反应"）；`WindowCloseRequested` → 关窗。
  - **入口三处**（与导入一致）：挂件 `⋯` →「登录同济获取课表…」、托盘同名项、设置窗口「导入」页的
    **登录同济并获取课表** 按钮。链路是 `WidgetActions.OpenLogin` → `MainWindow._openLogin` →
    `App.ShowTongjiLogin`（窗口单例，已开着就 `Activate`）。
  - **CLI**：`--login`（启动就开登录窗口）与 `--login-check <url>`（自检：开真窗口导航到给定地址，
    捕获到课表 → 写日志 → `Environment.Exit`，退出码表成败；60 秒看门狗兜底）。
  - **验收**（`.tools/verify-login.ps1`，纯 ASCII，**实测全绿**）：本地 `HttpListener` 假扮 1 系统，
    页面自己 `fetch` 报表接口（`?calendarId=122&studentCode=verify`）→ 程序捕获 → 14 门 / 19 条落盘。
    实测日志：`[login] WebView2 已就绪(profile=…)` → `[login] 撞上课表接口：报表接口 findStudentTimetab（HTTP 200）`
    → `[import] adapter=tongji-student 14 门课程 / 19 条上课安排…applied=True` → `[login] 捕获成功：…，34354 字节，calendarId=122`。
  - **踩过的坑**：① **验收脚本里不能出现中文**（PowerShell 5 按 ANSI 读，会报"字符串缺少终止符"）——
    匹配日志里的中文请用 ASCII 锚点（如 `\[login\].*findStudentTimetab`），别抄日志原文；
    ② **"读 Edge 的 cookie"这条路本机实测走不通**：Edge 运行时 Cookies 库被独占锁（`Copy-Item` / `cmd copy` /
    `robocopy` 全失败，`IOException`），且 Edge 153 已启用 App-Bound 加密（`Local State` 的
    `app_bound_encrypted_key` 前缀 `APPB`）——要解 v20 得调 IElevator COM。所以本功能**不碰浏览器数据**；
    ③ 登录窗口**不设** `ExtendsContentIntoTitleBar`（保留系统标题栏，拖窗/关闭更省事，与设置窗口的策略不同）。
    ④ **窗口图标必须显式设**（2026-09-16 修）——WinUI 3 的 `Window` **没有** `Icon` 属性，它注册的窗口类
    也不带图标，不设就回落到系统默认的"空白应用"图标（登录窗口的标题栏与任务栏实测就是它，而托盘图标
    因为走 `LoadImage` 一直是对的）。正解**两件一起**：`Rendering/WindowIcon.cs`（`AppWindow.SetIcon(Assets/app.ico)`，
    三个窗口构造里各调一次；失败只记日志不抛）＋ csproj 的 `<ApplicationIcon>`（只改 exe 的 PE 资源，
    **不能**替代 SetIcon）。验收：`.tools/verify-icon.ps1`（读 `WM_GETICON`，非零即 PASS）＋
    `.tools/dump-window-icon.ps1`（把窗口当前图标 dump 成 PNG，与 `Assets/app.ico` 的 48×48 逐像素相等即确证）。
- **本机的两个冒烟组合（都实测通过）**：
  - `-RunSmoke`：带材质 → `[backdrop] mode=mica-controller`；
  - `-RunSmoke -NoBackdrop`：跳过材质 → `[backdrop] skipped`；
  两者都产出 `[smoke] ok blocks=19 canvas=977x600`，`-DesktopLayer` 额外验证
  `[desktop-layer] attach=ok owner=0x...` 与 `send-to-bottom=ok`。
- **手动启动（双击即可）**：`.tools/make-launchers.ps1` 会在 `C:\tjt-tools\` 生成三个 `.cmd`：
  `TjtApp.cmd`（普通窗口）、`TjtApp-desktop.cmd`（贴桌面层）、`TjtApp-nobackdrop.cmd`（跳过材质）。
  它们做两件必须的事：设 `DOTNET_ROOT=C:\tjt-tools\dotnet`、在**真实 Windows 路径**上启动 exe。
  **不要直接双击 `Tjt.App.exe`**：找不到私有目录里的运行时，会弹 "You must install or update .NET"；
  **也不要从 `\\wsl.localhost\...` 运行**（UNC 路径不可靠，见全局 AGENTS.md）。
  日志写在 `C:\tjt-tools\app-launch.log`。改完代码先跑 `build-winui.ps1` 再启动，
  否则跑的还是上一次 `C:\tjt-tools\work` 里的旧产物。
- **应用自己写日志文件**（`--log <path>`，见 `AppLog.cs`）：本地 `Start-Process` 重定向 stdout 能拿到输出，
  但 GUI 子系统进程在别的宿主下可能拿不到 —— 文件通道是唯一可靠的，且**进程被强杀也保留最后阶段**。
  自检另有 45 秒看门狗（超时即打印最后阶段并非零退出），避免 CI/脚本悬着。
