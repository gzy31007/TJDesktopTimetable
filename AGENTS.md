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
- 测试：core 的每个公开函数都要有单测；同济个人课表适配器由 `test/e2e-timetable.spec.ts` 端到端覆盖（导入 → 布局 → 时间 → 单双周过滤）；**同格撞车**由 `fixtures/tongji-2026-1-collision.json`（构造数据，非抓包）覆盖，TS 与 C# 两侧共用这一份（C# 侧 `CollisionE2ETests.cs`）。
- 视觉规范：Win11 Fluent / 亚克力玻璃。设计令牌与基础控件在 `apps/desktop/src/renderer/shared/fluent.css`（色彩分级、圆角、阴影、明暗主题、`.f-btn`/`.f-pill`/`.f-switch`/`.f-card`），课表皮肤在 `shared/board.css`，挂件外壳在 `widget/widget.css`，管理窗口在 `manage/`。改渲染先读这几份，不要再引入一次性硬编码色值。
  - 历史基准（网格参数、色板、条纹特殊块、单双周并排）仍可对照 `/root/trivial/tongji-timetable/select_preview.html`，但视觉语言以 Fluent 令牌为准。
  - **挂件窗口材质（2026-09-14 四次定稿：材质回归成功，改成可选设置）**：`settings.material` = `solid`（默认，不透明实色底）/ `mica` / `mica-alt` / `acrylic`，在「设置 → 显示与行为 → 窗口材质」里切。映射见 `windows/widget.ts` 的 `resolveMaterial()`：
    - `solid`：`transparent: false` + 不设 `backgroundMaterial`，DWM 圆角 + 不透明实色底（浅 `#fafafa` / 深 `#202020`，随 `applyWidgetTheme()` 切换），`hasShadow: false`；
    - `mica` / `mica-alt`：非透明窗口 + `backgroundMaterial: 'mica' | 'tabbed'`，**保住 DWM 圆角**；
    - `acrylic`：**必须 `transparent: true`** 才糊得出来，代价是 `roundedCorners` 关掉（拿不到 DWM 圆角）。
    **切换材质必须重建窗口**（`recreateWidgetWindow()`）：`backgroundMaterial` 只在 `new BrowserWindow()` 时生效，运行时 `setBackgroundMaterial()` 即使 `DwmGetWindowAttribute` 读回 accepted 也不出模糊。
    **材质结论的变迁（重要，别再引用旧结论）**：旧硬结论是"挂件一律不开 `backgroundMaterial`"，理由是 Acrylic 与 Mica 都会在"Win+D 隐藏 → `ShowWindow` 恢复"之后 DWM 合成失效（`IsWindowVisible` 为真、owner 与 z-order 全对，但屏幕上不出现）。**该结论的触发前提已经消失**：新层级层让挂件压根不被隐藏（见"桌面 owner"条目）。
    2026-09-14 晚真机回归（每次都用 `Shell.Application`/`keybd_event` 触发 Win+D，并以第三方窗口被最小化作为阳性对照）：
    - `mica`：Win+D 后 `IsWindowVisible=True / IsIconic=False`，截取的挂件矩形里**壁纸与完整挂件内容同屏**（说明挂件画在桌面之上、没有被藏起来）；
    - `acrylic`：同样 `True/False`，截图是**完整的课表 + 工具条 + 亚克力面板**。
    所以材质重新可用；默认仍是 `solid`（最稳），材质不稳时用户随时能退回。观感层次仍由渲染层 `--glass-shell`（与"课表预览"同源，取 `--layer-strong` 同值）承担。
  - **Acrylic 的固有代价（与 Win+D 无关，仍然成立）**：失焦时被 DWM 切成不活跃（变灰发蓝）；必须 `transparent: true`，而透明窗口拿不到 DWM 圆角（要圆角就得用 `mica` 或 CSS 自绘）。
  - **非透明窗口不能用透明底色**：`transparent: false` 时 `backgroundColor` 的 alpha 会被忽略，给 `#00000000` 得到黑底（材质画在黑上 = 一块死色）。必须给不透明实色。
  - **材质只在窗口创建时声明有效**：`backgroundMaterial` 写在 `new BrowserWindow({...})` 里才生效。运行时再设（Electron 的 `setBackgroundMaterial`，或 koffi 直写 `DWMWA_SYSTEMBACKDROP_TYPE`）即使 `DwmGetWindowAttribute` 读回 `accepted: 3` 也不出模糊。
  - **`tabbed`（Mica Alt）现在也能用在挂件上**：它与 mica/acrylic 同走 DWM 材质路径，而材质回归已通过；管理窗口仍按原样用 `mica`。
  - **不要用 `SetWindowRgn` 给透明窗口裁圆角**：实测拖动缩放约 12 秒后主进程**无日志直接重启**（原生层崩溃）；且 `transparent: true` 本身就拿不到 DWM 圆角。对应 DeskBox（WinUI 3 `MicaController`/`DesktopAcrylicController` + `SystemBackdropConfiguration`）的等价做法就是"非透明窗口 + `backgroundMaterial` + DWM 圆角属性"。
  - 管理窗口用主进程 `backgroundMaterial: 'mica'` + 自绘标题栏（`titleBarOverlay`，右侧留 `clamp(138px, 11vw, 190px)` 给系统按钮，深浅主题经 `window:titlebar-theme` 同步）。
  - **底板的"不透明度"只作用在背景层**：底板画在 `.widget-shell::before` 上，`opacity: calc(0.55 + 0.45 * var(--shell-alpha))`。旧写法把 `opacity` 加在整个 `.widget-shell` 上、内容层再乘一次 `0.55 + 0.45 * alpha`，结果是滑杆拉到 0.3 时**文字与网格一起糊掉**（0.3 × 0.685 = 0.2）。下限 0.55 是为了保住对比度：浅色主题的深字压在"壁纸透上来的深底"上会直接读不出来（DeskBox 文档里那句"不要为了更透明而牺牲内容边界"就是这个）。**描边（`inset` box-shadow）不跟着变透**，它是卡片边界。
  - **`.tt .grid-bg` 必须保留 `display: grid` + `grid-template-columns/rows`**：缺了这两行，77 个 `.cell` 会塌成 1px 高、边框全堆在顶部，看起来就是"列头下方一条莫名其妙的灰带"（排查时用 CDP 探针量 `.cell` 尺寸最快：正常应是 `colw × rowh`）。
  - 色块染色走三个 CSS 变量（`--tint` / `--edge` / `--ink`），由 `TimetableBoard.vue` 按主题内联设置；`lift()` 必须返回 `#rrggbb`（返回 `rgb()` 会让下游混色算出 NaN，色块直接变透明）。
  - WSL 内验收视觉：`pnpm -F @tjt/desktop dev:web` 起浏览器预览（支持 `?today=&now=&theme=` 覆盖，仅 mock 模式生效，见 `shared/api.ts` 的 `previewOverrides`），再用 Windows Edge 无头截图（`--screenshot` 写 `\\wsl.localhost\...` 路径可行，本机沙箱禁写 `/mnt/c`）。
