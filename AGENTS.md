# AGENTS.md · TJDesktopTimetable

> 系统环境、WSL 网络与代理、工具链、字体等**通用**信息见全局 `~/.dsh/AGENTS.md`，本文件只写本项目相关内容。

## 项目定位

Windows 桌面小组件：把同济大学课表以半透明色块网格固定在桌面上。核心诉求是**架构分明、可扩展**——数据源（学校适配器）与窗口/渲染层彻底解耦，新增一所学校只需加一个适配器文件。

- 本地目录：`/root/TJDesktopTimetable`（仓库根）
- 远程仓库：https://github.com/gzy31007/TJDesktopTimetable （Public / MIT）
- 技术栈：Electron 44 + TypeScript + Vite（electron-vite）+ Vue 3；核心逻辑纯 TS；Win32 调用走 `koffi`

## 工作目录约定

```
packages/core/        @tjt/core —— 纯 TS，零平台依赖（模型/周次/冲突/布局/时间/适配器）+ vitest
packages/core/fixtures/  脱敏后的真实抓包数据（黄金测试基准，禁止放入学号、姓名等个人信息）
apps/desktop/         Electron 应用（main / preload / renderer）
docs/                 架构、数据模型、适配器指南
```

分层硬约束（改代码时必须遵守）：

1. `packages/core` **不得** import `electron` / `vue` / DOM API，保持可在 Node、浏览器、测试中直接运行。
2. `apps/desktop/src/renderer` **不得** import `electron`，只通过 preload 暴露的 `window.api` 通信。
3. `apps/desktop/src/main` 只做窗口、托盘、IO、Win32 调用，不写课表业务逻辑（业务逻辑一律进 core）。

## 任务规范

