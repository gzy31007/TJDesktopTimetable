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
- 测试：core 的每个公开函数都要有单测；同济适配器必须过黄金测试（fixture 解析结果与 `tongji-2026-1-major.expected.json` 逐条一致）。
- 视觉基准：`/root/trivial/tongji-timetable/select_preview.html`（网格参数、色板、条纹特殊块、单双周并排、冲突红闪）。改渲染前先对照它。
- 代理：WSL 内装依赖优先用国内镜像直连（快 30 倍，CI 也适用）：
  `pnpm install --registry=https://registry.npmmirror.com`
  只有推送到 GitHub / 拉 GitHub 资源时才用代理：`export https_proxy=http://127.0.0.1:7897 http_proxy=http://127.0.0.1:7897`
- 打包：`pnpm dist:win` → WSL 内交叉出免安装 `apps/desktop/dist/win-unpacked`（**不需要 wine**）；NSIS 安装包交给 `.github/workflows/build-win.yml`。

## 易错知识点

- 同济 `timetable/major` 返回的是**专业培养计划里的全部平行教学班**（实测 147 条排课记录 / 128 个教学班），**不是学生已选课表**，因此导入后必须走"勾选"流程；只有 `preselect` 字段可以跳过勾选。
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
- **窗口拖动优先用 Windows 原生 move loop**：`ReleaseCapture()` + `SendMessageW(hwnd, WM_NCLBUTTONDOWN, HTCAPTION, 0)`，跟手性与系统窗口一致，且系统自动处理鼠标捕获与松手。自实现"轮询光标 + `setPosition`"有两个硬伤：指针移出窗口就收不到 `mouseup`（拖动粘住），以及每帧 `setPosition` 等 DWM 合成的滞后感。自实现分支（WorkerW 子窗口用）必须双兜底：渲染层 `setPointerCapture` + 主进程 `GetAsyncKeyState(VK_LBUTTON)`。
- **拖动/缩放期间必须 `layer.pause()`**：置底保险定时器每秒 `SetWindowPos(HWND_BOTTOM)` 会和拖动抢 z-order，表现为拖到一半窗口被压回去。
- `WS_EX_NOACTIVATE` 窗口进入原生 move loop 时会短暂激活（系统行为）；拖动结束后 `layer.resume()` 会把窗口重新压到底部，不影响"平时不抢焦点"。
- Windows 侧排查可用 WSL interop 直接调 `cmd.exe` / `powershell.exe`，但**参数里的引号与反斜杠会被 interop 再处理一次**：把逻辑写进 `.ps1`/`.bat` 再执行，不要在 `cmd /c` 里堆嵌套引号（`tasklist /FI "IMAGENAME eq x"` 这种就会解析失败）。`.ps1` 用 Windows PowerShell 5 执行时按 ANSI 读取，**脚本内容必须是纯 ASCII**（含中文注释会因引号配对错乱而解析失败）。

## 注意事项

- 仓库 Public：fixtures 与文档中不得出现学号、姓名、cookie、token 等任何个人凭据。
- 本阶段**不做** 1 系统自动登录抓取（SSO + 短信增强链路复杂）；`adapters/tongji-student.ts` 只留占位接口，等抓包结果再补字段映射。
- 窗口默认"置底 + 可交互"（`HWND_BOTTOM`）；`WorkerW` 壁纸层作为可切换模式保留，切换失败必须自动回退，不能黑屏。