- 代理：WSL 内装依赖优先用国内镜像直连（快 30 倍，CI 也适用）：
  `pnpm install --registry=https://registry.npmmirror.com`
  只有推送到 GitHub / 拉 GitHub 资源时才用代理：`export https_proxy=http://127.0.0.1:7897 http_proxy=http://127.0.0.1:7897`
- 打包：`pnpm dist:win` → WSL 内交叉出免安装 `apps/desktop/dist/win-unpacked`（**不需要 wine**）；NSIS 安装包交给 `.github/workflows/build-win.yml`。

## 易错知识点

- **已移除**「专业培养计划（`timetable/major`）适配器」与「教学班勾选」流程：现在只支持个人课表导入即用。若用户误把培养计划数据导进来（同一门课多个教学班），会表现为导入结果异常（**没有** `tongji.looksLikePlan` 这类专门警告——2026-09-15 全仓 grep 确认该标识在 TS/C# 源码里都不存在，旧文档此条不准；适配器层里真正的诊断码是 `tongji.personal` / `tongji.flat` / `tongji.noSchedule` / `tongji.schedule.missing` / `tongji.term.unknown` / `tongji.term.startDate` / `tongji.summary`）。
- 个人课表与培养计划是**同一套后端字段**（`dayOfWeek` / `weekState` / `timeStart` / `roomName` …），区别只在数据范围，所以字段映射逻辑可复用。
- `weekState` 是 16 位周次掩码，bit0 = 第 1 周；单双周掩码不要硬编码 `0x5555/0xAAAA`（只对 16 周成立），按 `term.totalWeeks` 生成。
- `dayOfWeek` 取值 1–7，**7 = 周日**（注意与 JS `Date.getDay()` 的 0=周日 区分）。
- **并排分组键是「同天 + 同起止节次」，不是"时间段相交"**：周一 1-2 节与周一 1-3 节算两格，各自独占整列（视觉上互相压住）。布局不做跨块几何排布，这是与 `select_preview.html` 一致的既有语义。
- **并排 ≠ 冲突**：单双周错开的两门课（如 1-8 周 / 9-16 周）在 `conflict.ts` 里不算冲突，但布局照样把同一格的两块并排（`colCount = 2`）。改布局时别把 `colCount` 和 `coursesConflict` 混为一谈；两端都有黄金用例钉住（`fixtures/tongji-2026-1-collision.json`）。
- **同格多条 times 的合并键含教室**（适配器层：同天 + 同起止节次 + 同教室才合并、周次取并集）；同格不同教室不合并，成为并排的两块。
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
- **层级层的结论/证据/外观说明见 `docs/desktop-layer.md`**（含"怎么复现验收"与"怎么切到深色亚克力观感"）。下面是改代码时必须遵守的要点：
- **层级层结构（2026-09-14 重写，已拆分）**：`main/win32/` 下 `api.ts`（koffi 绑定唯一入口 + 常量 + 句柄工具）→ `desktop-host.ts`（宿主与 owner 生命周期）→ `resting.ts`（z-order 原语 + `WS_EX_NOACTIVATE` 摘戴）→ `resting-policy.ts`（**纯策略，零依赖，有单测**）→ `layer.ts`（编排）。
  - **静息落点三选一，不再一律置底**：无前台/前台是桌面壳 → 回桌面层（owner + 置底）；前台是自己或本应用其它窗口 → 只维护内部顺序、不动全局层级；前台是第三方应用 → 插到该应用**之后**（`SetWindowPos(hwnd, foreground, ...)`，`hWndInsertAfter` 是"插到它之后/更低"）。
  - **不再有每秒重压**：只留 5 秒 owner 巡检，owner 正常时一次 `GetWindowLongPtrW` 读、不产生任何 z-order 变化；Explorer 重启与显示变化交给 `watchDesktopLayerMessages()` 订阅 `TaskbarCreated` / `WM_DISPLAYCHANGE` / `WM_SETTINGCHANGE`（去抖 300ms），收到后作废宿主缓存并重新静息。
  - **静息态不戴 `WS_EX_NOACTIVATE`**。真机实测：戴着它时 `-webkit-app-region: drag` 的标题栏既不走系统原生 move loop（不可激活窗口被跳过），渲染层也收不到 pointerdown（Chromium 拖拽区把事件吞了），两者叠加 = **挂件完全拖不动**（日志里连一条"开始拖动"都没有）。所以静息就是"普通顶层窗口 + owner 挂桌面图标视图"，点击会短暂激活并把它提到普通层级带顶部，交互结束由落点策略把它放回去 —— 与 DeskBox 默认的"动态层级"一致（它只在实验性的 DesktopPinned 模式才戴 NOACTIVATE）。
  - 交互入口：`suspendRestingStyle()`（兜底清 NOACTIVATE + `HWND_TOPMOST` → 立刻 `HWND_NOTOPMOST` 脉冲，临时浮到普通带顶部）/ `resumeRestingStyle()`（按前台重新落点）。必须成对调用。
  - **拖动/缩放的真机结论**（2026-09-14，合成鼠标事件验证）：拖动走的是 **Chromium 原生路径**（拖拽区 → `WM_NCHITTEST → HTCAPTION` → 系统 move loop），窗口位移与鼠标位移一致（实测 +130,+100 的拖拽让窗口 `left/top` 从 739,283 变成 859,375），且**不会**触发渲染层的 `beginDrag`（那条日志是给缩放/自实现分支用的）。缩放柄不是拖拽区，走渲染层 `beginResize` → `suspendRestingStyle` → 自实现循环（日志可见"开始缩放"）。
  - 改完层级行为后，用 `.tools/layer-probe.ps1`（本机脚本，gitignore）做真机回归：它自己找 owner=DefView 的挂件窗口，按 Win+D、比对 `IsWindowVisible/IsIconic` 与像素，并打印阳性对照；注意 **PowerShell 进程不是 DPI 感知的**，`GetWindowRect`/`SetCursorPos` 用的是虚拟化坐标，而应用日志里的 `rect` 是物理坐标（本机缩放 150%，两者差 1.5 倍），混用会得到"看起来没动"的假结论。