- 提交：Conventional Commits（英文类型 + 中文简述），例：`feat(core): 增加同济专业课表适配器`；推送前必须 `pnpm test && pnpm typecheck`。
- 测试：core 的每个公开函数都要有单测；同济个人课表适配器由 `test/e2e-timetable.spec.ts` 端到端覆盖（导入 → 布局 → 时间 → 单双周过滤）。
- 视觉规范：Win11 Fluent / 亚克力玻璃。设计令牌与基础控件在 `apps/desktop/src/renderer/shared/fluent.css`（色彩分级、圆角、阴影、明暗主题、`.f-btn`/`.f-pill`/`.f-switch`/`.f-card`），课表皮肤在 `shared/board.css`，挂件外壳在 `widget/widget.css`，管理窗口在 `manage/`。改渲染先读这几份，不要再引入一次性硬编码色值。
  - 历史基准（网格参数、色板、条纹特殊块、单双周并排）仍可对照 `/root/trivial/tongji-timetable/select_preview.html`，但视觉语言以 Fluent 令牌为准。
  - **挂件窗口材质（最终选型，2026-09-14 三次定稿：不用系统材质）**：`transparent: false` + **不设 `backgroundMaterial`** + DWM 圆角（`main/win32/dwm.ts` 的 `applyRoundedCorners`）+ **不透明实色底**（浅 `#f3f3f3` / 深 `#202020`，随主题经 `applyWidgetTheme()` 切换）。渲染层 `--glass-shell` 给半透明底提供玻璃层次，圆角不自绘，`hasShadow: false`（去掉窗口投影那层外部立体感）。
    **硬结论：挂件一律不开 `backgroundMaterial`——Acrylic 和 Mica 都会在"Win+D 隐藏 → ShowWindow 恢复"后 DWM 合成失效**（窗口 `IsWindowVisible` 为真、owner 与 z-order 全对、诊断 `coveredByShell:false`，但屏幕上就是不出现，Electron 侧无法感知也修不好）。两者各回归过一次：`acrylic` 版与"照抄设置窗口加回 `mica`"版都在 Win+D 后消失，去掉材质即恢复。
    **2026-09-14 晚补充**：这条结论的触发前提是"窗口被 Win+D 隐藏 → `ShowWindow` 恢复"；新层级层已让窗口**不被隐藏**（见下方"桌面 owner"条目），所以材质可能重新可用——**待回归验证**，验证通过前仍按"挂件不开材质"执行。
    附带代价：不用材质 = 没有壁纸色调；若为了色调开材质，就会周期性丢窗口，不划算。观感改由渲染层底 `--glass-shell`（与"课表预览"同源，取 `--layer-strong` 同值）承担。
  - **为什么不给挂件用 Acrylic / Mica（重要）**：Acrylic 在"Win+D 隐藏 → 恢复"之后 **DWM 合成会失效**——实测窗口 `IsWindowVisible` 为真、owner 正确、z-order 也正确（诊断字段 `coveredByShell:false`、排位在 Progman 之前），但屏幕上就是不出现，Electron 侧无法感知也修不好（`webContents.invalidate()` + bounds 抖动只能治一时）。它另有两个固有代价：失焦被 DWM 切成不活跃（变灰发蓝）、必须 `transparent: true` 而透明窗口拿不到 DWM 圆角。
  - **非透明窗口不能用透明底色**：`transparent: false` 时 `backgroundColor` 的 alpha 会被忽略，给 `#00000000` 得到黑底（材质画在黑上 = 一块死色）。必须给不透明实色。
  - **材质只在窗口创建时声明有效**：`backgroundMaterial` 写在 `new BrowserWindow({...})` 里才生效。运行时再设（Electron 的 `setBackgroundMaterial`，或 koffi 直写 `DWMWA_SYSTEMBACKDROP_TYPE`）即使 `DwmGetWindowAttribute` 读回 `accepted: 3` 也不出模糊。
  - **`tabbed`（Mica Alt）同样不要用在挂件上**：它和 mica/acrylic 走同一条 DWM 材质路径，按上一条结论一样会在 Win+D 后丢窗口。管理窗口可以用，挂件不要。
  - **不要用 `SetWindowRgn` 给透明窗口裁圆角**：实测拖动缩放约 12 秒后主进程**无日志直接重启**（原生层崩溃）；且 `transparent: true` 本身就拿不到 DWM 圆角。对应 DeskBox（WinUI 3 `MicaController`/`DesktopAcrylicController` + `SystemBackdropConfiguration`）的等价做法就是"非透明窗口 + `backgroundMaterial` + DWM 圆角属性"。
  - 管理窗口用主进程 `backgroundMaterial: 'mica'` + 自绘标题栏（`titleBarOverlay`，右侧留 `clamp(138px, 11vw, 190px)` 给系统按钮，深浅主题经 `window:titlebar-theme` 同步）。
  - **`.tt .grid-bg` 必须保留 `display: grid` + `grid-template-columns/rows`**：缺了这两行，77 个 `.cell` 会塌成 1px 高、边框全堆在顶部，看起来就是"列头下方一条莫名其妙的灰带"（排查时用 CDP 探针量 `.cell` 尺寸最快：正常应是 `colw × rowh`）。
  - 色块染色走三个 CSS 变量（`--tint` / `--edge` / `--ink`），由 `TimetableBoard.vue` 按主题内联设置；`lift()` 必须返回 `#rrggbb`（返回 `rgb()` 会让下游混色算出 NaN，色块直接变透明）。
  - WSL 内验收视觉：`pnpm -F @tjt/desktop dev:web` 起浏览器预览（支持 `?today=&now=&theme=` 覆盖，仅 mock 模式生效，见 `shared/api.ts` 的 `previewOverrides`），再用 Windows Edge 无头截图（`--screenshot` 写 `\\wsl.localhost\...` 路径可行，本机沙箱禁写 `/mnt/c`）。
- 代理：WSL 内装依赖优先用国内镜像直连（快 30 倍，CI 也适用）：
  `pnpm install --registry=https://registry.npmmirror.com`
  只有推送到 GitHub / 拉 GitHub 资源时才用代理：`export https_proxy=http://127.0.0.1:7897 http_proxy=http://127.0.0.1:7897`
- 打包：`pnpm dist:win` → WSL 内交叉出免安装 `apps/desktop/dist/win-unpacked`（**不需要 wine**）；NSIS 安装包交给 `.github/workflows/build-win.yml`。

## 易错知识点

