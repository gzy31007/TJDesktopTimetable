# 架构

## 总览

```
                    ┌──────────────────────── 手动导入（粘贴 / 文件） ───────────────────────┐
                    ▼                                                                       │
  同济 1 系统 JSON   排课工具导出 HTML   其他学校 JSON                                        │
        │                  │                 │                                              │
        └──────────────────┴─────────────────┘                                              │
                           ▼                                                                │
              packages/core/src/adapters/*  （适配器注册表 + detect 打分）                    │
                           ▼                                                                │
              @tjt/core 统一模型（Timetable / Term / Course / Session）                      │
                  │              │                │                                        │
           周次掩码算法      冲突检测算法       网格布局 / 时间推算                          │
                  └──────────────┴────────────────┘                                        │
                           ▼                                                                │
                    electron-store 落盘（timetable.json / settings.json）                    │
                           ▼                                                                │
   ┌──────────────────────────────── apps/desktop ────────────────────────────────┐         │
   │ main（窗口 / 托盘 / Win32 置底 / IO）  ←→  preload（contextBridge IPC）        │         │
   │                                             ▼                                │         │
   │ renderer（Vue 3）: 桌面挂件窗口（TimetableBoard） + 管理窗口（导入 / 外观）│         │
   └──────────────────────────────────────────────────────────────────────────────┘         │
                                                                                            │
                    WorkerW / HWND_BOTTOM 置底 ◀── koffi 调用 user32.dll ──────────────────┘
```

## 分层与依赖规则

| 层 | 位置 | 允许依赖 | 职责 |
|---|---|---|---|
| 核心 | `packages/core/src` | 仅标准库 | 模型、周次、冲突、布局、时间、导入管线、适配器 |
| 主进程 | `apps/desktop/src/main` | Electron、Node、koffi、`@tjt/core` | 窗口生命周期、托盘、置底、文件读写，**不含课表业务逻辑** |
| 预加载 | `apps/desktop/src/preload` | Electron（`contextBridge`） | 暴露类型化 IPC API |
| 渲染 | `apps/desktop/src/renderer` | Vue、`@tjt/core` | 展示与交互，**禁止直接 import electron** |

规则由 `AGENTS.md` 强制，好处是：核心算法可在 Node / 浏览器 / 测试中直接跑，未来可以把同一套 UI 复用到 Web 版。

## 导入管线

```
输入（粘贴文本 + N 个文件）
  → inputTexts()                     收集所有文本片段
  → registry.best()                  各适配器 detect() 打分（0..1），最高分胜出
  → adapter.parse()                  → ImportResult { term, courses | candidates, preselect, diagnostics }
  → materializeTimetable(result)      → Timetable（个人课表直接落盘）
  → 落盘 + 通知渲染层刷新
```

`ImportResult` 有意区分两种语义：

- `courses`：**个人课表**——同济 1 系统「我的课表」走这条路，导入即用；
- `candidates` + `preselect`：**候选池**（平行班清单，需要用户勾选）。这是留给其他学校/培养计划类数据源的通用能力，本项目当前 UI 不使用。

同济适配器已从"专业课表（`timetable/major`，需勾选）"切换为"个人课表（导入即用）"；数据获取支持本地 JSON 与 Cookie 抓取两条路。

## 导入管线（WinUI 线，2026-09-16）

C# 侧把同一条管线复刻了一遍，分层与 TS 侧一一对应：

```
SettingsWindow「导入」页 / 托盘「导入课表…」/ 挂件 ⋯ 菜单   （Rendering/ImportPage.cs）
        │  Intent（粘贴文本 / 文件 / 适配器 id / 请求全文）
        ▼
ImportService（Tjt.App/Data）      编排：导入 → 落成 Timetable → 交回外壳落盘 + 重画
        │                        抓取：TongjiFetcher（HttpClient，30s 超时；Cookie 只进内存与 credentials.json）
        ▼
ImportPipeline（Tjt.Core）        registry.best() → adapter.Parse() → MaterializeTimetable()
        │                        纯函数部分的移植：HttpRequest / TimetableJson / TongjiResponseProbe
        ▼
AppHost.Load()                   载入顺序：--fixture → 用户导入的 timetable.json → fixtures/… → 内置样例
```

- **`ImportService` 不认识窗口**（三个回调：`apply` / `reload` / `describe`），设置窗口也不认识 `MainWindow`；
  两端都只依赖接口，窗口层改动不会牵动导入逻辑。
- **落盘文件两端同形**：`timetable.json`（camelCase，对齐 TS 的 `Timetable`）与
  `credentials.json`（`{ tongjiRequest, savedAt }`）在 Electron 线与 WinUI 线之间可以互相读 ——
  实测 WinUI 侧直接读到了 Electron 侧 2026-09-14 导入的那份课表。
- 验收脚本：`.tools/verify-import.ps1`（载入顺序 / 导入落盘 / 读回 / 四个设置页构建 /
  本地合成服务上的完整抓取链路与失败负例）。

## 窗口层设计