- 启动后 1s/3s/6s 各打一行 `[win32] 层级自检`（Electron 的 `isVisible()`/`getBounds()` 与 Win32 的 `IsWindowVisible`/`GetWindowRect`/owner/父窗口链并排）；排查"Electron 说显示了、屏幕上看不见"时先看这三行。
- **`SetWindowPos` 的 `hWndInsertAfter` 是"插到该窗口之后（z-order 更低）"，不是"上方"**：曾误把 owner 传进去想让挂件"贴着桌面之上"，结果把它插到 Progman 下面被桌面盖住。owned 窗口本来就恒在 owner 之上，置底用 `HWND_BOTTOM` 即可。
- **owner 巡检必须和"当前期望的宿主"比较**：宿主解析结果会随 Explorer 重启而变（DefView 句柄被换掉），比较对象必须每次从 `resolveDesktopHost()` 实时取，不能缓存期望值——否则会每秒误判"丢失"并重挂、反复搅动 z-order。
- **鼠标交互期只做"临时浮起"，不动 owner**：贴桌面层时挂件是桌面宿主（`SHELLDLL_DefView`）的 owned window，owner 全程保留；`suspendRestingStyle()` 只清一次 NOACTIVATE（兜底）+ 打 `HWND_TOPMOST` → 立刻 `HWND_NOTOPMOST` 的脉冲。旧实现"按下时摘 owner、松开挂回"已随 2026-09-14 重写删除。
- **不要再引入"修 Shell last active popup"这类抢前台的补救**（已随 2026-09-14 重写整体删除，`repairShellLastActivePopup` 不再存在）：它内部要 `SetForegroundWindow(Progman)` 再切回，等于周期性抢前台——用户观察到的"别的程序有焦点时挂件也会消失"就是它自己造成的干扰。新层级层不需要它：owner 挂在桌面图标视图上、窗口不参与前台争夺，没有那个指针可修。
- **`detach()` 不隐藏窗口**：切换层级模式（托盘 / 设置面板改 `mode`）会 `attachLayer()` → 先 `detach()` 再重新 `attachToDesktop()`；旧实现里 `detach()` 调了 `SW_HIDE`，结果**切一次模式挂件就消失且没人再显示回来**。真正要隐藏只能走 `setWidgetVisible(false)`。
- **"贴桌面 + Win+D 后仍可见"要用 Owner，不是 SetParent**：`SetWindowLongPtrW(hwnd, GWLP_HWNDPARENT(-8), <桌面图标视图>)` —— owned 窗口恒在 owner 之上、不随 Win+D 隐藏或最小化，同时仍是顶层窗口（拖动/鼠标/坐标都正常）。`SetParent` 成 WorkerW 子窗口会被桌面图标压在下面，且拖动坐标错乱。owner 的**写入/校验/还原**细节见上方"桌面 owner"条目。
- **不要给挂件静息态戴 `WS_EX_NOACTIVATE`**：不可激活的窗口会被系统跳过原生 move loop，而 `-webkit-app-region: drag` 又依赖这条 move loop —— 戴上就等于"挂件拖不动"。想要"点击不抢前台"就只能走"悬停/命中测试时临时摘样式"那条路（需要 `WM_NCHITTEST` 子类化），代价与复杂度都不低；本项目选择不戴。`resting.ts` 里仍保留 `setNoActivate()` 原语，`suspendRestingStyle()` 会兜底清一次，防止将来有人又加回去。
- **Electron 里拖动窗口用 `-webkit-app-region: drag`**（Chromium 内建 `WM_NCHITTEST → HTCAPTION`，真实鼠标输入、跟手、不丢事件），交互控件加 `no-drag`。从主进程 `SendMessageW(hwnd, WM_NCLBUTTONDOWN, HTCAPTION)` 在 Electron 上实测**不生效**（渲染层 DOM 事件也会被 drag 区域吞掉，两者恰好构成自然降级：drag 生效时走原生，失效时走自实现循环）。
- 自实现拖动/缩放分支（WorkerW 子窗口用）必须双兜底：渲染层 `setPointerCapture` + 主进程 `GetAsyncKeyState(VK_LBUTTON)`。
- **拖动/缩放期间必须 `layer.pause()`**：owner 巡检会在拖动中途重挂 owner、和拖动抢 z-order（旧实现是每秒 `SetWindowPos(HWND_BOTTOM)`，已删除）。
- Windows 侧排查可用 WSL interop 直接调 `cmd.exe` / `powershell.exe`，但**参数里的引号与反斜杠会被 interop 再处理一次**：把逻辑写进 `.ps1`/`.bat` 再执行，不要在 `cmd /c` 里堆嵌套引号（`tasklist /FI "IMAGENAME eq x"` 这种就会解析失败）。`.ps1` 用 Windows PowerShell 5 执行时按 ANSI 读取，**脚本内容必须是纯 ASCII**（含中文注释会因引号配对错乱而解析失败）。
- **改用户 `settings.json` 之前必须先停掉应用**：运行中的实例会在 `moved`/`resized` 等时机 `saveSettings()` 回写，而 `Stop-Process` 是强杀、退出路径不保证执行 —— 先改文件再杀进程，改动会被旧实例的内存值覆盖（实测："恢复挂件位置"这一步就这么白做了一次，`941×719` 被写回成 `819×535`）。正确顺序：**stop → 改 → start**（脚本见 `.tools/stop.ps1` / `.tools/start.ps1`）。
- **合成鼠标输入的三个坑**（本机验收反复踩到，用 `.tools/` 下的脚本时注意）：
  1. `SetCursorPos` 只挪光标、**不产生鼠标输入消息** —— 所以它测不出 hover（`:hover` 不触发）、也测不出点击；要真实输入得用 `mouse_event(MOUSEEVENTF_MOVE|MOUSEEVENTF_ABSOLUTE, x, y, ...)`，坐标是按主屏归一化到 0..65535 的（`x = 虚拟x * 65535 / 虚拟宽`）。
  2. **PowerShell 不是 DPI 感知进程**：`GetWindowRect` / `SetCursorPos` 用的是虚拟坐标，而应用日志里的 `rect` 是物理坐标（本机 2560×1600 / 缩放 150% → 虚拟 1707×1067，差 1.5 倍）；混用会得到"窗口没动/hover 没反应"的假结论。`CopyFromScreen` 反而吃物理坐标。
  3. 截图别赌坐标：直接全屏 `CopyFromScreen(0,0,2560,1600)`，再按日志里的物理 rect 裁剪 —— 局部截图一旦坐标偏一点，就会截到别的窗口而误判。
  - 另外 `-webkit-app-region: drag` 的区域在 Chromium 里算**非客户区**，DOM 收不到 hover/mouseover；所以"悬停显示"的控件不要只挂在拖拽区上（挂件现在的做法是：hover 判定挂在 `.widget-shell`，而 `.widget-bar` 是拖拽区，实测悬停内容区即可让整壳 hover 生效）。