- **已移除**「专业培养计划（`timetable/major`）适配器」与「教学班勾选」流程：现在只支持个人课表导入即用。若用户误把培养计划数据导进来（同一门课多个教学班），适配器会给 `tongji.looksLikePlan` 警告。
- 个人课表与培养计划是**同一套后端字段**（`dayOfWeek` / `weekState` / `timeStart` / `roomName` …），区别只在数据范围，所以字段映射逻辑可复用。
- `weekState` 是 16 位周次掩码，bit0 = 第 1 周；单双周掩码不要硬编码 `0x5555/0xAAAA`（只对 16 周成立），按 `term.totalWeeks` 生成。
- `dayOfWeek` 取值 1–7，**7 = 周日**（注意与 JS `Date.getDay()` 的 0=周日 区分）。
- 教学班去重键用 `teachingClassId`（数字），`code` 是教学班代码字符串（如 `00213702`），`courseCode` 是课程代码（如 `002137`），三者不可混用。
- 校历时间戳是毫秒（如 `beginDay: 1820160000000`），且 `weekBenginDay` 表示"周从周几开始"（同济为 2 = 周一），不是开学日。
- `koffi` 的平台二进制走 `optionalDependencies`（`@koromix/koffi-win32-x64`），在 WSL 上依赖 `pnpm-workspace.yaml` 的 `supportedArchitectures`，打包时必须 `asarUnpack: ["**/*.node"]`。
- **WSL 沙箱下打包必须重定向缓存**：electron-builder 默认写 `~/.cache/electron`，本机沙箱只允许写工作区 → 报 `EACCES: permission denied, mkdir '/root/.cache/electron'`。用 `pnpm -F @tjt/desktop dist:win:wsl`（内部传 `--config.electronDownload.cache=$PWD/.cache/electron`）。
- `electron-builder.yml` 里的 `electronVersion` 必须是**精确版本**：本地没装 electron 运行时（postinstall 被有意跳过），electron-builder 无法推断 `^44.3.0` 这种范围；升级 electron 时同步改这里。
- pnpm 11 默认拦截依赖 postinstall（`ERR_PNPM_IGNORED_BUILDS`）：新依赖需要构建脚本时，写进 `pnpm-workspace.yaml` 的 `allowBuilds`。
- WSL 内装依赖走代理极慢（实测 registry 请求 30s、19 KB/s）：改用国内镜像直连 `pnpm install --registry=https://registry.npmmirror.com`（实测 600 KB/s）。
- WSL 内无法验证 Win32 窗口层级（置底/穿透）行为，这部分只能在 Windows 真机验收。
- **透明窗口不要只依赖 `ready-to-show`**：`transparent: true` 的 BrowserWindow 在部分 Windows 配置下永不触发该事件，只在那里 `show()` 会得到"进程在跑但界面不出现"。挂件窗口用三重保险：`ready-to-show` + `did-finish-load` + 3 秒超时兜底（见 `windows/widget.ts` 的 `reveal`）。
- **首次启动必须给可见反馈**：没有课表时挂件是空内容且被压在 z-order 最底，用户会以为"没打开"。`main/index.ts` 在无课表或带 `--manage` 时会自动打开管理窗口。
- **单实例锁的副作用**：上一次实例没退出（哪怕界面不可见）时，再双击 exe 会被静默挡掉，表现同样是"打不开"。排查时先 `taskkill /F /IM TJDesktopTimetable.exe`。
- **不要从 UNC 路径（`\\wsl.localhost\...`）运行产物**：Chromium 需要内存映射加载 `resources.pak`/`icudtl.dat`，9p 文件系统上不可靠；产物要放到 Windows 本地磁盘（`C:\...`）再运行。WSL 侧复制过去极慢（9p 逐文件），让用户用资源管理器拖，或后台 robocopy。
- **`app.getPath('userData')` 默认取 package.json 的 `name`**（`@tjt/desktop` → `%APPDATA%\@tjt\desktop`）。已在 `main/index.ts` 显式 `app.setName` + `app.setPath('userData', ...)` 固定为 `%APPDATA%\TJDesktopTimetable`。
- **桌面 owner 用 Explorer 已创建的 `SHELLDLL_DefView`**（2026-09-14 二次修正，推翻当天早些时候的 Progman 结论）：用 `EnumWindows` 遍历顶层窗口 + `FindWindowExW(top, 0, "SHELLDLL_DefView", null)` 取第一个命中的桌面图标视图；写 owner 前存档原值、写后读回校验、失败即还原并回退，句柄缓存用 `IsWindow` 自愈。**绝不用 `SetParent`**（子窗口会被桌面图标压住、拖动坐标错乱），**绝不发 `0x052C` 催生 WorkerW**（登录期与 Explorer 恢复图标布局抢时序，会打乱用户的桌面图标）。
  - 为什么推翻：参照实现 DeskBox 的宿主取的就是 `SHELLDLL_DefView`（不是 Progman）；2026-09-14 真机实测本实现（owner=DefView）在 Win+D 后**既不隐藏也不最小化**——本次启动日志共 11 行、3 次 Win+D 产生 **0 条** hide/minimize，阳性对照（第三方窗口）被正常最小化，像素比对确认挂件矩形内亮度 27.3→28.4 未变成壁纸的 59.5。
  - 旧结论"DefView 会恢复了却看不见"的真凶不是 owner 选错，而是当时**另外四套机制同时在改 z-order**（每秒 `HWND_BOTTOM`、`hide` 里的 `SW_RESTORE` + owner 重挂、`nudgeRepaint` 的 1px 抖动、周期抢前台的 last-active-popup 修复）。
