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
  - **挂件窗口材质（最终选型，2026-09-14 二次定稿）**：`transparent: false` + `backgroundMaterial: 'mica'` + DWM 圆角（`main/win32/dwm.ts` 的 `applyRoundedCorners`）+ **不透明实色底**（浅 `#f3f3f3` / 深 `#202020`，随主题经 `applyWidgetTheme()` 切换）。渲染层 `--glass-shell: transparent` 把底色让给系统材质，圆角不自绘（避免双圆角），`hasShadow: false`（去掉窗口投影那层外部立体感）。
  - **为什么放弃 Acrylic（重要）**：Acrylic 在"Win+D 隐藏 → 恢复"之后 **DWM 合成会失效**——实测窗口 `IsWindowVisible` 为真、owner 正确、z-order 也正确（诊断字段 `coveredByShell:false`、排位在 Progman 之前），但屏幕上就是不出现，Electron 侧无法感知也修不好（`webContents.invalidate()` + bounds 抖动只能治一时）。它另有两个固有代价：失焦被 DWM 切成不活跃（变灰发蓝）、必须 `transparent: true` 而透明窗口拿不到 DWM 圆角。
  - **非透明窗口不能用透明底色**：`transparent: false` 时 `backgroundColor` 的 alpha 会被忽略，给 `#00000000` 得到黑底（材质画在黑上 = 一块死色）。必须给不透明实色。
  - **材质只在窗口创建时声明有效**：`backgroundMaterial` 写在 `new BrowserWindow({...})` 里才生效。运行时再设（Electron 的 `setBackgroundMaterial`，或 koffi 直写 `DWMWA_SYSTEMBACKDROP_TYPE`）即使 `DwmGetWindowAttribute` 读回 `accepted: 3` 也不出模糊。
  - **`tabbed`（Mica Alt）备选**：任务管理器用的就是它，比 mica 对比度更高、底纹更明显。要更重的层次感时把 `backgroundMaterial` 换成 `'tabbed'` 即可（Win11 23H2+，低于该版本 DWM 会忽略并退化成 mica/纯色）。
  - **不要用 `SetWindowRgn` 给透明窗口裁圆角**：实测拖动缩放约 12 秒后主进程**无日志直接重启**（原生层崩溃）；且 `transparent: true` 本身就拿不到 DWM 圆角。对应 DeskBox（WinUI 3 `MicaController`/`DesktopAcrylicController` + `SystemBackdropConfiguration`）的等价做法就是"非透明窗口 + `backgroundMaterial` + DWM 圆角属性"。
  - 管理窗口用主进程 `backgroundMaterial: 'mica'` + 自绘标题栏（`titleBarOverlay`，右侧留 `clamp(138px, 11vw, 190px)` 给系统按钮，深浅主题经 `window:titlebar-theme` 同步）。
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
- **桌面 owner 用 Progman，不要用 SHELLDLL_DefView**（2026-09-14 修正）：owner 取 `GetShellWindow()` = Progman，对齐 WitchDrawer 的 `DesktopShellHost.ResolveOwner`；早期用 `SHELLDLL_DefView`（Progman 的**子窗口**）在 Win+D 路径下 z-order 行为不同，实测会"恢复了却看不见"。DefView 仅作回退。
- **`SetWindowPos` 的 `hWndInsertAfter` 是"插到该窗口之后（z-order 更低）"，不是"上方"**：曾误把 owner 传进去想让挂件"贴着桌面之上"，结果把它插到 Progman 下面被桌面盖住。owned 窗口本来就恒在 owner 之上，置底用 `HWND_BOTTOM` 即可。
- **owner 检测必须和"当前期望的 owner"比较**：换 owner 目标（DefView→Progman）时忘了同步 `ownerLost()`，会每秒误判"丢失"并重挂、反复搅动 z-order（日志刷屏 `桌面层 owner 丢失，重新挂载`）。
- **鼠标按下期间要临时摘掉 Shell owner**（WitchDrawer 的 `SuspendDesktopOwnershipForMouseInput`）：否则 Explorer 会把被点到的挂件记成 Progman 的 "last active popup"，之后 Win+D 会去激活挂件而不是显示桌面。摘除期间 `ownerLost()` 要让路，别和"交互结束恢复"打架。
- **"贴桌面 + Win+D 后仍可见"要用 Owner，不是 SetParent**：`SetWindowLongPtrW(hwnd, GWLP_HWNDPARENT(-8), <桌面宿主>)`，owner 取 Progman（见上条）—— owned 窗口恒在 owner 之上、不随 Win+D 消失，同时仍是顶层窗口（拖动/鼠标/坐标都正常）。`SetParent` 成 WorkerW 子窗口会被图标压在下面，且拖动坐标错乱。（做法对齐 DeskBox 的 DesktopPinned 模式）
- **`WS_EX_NOACTIVATE` 会让窗口拖不动**：不可激活的窗口会被系统跳过原生 move loop。本项目已移除该样式，「不打扰」由"贴桌面层 + 置底"承担；这条与"点挂件不抢焦点"存在取舍，不要再硬塞回来。
- **Electron 里拖动窗口用 `-webkit-app-region: drag`**（Chromium 内建 `WM_NCHITTEST → HTCAPTION`，真实鼠标输入、跟手、不丢事件），交互控件加 `no-drag`。从主进程 `SendMessageW(hwnd, WM_NCLBUTTONDOWN, HTCAPTION)` 在 Electron 上实测**不生效**（渲染层 DOM 事件也会被 drag 区域吞掉，两者恰好构成自然降级：drag 生效时走原生，失效时走自实现循环）。
- 自实现拖动/缩放分支（WorkerW 子窗口用）必须双兜底：渲染层 `setPointerCapture` + 主进程 `GetAsyncKeyState(VK_LBUTTON)`。
- **拖动/缩放期间必须 `layer.pause()`**：置底保险定时器每秒 `SetWindowPos(HWND_BOTTOM)` 会和拖动抢 z-order。
- Windows 侧排查可用 WSL interop 直接调 `cmd.exe` / `powershell.exe`，但**参数里的引号与反斜杠会被 interop 再处理一次**：把逻辑写进 `.ps1`/`.bat` 再执行，不要在 `cmd /c` 里堆嵌套引号（`tasklist /FI "IMAGENAME eq x"` 这种就会解析失败）。`.ps1` 用 Windows PowerShell 5 执行时按 ANSI 读取，**脚本内容必须是纯 ASCII**（含中文注释会因引号配对错乱而解析失败）。

## 注意事项

- 仓库 Public：fixtures 与文档中不得出现学号、姓名、cookie、token 等任何个人凭据。
- **不做** 1 系统自动登录（SSO 带短信增强，塞进桌面客户端不划算）：改为用户手动粘贴 Cookie，主进程 `main/tongji.ts` 发起请求并探测接口路径。Cookie 存 `credentials.json`，**任何日志都不得打印 Cookie 内容**。
- 窗口默认「桌面层 + 置底」：Owner 设为桌面宿主 Progman（Win+D 后仍可见），z-order 压在普通窗口之下；`wallpaper`（WorkerW 子窗口）与纯置底作为可切换/回退模式保留，切换失败必须自动回退，不能黑屏。
- 拖动用 `-webkit-app-region: drag`（见上）；点击挂件会让它获得焦点，这是移除 `WS_EX_NOACTIVATE` 的代价，属于有意取舍。