## 注意事项

- 仓库 Public：fixtures 与文档中不得出现学号、姓名、cookie、token 等任何个人凭据。
- **不做** 1 系统自动登录（SSO 带短信增强，塞进桌面客户端不划算）：改为用户手动粘贴 Cookie，主进程 `main/tongji.ts` 发起请求并探测接口路径。Cookie 存 `credentials.json`，**任何日志都不得打印 Cookie 内容**。
- 窗口默认「桌面层 + 静息」：Owner 设为桌面图标视图 `SHELLDLL_DefView`（Win+D 后仍可见），静息落点按前台窗口三选一（见"层级层结构"）；`wallpaper`（WorkerW 子窗口）与纯置底作为可切换/回退模式保留，切换失败必须自动回退，不能黑屏。
- 拖动用 `-webkit-app-region: drag`（见上）；静息态**不戴** `WS_EX_NOACTIVATE`（戴了拖不动，见"易错知识点"），交互期只做"临时浮起 + 结束后重新落点"。

## C# / WinUI 线（2026-09-15 起）

> **常驻规则（2026-09-15 用户明确定下）：今后前端一律以 DeskBox 为参照实现。**
> 遇到"观感/交互与 DeskBox 不一致"时，默认动作是**去 `.refs/DeskBox` 找它怎么做的**，
> 而不是自己从 Win32/WinUI 文档推。本轮四次返工（材质、白边→黑带、边缘缩放失效、缩放光标）
> 全都是"自己想当然"导致的，而四次的答案都写在 DeskBox 里。
> **边界仍是许可**：`.refs/DeskBox` 是 **GPL-3.0-only** 只读副本，只能提取
> "用了哪些 API / 什么机制"这类**事实**，不得抄代码、注释、文档正文、美术资源
> （依据 `docs/deskbox-refactor-assessment.md`）。实践口径：**先查它、后自己写**。

