# TJDesktopTimetable · 桌面课表小组件

把大学课表以半透明色块网格**固定在桌面上**的 Windows 小组件，已支持**同济大学 1 系统**与**上海交通大学「学在交大」**。贴桌面层、不抢焦点、可拖动缩放；课表手动获取一次即可；架构上把"学校适配器"与"窗口 / 渲染"解耦，新增一所学校只需加一个适配器文件。

> **主线 = WinUI 3（C#，`dotnet/`）**，自 v1.0.0 起正式发布，见 [Releases](../../releases)。
> 早期的 Electron + Vue 实现（`apps/desktop/` + `packages/core/`）**已于 2026-09-16 删除** ——
> 它的黄金 fixture 搬到了 `dotnet/fixtures/`，历史代码见 git 历史（`git log --diff-filter=D -- '*apps/desktop*'`）。

> **新增 Linux 线**：`dotnet/Tjt.Linux/`（Avalonia 壳，`dotnet/TjtTimetable.Linux.slnx`），
> 复用同一套 TjtCore / Tjt.Widget，在 Linux 桌面（X11 / XWayland，KDE 实测）上提供同款挂件，
> 详见下文 [Linux 版](#linux-版tjtlinux)。

## 特性

- **贴桌面**：窗口挂到桌面图标层（Owner = `SHELLDLL_DefView`）——浮在桌面图标之上、被普通窗口正常覆盖，**按 Win+D 显示桌面后依然可见**；不抢焦点。
- **无边框 + 自实现拖动/缩放**：四边四角八块热区、缩放光标、最小尺寸，位置尺寸与显示器记忆。
- **系统材质**：Mica / Mica Alt / Acrylic / 实色四种，**运行时即时切换**（不重建窗口）；深浅主题各自正确。
- **一眼看懂今天**：顶部显示「学期 · 第 N 周 · 今日 N 节」，当前周与今日列高亮，非全周课用条纹区分。
- **周末想收就收**：不常看周末时一键关掉（设置页 / 挂件 `⋯` 菜单 / 托盘三处同一开关），课表从 7 列变 5 列、列宽随之变宽。
- **开机自启**：设置「常规」页勾一下，登录 Windows 后自动把挂件放回桌面；关掉时会把注册表里的启动项删干净（写的是当前用户，不需要管理员）。
- **新版本提示**：启动后自动查一次 GitHub 上的最新发布（**可在设置「常规」页关掉**）；有新版本时托盘菜单与设置「关于」页会提示，点一下直接打开下载 —— 下载链接与查询都带 **gh-proxy.org 镜像**（国内直连 GitHub 不通时自动兜底，见下）。
- **导入即用**：应用里**登录一次**（内置 WebView2 窗口，走学校自己的登录 —— 同济统一身份认证含短信 / 交大 jAccount），课表自动到手；也支持粘贴一条浏览器请求，或导入本地 JSON。解析成功立即上桌面，**无需挑选教学班**。
- **可扩展**：`TjtCore` 是平台无关的核心库（Linux 上就能 `dotnet test`），适配器注册表 + 统一课表模型。
- **离线优先**：课表 / 账号 / 行为数据都只落在本机 JSON，**不上传**；Cookie 只存本机、不进日志。唯一的联网行为是启动后查一次 GitHub 上的最新版本（只读版本号，不发送任何本机信息，设置里可关）。查询**先直连 `api.github.com`**，直连失败才经 `gh-proxy.org` 镜像重试一次；「发现新版本」给的下载链接直接指向镜像（国内点开就能下）。

## 架构

```
导入请求 / JSON ──▶ adapters/*.cs ──▶ Tjt.Core 统一模型 ──▶ %APPDATA%\TJDesktopTimetable\timetable.json
                                          │
                                   周次 / 冲突 / 布局算法
                                          │
                          Tjt.Widget（纯计算：几何 / 染色 / 命名分档）
                                          │
                          Tjt.App（WinUI 3 外壳：窗口 / 材质 / Win32 / 托盘）
```

```
dotnet/TjtCore/         平台无关核心：模型、周次掩码、冲突、布局、时间、适配器
dotnet/Tjt.Widget/      挂件视觉层：几何、色块染色、名称分档、呈现模型（纯计算，Linux 可测）
dotnet/Tjt.App/         WinUI 3 外壳：窗口、材质、Win32 层级、托盘、设置窗口
  Data/                 设置 / 课表 / 凭据落盘、抓取、导入编排
  Rendering/            可视树搭建（不含业务逻辑）
  Win32/                层级层、拖动、缩放、托盘、消息钩子
dotnet/fixtures/        脱敏后的真实抓包数据（黄金测试基准，禁止放入学号/姓名等个人信息）
docs/                   desktop-layer.md · winui-build.md · winui-lessons.md · import.md
```

## 安装

**Windows（推荐：安装包）**

1. 从 [Releases](../../releases) 下载最新正式版的 `TJDesktopTimetable-v<版本>-setup.exe`（约 59 MB）。
2. 双击安装 → 装到 `Program Files`，向导里可以勾选**开始菜单 / 桌面快捷方式 / 开机自启 / 装完启动**。
3. 首次启动会打开设置窗口的「导入课表」页，按下一节导入一次即可。

> - 安装包同样是自包含的：**不需要**预装 .NET 或 Windows App Runtime。
> - 目前没有代码签名，SmartScreen 可能提示"未知发布者"——点「更多信息 → 仍要运行」即可。
> - 卸载：控制面板「应用和功能」里卸载；**你的课表与登录信息会保留**在
>   `%APPDATA%\TJDesktopTimetable\`，卸载完成页会告诉你路径。
> - 如果机器上没有 WebView2 Runtime（登录窗口的依赖），安装时会提示一次 —— 不影响安装，
>   也不影响"粘贴一条浏览器请求"那条导入路径（Win11 自带；Win10 可从 Microsoft 官网免费装）。

**Windows（备选：绿色 zip）**

1. 从 [Releases](../../releases) 下载最新正式版的 `TJDesktopTimetable-v<版本>-win-x64.zip`。
2. 解压到任意**本地磁盘**目录（别放在 `\\wsl.localhost\...` 这类 UNC 路径下）。
3. 双击 `Tjt.App.exe`。（这一份同样自包含。）

**Linux**

1. 从 [Releases](../../releases) 下载最新正式版的 `TJDesktopTimetable-v<版本>-linux-x64.tar.gz`（自包含单文件：**不需要**预装 .NET）。
2. `tar -xzf TJDesktopTimetable-v<版本>-linux-x64.tar.gz -C ~/.local/opt/TJDesktopTimetable`。
3. 运行 `~/.local/opt/TJDesktopTimetable/Tjt.Linux`。首次启动没有课表时会打开导入窗口。
   > 依赖：X11 / XWayland，以及 `libICE` / `libSM`（Debian/Ubuntu：`sudo apt install libice6 libsm6`）；
   > 中文课表需要 CJK 字体。详见下面 [Linux 版](#linux-版tjtlinux)。

> 无论哪种安装方式，数据都在 `%APPDATA%\TJDesktopTimetable\`（`settings.json` / `timetable.json` / `credentials.json`）；
> 想彻底清干净就一并删掉。Linux 数据目录为 `~/.config/TJDesktopTimetable/`。
> 开发机上重新构建后启动：`C:\tjt-tools\TjtApp.cmd`（普通窗口）/ `TjtApp-desktop.cmd`（显式贴桌面层）/ `TjtApp-nobackdrop.cmd`（跳过材质）。

## Linux 版（Tjt.Linux）

用 [Avalonia](https://avaloniaui.net/) 写的 Linux 壳，与 Windows 版**同一套核心**
（TjtCore 解析 / Tjt.Widget 算坐标与染色，渲染层照数字摆控件）。在 KDE Plasma（Wayland 会话走
XWayland）上实测通过。

### 功能对齐情况

| 能力 | Windows 版 | Linux 版 |
|---|---|---|
| 贴桌面层（显示桌面后仍可见） | Owner = `SHELLDLL_DefView` | X11 `_NET_WM_WINDOW_TYPE_DESKTOP` + `_NET_WM_STATE_BELOW` |
| 拖动 / 八热区缩放 | Win32 自实现轮询 | Avalonia `BeginMoveDrag` / `BeginResizeDrag`（热区几何仍走 `CursorZones`/`ResizePolicy`） |
| 位置尺寸记忆 | `settings.json`（**外框** DIP + `FrameCorrection`） | `settings.json`（**客户区** DIP）；文件同名同形可互拷，但尺寸会差一圈窗口边框，且 Linux 回写不带 Windows 专属字段 |
| 主题 | Mica / Acrylic / 实色 | 固定半透明壳（合成器真透明 + 圆角），跟随系统深浅色 |
| 导入 | 内置登录（WebView2）/ 粘贴请求 / JSON | **粘贴请求 / JSON**（内置登录是 WebView2 专属，Linux 无对应） |
| 托盘 / 设置窗口 | 有 | 暂无（挂件 `⋯` 菜单承载全部入口） |

### 构建与运行

需要 .NET 10 SDK（Arch：`sudo pacman -S dotnet-sdk`；其他发行版见
[官方文档](https://learn.microsoft.com/dotnet/core/install/linux)）与 X11 或 XWayland（KDE/GNOME
Wayland 会话自带）。

运行时还需要 Avalonia X11 后端的系统库 **`libICE` / `libSM`**（Debian/Ubuntu：`sudo apt install libice6 libsm6`；
Arch：`sudo pacman -S libice libsm`）—— 桌面发行版一般都随 DE 装好了，但精简环境/容器里缺了会**启动即崩**
（`DllNotFoundException: libICE.so.6`）。另外中文课表要有一个含 CJK 的字体
（`noto-fonts-cjk` / `fonts-noto-cjk` 等），否则中文会渲染成方框 —— 字体回退链只保证"拿得到一个默认字体"。

```bash
# 测试（平台无关，与上游同一条命令）
dotnet test dotnet/TjtTimetable.slnx

# 运行挂件
dotnet run --project dotnet/Tjt.Linux

# 自包含发布（单文件 + Skia 原生库 + fixtures，免装 .NET）
dotnet publish dotnet/Tjt.Linux/Tjt.Linux.csproj -c Release -r linux-x64 \
  --self-contained /p:PublishSingleFile=true /p:DebugType=embedded
```

常用开关：`--dark` / `--light`（主题）、`--size 880x600`、`--fixture <path>`、
`--weekend` / `--no-weekend`、`--no-desktop-layer`、`--import <json>`（启动即导入落盘）、
`--fetch-check <请求文件>`（抓取自检，退出码表成败）、`--log <path>`。

### 桌面集成

```bash
install -Dm644 dotnet/Tjt.Linux/packaging/tjt-linux.desktop ~/.local/share/applications/
install -Dm644 <发布目录>/Assets/app-256.png ~/.local/icons/tjt-linux.png
# 然后编辑 .desktop 里的 Exec= 与 Icon= 指向实际路径
```

数据目录：`~/.config/TJDesktopTimetable/`（`settings.json` / `timetable.json` / `credentials.json`，
与 Windows 版同构）。首次启动没有导入过课表时会自动打开导入窗口。

### 已知限制

- **X11 / XWayland only**：Avalonia 稳定线没有原生 Wayland 后端；KWin 对 XWayland 窗口完整支持
  DESKTOP 类型 + keep-below（实测「显示桌面」后挂件仍可见）。其他 WM（GNOME/Mutter 等）行为未验证，
  不生效时可用 WM 自身的窗口规则兜底。
- **运行期改窗口类型仅 KWin 实测**：DESKTOP 属性是在窗口 map 之后才改的（切回 NORMAL 同理），
  EWMH 客户消息要求 WM 订阅 `SubstructureRedirectMask`——没有 WM 接收时日志会记
  `[x11] … 没有被任何 WM 接收` 而不是静默"成功"。
- **别再用 DOCK 类型**：KWin 5 的 `layerForDock()` 会把 keep-below 的 dock 压到 Normal 层（恰好可用），
  KWin 6 起 `belongsToLayer()` 直接 `isDock() → AboveLayer`（keepBelow 分支走不到）——DOCK 一开就置顶
  （v6.7.5 实测；v1.2.0 发布的 Linux 版用的正是 DOCK，本版因此换成 DESKTOP）。
- **DESKTOP 型窗口会被 KWin 压到 plasmashell 桌面容器（壁纸）之下**，开启贴桌面层时必须跟一个
  `XRaiseWindow`（客户端合法的 ConfigureRequest(Above)），否则挂件整个"消失"在壁纸后面。
- 内置登录窗口是 WebView2 专属能力，Linux 用「粘贴一条浏览器请求」路径（功能等价）。
- 没有托盘图标与设置窗口；「贴桌面层 / 显示周末」等开关在挂件 `⋯` 菜单里。
- KWin 重启后贴桌面层状态会丢（Windows 版有 owner 巡检兜底，Linux 版暂未做），重开一次应用即可。


## 课表数据从哪来

**A. 内置登录（推荐）**

这个窗口只有两个触发点：**设置窗口「导入」页 → 选学校（同济 / 交大下拉框）→ 点「登录并获取课表」**（手动），
以及命令行 `--login`（学校由 `--login-school` 定）。
挂件 `⋯` 菜单与托盘里不设这一项 —— 换课表本来就要进「导入」页。

> 首次启动（还没有导入过课表）**不会**替你选学校：挂件上先显示一张**虚构的示例课表**，
> 并自动打开设置窗口的「导入」页，由你在「登录同济」/「登录交大」/「粘贴请求」里挑一个。

1. 弹出一个应用自己的浏览器窗口，里面是**学校自己的登录页** —— 账号密码与验证码都在那里完成，
   本应用不接触密码（该窗口关闭了密码保存与自动填充）。
2. 登录后打开课表页：
   - **同济**：点开「我的课表」，页面自己会去调那条课表接口，程序**在一旁把响应接住** →
     解析 → 立即应用到桌面挂件，然后窗口自己关上。
   - **交大**：登录 `j.sjtu.edu.cn` 后课表页会自动加载；程序随即用这份登录态取一次**整学期课表**
     与**教务日历**（课表页默认按周拉取，那条响应里没有"这门课上哪些周"），拿到就导入并关窗。
3. 登录态（cookie）留在应用自己的 WebView2 profile（`%APPDATA%\TJDesktopTimetable\WebView2`），
   与你的 Edge / Chrome 完全隔离；下次再打开通常还是登录状态，点开课表页即可刷新。

> - 这里刻意**不猜** `studentCode`（前端加密的 uid）也不去读浏览器的 cookie 数据库：
>   页面自己去发请求，我们只做旁观者 —— 前端怎么改都抓得到。
> - **交大那条为什么不一样**：jAccount 登录是标准 OAuth2，课表接口只要明文的 `year`/`semester` ——
>   所以拿到登录态后主动取整学期数据，而不是照抄页面那条按周请求（照抄只会剩本周有课）。
> - 命令行自检：`--login-check <url>`（对本地合成服务跑一遍捕获链路，退出码表成败；见 `.tools/verify-login.ps1`）；
>   `--login-school tongji|sjtu` 选学校。

**B. 直接获取（粘贴浏览器请求）**

不想在本应用里登录时用这条。

**同济：** 浏览器登录 [1 系统](https://1.tongji.edu.cn/)，打开"我的课表"页面。
2. F12 → **Network** → 刷新页面 → 找到**返回 200 且内容是课程列表**的那条请求 → 右键 → **Copy → Copy as cURL**。
   - 复制方式：右键 → **Copy as PowerShell**（`Copy as cURL` 也支持）。
   - 课表页现在调的是 `GET /api/electionservice/reportManagement/findStudentTimetab?calendarId=…&studentCode=…`
     （研究生页是 `findSchoolTimetab2`）；旧接口 `…/student/xxxx/getDataBk` 同样支持。
   - 复制错请求（例如 `schoolCalendar/detail`——那是**校历**、里面没有课程）时，程序会在探测行里如实告诉你
     它看到了什么，而不是默默失败。
   - 只复制了第一行（没有 `-H 'cookie: …'`）时也会明确提示"看起来只复制了第一行"。
3. 打开"导入课表"页，把这条请求整段粘贴进去 → 点 **获取我的课表**。
4. 程序照原样请求一次（请求里自带 Cookie 与 `x-token`）→ 解析 → 立即应用到桌面挂件；
   学期 id 直接从请求 URL 的 `calendarId` 取，所以"当前第几周"也是准的。

**交大：** 浏览器登录 `j.sjtu.edu.cn` 打开课表页，F12 里右键 → Copy as PowerShell，**复制任意一条**课表请求
（`/app/stu/lesson/listBySemester` 或按周的 `listByWeek`）即可。程序只从里面取 `year`/`semester`
与 Cookie，然后自己去取**整学期课表 + 教务日历** —— 因为按周那条响应不带周次信息，
照它建出来的课表只会剩本周有课。请求只会打到 `j.sjtu.edu.cn`（粘错地址也不会把登录态发去别处）。

> 粘贴内容只保存在本机 `%APPDATA%\TJDesktopTimetable\credentials.json`，不会上传、不进日志（日志里只记长度）；
> 用完可在浏览器退出登录使其失效。
> 命令行自检：`--fetch-check <请求文件>`（抓一次、把探测结果写日志、不落盘、退出码表成败）。

**C. 本地 JSON 导入**

1. 同上抓包，把课表接口响应另存为 JSON（可选再存一份校历响应）。
2. "导入课表"页 → 选择 JSON 文件（或直接粘贴 JSON）→ **导入并应用**。

> 导入结果落盘在 `%APPDATA%\TJDesktopTimetable\timetable.json`（camelCase），可直接手工替换 / 备份。

## 已知限制

- 内置登录需要 WebView2 运行时（Windows 11 与较新的 Win10 已自带）；登录态存在应用自己的 profile 里，
  过期后重新登录一次即可。**不做**"读取 Edge/Chrome 已有 cookie"：那是绕过浏览器的应用绑定加密，
  本应用选择让页面自己去登录、自己发请求。
- 研究生页的 `findSchoolTimetab2` 分支按前端源码实现，**未在真机实测**。
- 内置学期表只覆盖已知学期（当前 `122` / `124`）：更远的学期导入后课表正常，但顶部不显示"现在第几周"。
- Acrylic 需要透明窗口，观感可能弱于 Mica。
- 设置里的**周次过滤（只看单周 / 双周）尚未实现**；「显示周末」已在 v1.1.0 支持（外观页 / 挂件 `⋯` 菜单 / 托盘）。
- 仅 x64：Windows 10 2004+ / linux-x64（Linux 版限制见上文「已知限制」）。

## 开发

```bash
# 平台无关的核心库 + 挂件视觉层（Linux / WSL 直接跑）
DOTNET_CLI_HOME=$PWD/.tools/dotnet-home ./.tools/dotnet/dotnet test dotnet/TjtTimetable.slnx

# WinUI 外壳：只能在 Windows 构建（脚本会 robocopy 到 C:\tjt-tools\work 再编译）
PS=/mnt/c/Windows/System32/WindowsPowerShell/v1.0/powershell.exe
$PS -NoProfile -ExecutionPolicy Bypass -File '\\wsl.localhost\Ubuntu-24.04\root\TJDesktopTimetable\.tools\build-winui.ps1' -RunSmoke
```

> ⚠️ 开关（`-RunSmoke` / `-NoBackdrop` / `-DesktopLayer`）**必须**配合 `-File` 用；
> `-EncodedCommand "$B64"` 那种形式后面再跟开关会被 PowerShell 当成非法参数（实测）。

改代码前请先读 [`AGENTS.md`](AGENTS.md)：那里有分层硬约束、以及这几年踩过的坑（层级层、材质、DPI、Win32 层级、验证脚本）。

## 扩展其他学校

适配器契约在 `dotnet/TjtCore/Adapters/AdapterTypes.cs`（`Detect` + `Parse`）：实现一个适配器 → 在 `Registry.cs` 里登记 → 往 `dotnet/fixtures/` 放一份脱敏 fixture → 写测试。核心算法与全部 UI 都不用改（`dotnet/TjtCore.Tests/` 里有现成的移植验收用例可照抄）。

## 路线图

- [x] v0.x · 核心库、客户端外壳、贴桌面层级、材质、拖动缩放、托盘与设置窗口
- [x] **v1.0.0** · WinUI 3 主线正式发布（真实课表导入 / 四种材质 / 无边框缩放 / 托盘与设置 / 自包含发布）
- [x] 内置登录窗口（WebView2 里走学校 SSO，旁路捕获课表接口响应）
- [x] **v1.1.0** · 内置登录窗口 + 「显示周末」开关（外加遮挡误拖、表头呼吸位、窗口图标三项修复）
- [x] **v1.2.0** · Linux 版（Avalonia 壳）首发；视觉收口（时间列 64 DIP / 深色色块提亮 / 名称按宽度截断）+
      时间线按节次分段、画布贴顶、材质四档对齐 DeskBox
- [x] 上海交大「学在交大」课表：`sjtu-student` 适配器 + 整学期抓取（含教务日历）+ 内置登录窗口
- [x] **Windows 安装包**（Inno Setup）：装到 Program Files、四个安装勾选、卸载保留数据、CI 随 Release 一起产出
- [x] **v1.3.0** · 开机自启 + **新版本提示**（启动后查一次 GitHub 最新发布：托盘与设置「关于」页提示，
      可关、可跳过某个版本）
- [x] **v1.3.1** · Linux 壳六项修复（紧凑布局与 Windows 同口径、X11 事件掩码修正、导入诊断链路打通、
      凭据权限 0600/0700、外壳健壮性）+ 贴桌面层换 `DESKTOP`（KWin 6 下不再被置顶）
- [ ] 新学期自动取校历（不再依赖内置学期表）
- [ ] 周次过滤（只看单周 / 双周）
- [ ] ICS / 图片导出
- [ ] 多校适配器

## 许可

MIT © 2026 gzy31007