桌面挂件（`main/windows/widget.ts`）：

- `frameless + transparent + skipTaskbar + no shadow`，尺寸与位置持久化到 `settings.json`，记录所在显示器并在显示器变化时回收。
- 默认 `desktop` 模式：`WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW` + `SetWindowPos(HWND_BOTTOM)` 置底，
  1 秒保险定时器在"非拖动、非管理窗口打开"时重新压到底部，"显示桌面"导致被隐藏时 `ShowWindow(SW_SHOWNA)` 复位。
- 可选 `wallpaper` 模式：WorkerW 注入（`Progman → 0x052C → SHELLDLL_DefView → FindWindowEx('WorkerW') → SetParent`），
  即"贴在桌面图标之下"的真壁纸层；注入失败自动回退 `desktop`。
- 拖动用 IPC + `screen.getCursorScreenPoint()`（避免与 `WS_EX_NOACTIVATE` 抢焦点冲突）；
  提供 `setIgnoreMouseEvents(true, { forward: true })` 的"点击穿透"锁定模式。
- `koffi` 以纯 FFI 方式调用 `user32.dll`，不需要 node-gyp / electron-rebuild，跨 Electron 版本稳定。

## 存储

| 文件 | 内容 |
|---|---|
| `timetable.json` | 最终课表（`Timetable`，含 source 元信息），可直接手工替换 / 备份；**两端同形，可互读** |
| `settings.json` | 外观与窗口状态：位置尺寸、模式、周次过滤、显示周末、透明度、开机自启、适配器偏好（**两侧字段不同形**：WinUI 线用 PascalCase 与自己的枚举） |
| `credentials.json` | 上次粘贴的抓取请求全文（含 Cookie）；**任何日志都不得打印其内容**，只记长度 |

## 构建与打包

| 目标 | 命令 | 说明 |
|---|---|---|
| 开发 | `pnpm dev` | electron-vite，三端 HMR |
| 渲染层预览 | `pnpm dev:web` | 纯浏览器 + mock 数据，调视觉用（WSL 内可跑） |
| Windows 免安装 | `pnpm dist:win` | WSL 内交叉打包 `win-unpacked`（无需 wine） |
| Windows 安装包 | `.github/workflows/build-win.yml` | windows-latest 上打 NSIS，产物为 artifact |

交叉打包要点：`pnpm-workspace.yaml` 的 `supportedArchitectures` 同时声明 `linux` 与 `win32`，
让 `koffi` 的 `@koromix/koffi-win32-x64` 在 WSL 里也能装上；`electron-builder.yml` 需要
`asarUnpack: ["**/*.node"]`，否则原生模块加载失败。

## 参考项目

| 项目 | 借鉴点 |
|---|---|
| [Felix-au/DeskX-Wallpaper-Engine](https://github.com/Felix-au/DeskX-Wallpaper-Engine) | `koffi` + WorkerW 注入流程、置底 fallback、electron-forge/builder 打包组织 |
| [RyugaLDragoMeteor/ForeverPapere](https://github.com/RyugaLDragoMeteor/ForeverPapere) | Electron 贴桌面层的 TypeScript 组织方式 |
| [nicocodes9/desktop-clock-widget](https://github.com/nicocodes9/desktop-clock-widget) | 透明无边框小挂件的窗口参数与打包配置 |
| [ClassIsland/ClassIsland](https://github.com/ClassIsland/ClassIsland) | 课表数据结构、"课表源"抽象与托盘交互 |
| [XingHeYuZhuan/shiguangschedule](https://github.com/XingHeYuZhuan/shiguangschedule) | 多校教务导入的适配器组织方式 |
| [Grible/timetable.js](https://github.com/Grible/timetable.js) / [zfman/TimetableView](https://github.com/zfman/TimetableView) | 网格渲染与重叠课程并排处理 |
| [VincentZyuApps/koishi-plugin-course-schedule](https://github.com/VincentZyuApps/koishi-plugin-course-schedule) | 通用课表导入格式（WakeUp / ICS / JSON）取舍 |

## 决策记录

| 决策 | 选择 | 理由 |
|---|---|---|
| Win32 绑定方式 | `koffi` | 纯 FFI、预编译分平台包、无需 electron-rebuild；`electron-as-wallpaper` 已多年未更新 |
| 桌面呈现默认模式 | `HWND_BOTTOM` 置底（可交互） | 满足"固定在桌面且能点"的诉求；WorkerW 作为可切换模式保留 |
| 核心算法放独立包 | `@tjt/core` | 零平台依赖 → 可测试、可复用到 Web、适配器扩展不影响 UI |
| 颜色分配 | 课程名稳定哈希 | 基准版按出现顺序取色，换导入顺序就变色 |
| 单双周掩码 | 按 `totalWeeks` 生成 | 基准版硬编码 `0x5555/0xAAAA`，只对 16 周成立 |
| 时间换算 | 纯日期序号（UTC 日号） | 校历时间戳是北京时间午夜，用本地 Date 会跨时区偏移一天 |