技术栈正在从「Electron 独占」转向「WinUI 外壳 + 核心逻辑双实现」。原因是材质：Electron 的三条系统材质路径实测全拿不到 DeskBox 那种质感（DWM 对"从未被激活的窗口"一律降级成近黑平色），只有 WinUI 的 `MicaController` + `SystemBackdropConfiguration`（可强制 `IsInputActive`）能拿到。

### 三个工程的分工（改动时必须守住）

| 位置 | TFM | 能跑在哪 | 职责 |
|---|---|---|---|
| `dotnet/TjtCore` | `net10.0` | Linux + Windows | 模型 / 周次 / 冲突 / 布局 / 时间 / 适配器（TS `packages/core` 的移植） |
| `dotnet/Tjt.Widget` | `net10.0` | Linux + Windows | 挂件视觉层：几何、色块染色、名称分档、呈现模型（**纯计算，不得引用 WinUI/Win32**） |
| `dotnet/Tjt.App` | `net10.0-windows10.0.22621.0` | **只能 Windows** | WinUI 外壳：窗口、材质、Win32、照坐标摆控件 |

- `dotnet/TjtTimetable.slnx` = 前两个（Linux 也要能 `dotnet test`）；`dotnet/TjtTimetable.Windows.slnx` = 第三个。
  **不要**把 `Tjt.App` 并进前者，否则 Linux/CI 的构建整片失败。