- **层级层结构（2026-09-14 重写，已拆分）**：`main/win32/` 下 `api.ts`（koffi 绑定唯一入口 + 常量 + 句柄工具）→ `desktop-host.ts`（宿主与 owner 生命周期）→ `resting.ts`（z-order 原语 + `WS_EX_NOACTIVATE` 摘戴）→ `resting-policy.ts`（**纯策略，零依赖，有单测**）→ `layer.ts`（编排）。
  - **静息落点三选一，不再一律置底**：无前台/前台是桌面壳 → 回桌面层（owner + 置底）；前台是自己或本应用其它窗口 → 只维护内部顺序、不动全局层级；前台是第三方应用 → 插到该应用**之后**（`SetWindowPos(hwnd, foreground, ...)`，`hWndInsertAfter` 是"插到它之后/更低"）。
  - **不再有每秒重压**：只留 5 秒 owner 巡检，owner 正常时一次 `GetWindowLongPtrW` 读、不产生任何 z-order 变化；Explorer 重启与显示变化交给 `watchDesktopLayerMessages()` 订阅 `TaskbarCreated` / `WM_DISPLAYCHANGE` / `WM_SETTINGCHANGE`（去抖 300ms），收到后作废宿主缓存并重新静息。
  - **交互期摘 `WS_EX_NOACTIVATE`、结束后戴回**（戴着它系统会跳过原生 move loop = 完全拖不动）：`suspendRestingStyle()` / `resumeRestingStyle()` 必须成对调用。
  - 启动后 1s/3s/6s 各打一行 `[win32] 层级自检`（Electron 的 `isVisible()`/`getBounds()` 与 Win32 的 `IsWindowVisible`/`GetWindowRect`/owner/父窗口链并排）；排查"Electron 说显示了、屏幕上看不见"时先看这三行。