- 新逻辑优先下沉到 `Tjt.Widget`：那里能在 WSL 上编译 + 单测，反馈最快；外壳里只留"必须在 Windows 上跑"的东西。
- 视觉规则的**唯一真源**仍是 `apps/desktop/src/renderer/shared/TimetableBoard.vue`（`blockRect` / `blockFontSize` / `blockName` / `tintStyle`）；C# 侧 `Tjt.Widget` 逐档对齐，改一边必须同步另一边，`BoardVisualTests` 有对照断言。

### 在 Windows 本机构建（WSL 侧编译不了 WinUI）

```bash
B64=$(python3 -c "import base64;print(base64.b64encode(open('.tools/build-winui.ps1','rb').read().decode('ascii').encode('utf-16-le')).decode())")
/mnt/c/Windows/System32/WindowsPowerShell/v1.0/powershell.exe -NoProfile -ExecutionPolicy Bypass -EncodedCommand "$B64"
```

- 脚本把源码 robocopy 到 `C:\tjt-tools\work`（**反向拉取**：Windows 侧读 `\\wsl.localhost\...`，不是 WSL 写 `/mnt/c`），
  再用 `C:\tjt-tools\dotnet\dotnet.exe`（.NET 10 SDK，装在工作区里，不污染系统）构建；加 `-RunSmoke` 还会跑冒烟自检。
  日志在 `C:\tjt-tools\build.log`。
- **WSL 沙箱把 `/mnt/c` 挂成只读**：`touch /mnt/c/...` 会 `Permission denied`，robocopy 到 `/mnt/c` 也失败。
  一切对 Windows 盘的写操作都要走 PowerShell（interop 或 `-EncodedCommand`），别从 WSL 直接写。
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
  - **第一版走"自答 `WM_NCHITTEST` + 系统原生缩放循环"**，命中测试全对，但**真机手动验收判定"边缘缩放失效"** ——
    因为原生循环要求窗口带 `WS_THICKFRAME`，而那个样式位正是那圈 10px 非客户区框的来源。
    **两者不可兼得**：`IsResizable=true` → `frame=10,10`（白边/黑带），`false` → `frame=0,0`（无框但拖不动）。
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
  - 抓取带仍是 6px、仍用**内缩矩形**判边（`x < left + band`，
    不是 `x - left < band`，后者会把窗口左侧外面整片桌面算成抓取带 —— 有单测钉住）。
  - **⚠️ 光标条必须显式 `Grid.SetRow(strip, 1)`**：`BoardRenderer` 的内容根是两行 Grid
    （row 0 = Auto 头部条，row 1 = Star 滚动区），子元素**默认落在 row 0**。
    把"底部对齐的下光标条"放进 Auto 行，Auto 行为了容纳它会被迫长到整窗高
    （真机逐子元素探针实测：`row0` 长到 606，Star 行只剩 138）——表现为**头部条被推到下方、
    上面一大片空白**。这是本轮"布局忽然坏了"的真凶，靠"去掉条对比截图"+"逐子元素打印几何"
    两步定位。教训：往有两行的 Grid 里加覆盖元素，先想清楚它落在哪一行。
  - **已知取舍**：顶部光标条（6px，`HorizontalAlignment=Stretch`）压在顶部条上，
    会吃掉那 6px 的 pointerdown —— 即"贴着窗口最上沿那一条按下去拖窗口"可能不响应。
    这是 DeskBox 也有的同类折中（它的 resize 边框同样盖在标题栏上）；真觉得别扭再给拖拽区让出 6px。
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
  - **症状两连**：先是一圈 `#F3F3F3` **白边**；把框区改成"当玻璃"后又变成一条 `#2B2B2B` **黑带**。
    两种都不是渲染层画的 —— 用一次性探针（给 `BoardRenderer` 的内容根铺洋红）量出：
    那圈在**客户区之外**，XAML 碰不到它。
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
    右边 **刷新** 与 **⋯ 溢出菜单**（设置 / 重新载入课表 / 恢复默认位置 / 贴桌面层勾选 / 隐藏 / 退出）。
    图标用 Segoe Fluent Icons 字形（`Rendering/IconGlyph.cs`），跟主题色走、任意 DPI 都清晰。
    动作经 `WidgetActions`（渲染层只发意图，逻辑留在 `MainWindow.BuildActions()`）。
  - **托盘图标**（`Win32/TrayIcon.cs`）：`Shell_NotifyIcon` + 消息专用窗口（`HWND_MESSAGE`，离屏、
    不参与层级）+ 原生右键菜单（`TrackPopupMenu(TPM_RETURNCMD)` 同步取回选中项，不需要消息分发）。
    左键单击 = 显示挂件；菜单 = 显示 / 设置 / 重新载入 / 恢复位置 / 贴桌面层勾选 / 退出。
    WASDK 1.8 没有托盘 API，所以直接 P/Invoke；图标是 `Assets/app.ico`（运行时 `LoadImage` 加载）。
  - **设置窗口**（`SettingsWindow.xaml(.cs)` + `Rendering/SettingsView.cs`）：左侧 `NavigationView`
    导航（常规 / 外观 / 关于）+ 卡片行（左图标、标题、说明，右控件），照 DeskBox 那套。
    改动**即时生效并落盘**（没有保存按钮）；**材质**例外 —— 它只在窗口创建时确定，
    界面上明确写"重启后生效"。
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