- **`SetWindowPos` 的 `hWndInsertAfter` 是"插到该窗口之后（z-order 更低）"，不是"上方"**：曾误把 owner 传进去想让挂件"贴着桌面之上"，结果把它插到 Progman 下面被桌面盖住。owned 窗口本来就恒在 owner 之上，置底用 `HWND_BOTTOM` 即可。
- **owner 巡检必须和"当前期望的宿主"比较**：宿主解析结果会随 Explorer 重启而变（DefView 句柄被换掉），比较对象必须每次从 `resolveDesktopHost()` 实时取，不能缓存期望值——否则会每秒误判"丢失"并重挂、反复搅动 z-order。
- **鼠标交互期摘的是 `WS_EX_NOACTIVATE`，不是 owner**：贴桌面层时挂件是桌面宿主（`SHELLDLL_DefView`）的 owned window，owner 全程保留；要临时浮起/可拖动，靠 `suspendRestingStyle()`（摘 `WS_EX_NOACTIVATE` + `HWND_TOPMOST` → 立刻 `HWND_NOTOPMOST` 的脉冲）。旧实现"按下时摘 owner、松开挂回"已随 2026-09-14 重写删除。
- **不要再引入"修 Shell last active popup"这类抢前台的补救**（已随 2026-09-14 重写整体删除，`repairShellLastActivePopup` 不再存在）：它内部要 `SetForegroundWindow(Progman)` 再切回，等于周期性抢前台——用户观察到的"别的程序有焦点时挂件也会消失"就是它自己造成的干扰。新层级层不需要它：静息态戴 `WS_EX_NOACTIVATE` 且 owner 挂在桌面图标视图上，不参与前台争夺。
- **`detach()` 不隐藏窗口**：切换层级模式（托盘 / 设置面板改 `mode`）会 `attachLayer()` → 先 `detach()` 再重新 `attachToDesktop()`；旧实现里 `detach()` 调了 `SW_HIDE`，结果**切一次模式挂件就消失且没人再显示回来**。真正要隐藏只能走 `setWidgetVisible(false)`。
- **"贴桌面 + Win+D 后仍可见"要用 Owner，不是 SetParent**：`SetWindowLongPtrW(hwnd, GWLP_HWNDPARENT(-8), <桌面图标视图>)` —— owned 窗口恒在 owner 之上、不随 Win+D 隐藏或最小化，同时仍是顶层窗口（拖动/鼠标/坐标都正常）。`SetParent` 成 WorkerW 子窗口会被桌面图标压在下面，且拖动坐标错乱。owner 的**写入/校验/还原**细节见上方"桌面 owner"条目。
- **`WS_EX_NOACTIVATE` 会让窗口拖不动，但不要因此永久摘掉它**：不可激活的窗口会被系统跳过原生 move loop。正确做法是**静息时戴着、交互前摘掉、结束再戴回**（`suspendRestingStyle`/`resumeRestingStyle`）。戴着它才有点击不抢前台、不把挂件提到第三方应用之上的桌面手感。
- **Electron 里拖动窗口用 `-webkit-app-region: drag`**（Chromium 内建 `WM_NCHITTEST → HTCAPTION`，真实鼠标输入、跟手、不丢事件），交互控件加 `no-drag`。从主进程 `SendMessageW(hwnd, WM_NCLBUTTONDOWN, HTCAPTION)` 在 Electron 上实测**不生效**（渲染层 DOM 事件也会被 drag 区域吞掉，两者恰好构成自然降级：drag 生效时走原生，失效时走自实现循环）。
- 自实现拖动/缩放分支（WorkerW 子窗口用）必须双兜底：渲染层 `setPointerCapture` + 主进程 `GetAsyncKeyState(VK_LBUTTON)`。
- **拖动/缩放期间必须 `layer.pause()`**：owner 巡检会在拖动中途重挂 owner、和拖动抢 z-order（旧实现是每秒 `SetWindowPos(HWND_BOTTOM)`，已删除）。
- Windows 侧排查可用 WSL interop 直接调 `cmd.exe` / `powershell.exe`，但**参数里的引号与反斜杠会被 interop 再处理一次**：把逻辑写进 `.ps1`/`.bat` 再执行，不要在 `cmd /c` 里堆嵌套引号（`tasklist /FI "IMAGENAME eq x"` 这种就会解析失败）。`.ps1` 用 Windows PowerShell 5 执行时按 ANSI 读取，**脚本内容必须是纯 ASCII**（含中文注释会因引号配对错乱而解析失败）。

## 注意事项

- 仓库 Public：fixtures 与文档中不得出现学号、姓名、cookie、token 等任何个人凭据。
- **不做** 1 系统自动登录（SSO 带短信增强，塞进桌面客户端不划算）：改为用户手动粘贴 Cookie，主进程 `main/tongji.ts` 发起请求并探测接口路径。Cookie 存 `credentials.json`，**任何日志都不得打印 Cookie 内容**。
- 窗口默认「桌面层 + 静息」：Owner 设为桌面图标视图 `SHELLDLL_DefView`（Win+D 后仍可见），静息落点按前台窗口三选一（见"层级层结构"）；`wallpaper`（WorkerW 子窗口）与纯置底作为可切换/回退模式保留，切换失败必须自动回退，不能黑屏。
- 拖动用 `-webkit-app-region: drag`（见上）；静息态戴 `WS_EX_NOACTIVATE`（点击不抢前台），交互期由 `suspendRestingStyle()` 摘掉以便拖动，结束后 `resumeRestingStyle()` 戴回。
