# TJDesktopTimetable · 同济桌面课表小组件

把同济大学课表以半透明色块网格**固定在桌面上**的 Windows 小组件。置底、不抢焦点、可拖动缩放；数据手动获取一次即可；架构上把"学校适配器"与"窗口 / 渲染"解耦，新增一所学校只需加一个适配器文件。

> 视觉与交互基准：`select_preview.html`（同济专业课表 7 列 × 11 节网格、色块、单双周差分、同格多块并排）。

## 特性

- **贴桌面**：窗口挂到桌面图标层（Owner = `SHELLDLL_DefView`）——浮在桌面图标之上、被普通窗口覆盖，**按 Win+D 显示桌面后依然可见**；可拖动（拖标题栏）、可缩放，位置尺寸与显示器记忆。
- **一眼看懂今天**：当前教学周高亮、今日列高亮、单周 / 双周 / 全部周次过滤、非全周课用条纹虚线区分。
- **导入即用**：粘贴 Cookie 从 1 系统一键获取个人课表，或导入本地 JSON；解析成功立即上桌面，**无需挑选教学班**。
- **可扩展**：`@tjt/core` 是纯 TypeScript，零 Electron 依赖；适配器注册表 + 统一课表模型，未来可加其他学校，也可复用到 Web。
- **离线**：所有数据落本地 JSON，不联网、不上传。

## 架构

```
导入 JSON ──▶ adapters/<school>.ts ──▶ @tjt/core 统一模型 ──▶ 本地存储
                                          │                      │
                                          │                      ▼
                                   周次/冲突/布局算法      Electron 主进程
                                          │                      │
                                          └──────▶ Vue 渲染层 ◀──┘
                                                   ├─ 桌面挂件窗口（置底、可交互）
                                                   └─ 管理窗口（导入 / 外观）
```

```
packages/core/          @tjt/core：模型、周次掩码、冲突检测、网格布局、时间推算、适配器注册表
  src/adapters/         每个学校一个文件；tongji-student（同济个人课表）、preview-html（排课工具导出）、generic（通用 JSON）
apps/desktop/           Electron 应用：main（窗口/托盘/Win32）、preload（IPC 桥）、renderer（Vue 3）
docs/                   architecture.md · data-model.md · adapter-guide.md
```

## 快速开始

```bash
# 依赖（国内镜像直连，比走代理快约 30 倍；CI / 海外网络可去掉 --registry）
pnpm install --registry=https://registry.npmmirror.com

pnpm test          # 核心逻辑单测（含同济真实数据黄金测试，50 个用例）
pnpm typecheck     # 全仓类型检查（core tsc + desktop vue-tsc）
pnpm dev           # Electron 开发态（需 Windows 侧运行以验证窗口层级）
pnpm dev:web       # 纯浏览器预览渲染层（WSL 内可跑，默认 http://127.0.0.1:5199）
pnpm dist:win      # WSL 内交叉打包 Windows 免安装版 → apps/desktop/dist/win-unpacked
```

> 在 WSL 里做视觉调试：`pnpm dev:web` 后访问 `http://127.0.0.1:5199/widget/index.html`（挂件）
> 与 `/manage/index.html`（设置）。浏览器模式使用内置示例课表并写入 localStorage，不需要 Electron。
>
> 打包产物是免安装目录，**必须放到 Windows 本地磁盘再运行**（例如拖到桌面）：从
> `\\wsl.localhost\...` 这类 UNC 路径直接双击运行不可靠（Chromium 需要内存映射加载 pak 文件）。
>
> 首次启动没有课表时会**自动打开设置窗口**；关闭设置窗口后，课表挂件固定在桌面右下角（贴桌面层、Win+D 后仍可见）。
> 挂件靠**拖动顶部标题栏**移动（顶部条上的按钮不受影响），右下角三角手柄缩放。
> 如果感觉"双击没反应"，先看托盘图标是否存在，再查看 `%APPDATA%\TJDesktopTimetable\startup.log`
> （托盘菜单 → 查看启动日志）。上一次实例没退出时，重复双击会被单实例锁静默挡掉：
> `taskkill /F /IM TJDesktopTimetable.exe` 后再启动。
>
> 需要 NSIS 安装包时跑 GitHub Actions 的 **Build Windows** 工作流（本地打 NSIS 需要 wine）。

## 课表数据从哪来

本阶段不做自动登录抓取（1 系统 SSO + 短信验证码链路不适合放进桌面客户端）。手动获取一次：

两种方式，任选其一（**两条客户端都有同一条导入链**：Electron 的管理窗口 / WinUI 的设置窗口 →「导入课表」页）：

**A. 从 1 系统直接获取（推荐）**

1. 浏览器登录 [1 系统](https://1.tongji.edu.cn/)，打开"我的课表"页面。
2. F12 → **Network** → 刷新页面 → 找到**返回 200 且内容是课程列表**的那条请求 → 右键 → **Copy → Copy as cURL**。
   - 现在课表页调的是 `GET /api/electionservice/reportManagement/findStudentTimetab?calendarId=…&studentCode=…`
     （研究生页是 `findSchoolTimetab2`）；旧接口 `…/student/xxxx/getDataBk` 同样支持。
   - 复制错请求（例如 `schoolCalendar/detail`——那是**校历**、里面没有课程）时，程序会在探测行里如实告诉你
     它看到了什么，而不是默默失败。
3. 打开"导入课表"页，把这条请求整段粘贴进去 → 点 **获取我的课表**。
4. 程序照原样请求一次（请求里自带 Cookie 与 `x-token`）→ 解析 → 立即应用到桌面挂件；
   学期 id 直接从请求 URL 的 `calendarId` 取，所以"当前第几周"也是准的。

> 粘贴内容只保存在本机 `%APPDATA%\TJDesktopTimetable\credentials.json`，不会上传、不进日志（日志里只记长度）；
> 用完可在浏览器退出登录使其失效。粘贴请求而不是猜接口路径，是因为接口里的 `{id}` 是选课批次相关的内部 id，无法稳定构造。
> 自检用的命令行开关：`--fetch-check <请求文件>`（抓一次、把探测结果写日志、不落盘）。

**B. 本地 JSON 导入**

1. 同上抓包，把个人课表接口响应另存为 JSON（可选再存一份校历响应）。
2. "导入课表"页 → 选择 JSON 文件（或直接粘贴 JSON）→ **导入并应用**。

> 导入结果落盘在 `%APPDATA%\TJDesktopTimetable\timetable.json`，**两端同形**（camelCase，逐字段对齐 TS 的
> `Timetable`）：在 Electron 线导入的课表，WinUI 线打开就能用，反之亦然。

## 扩展其他学校

见 [`docs/adapter-guide.md`](docs/adapter-guide.md)：实现一个 `SchoolAdapter`（`detect` + `parse`）→ 在 `registry.ts` 注册 → 放一份 fixture → 写一个 spec。核心算法与 UI 全部复用。

## 路线图

- [x] M1 核心库：统一模型、周次/冲突/布局/时间算法、同济适配器 + 单测（50 用例全绿）
- [x] M2 管理窗口：导入面板（Cookie 抓取 / 本地 JSON）、外观设置
- [x] M3 桌面挂件窗口：置底、拖动缩放、托盘、开机自启
- [x] M4 Windows 交叉打包出 `win-unpacked`、CI（typecheck + test）
- [ ] M5 Windows 真机验收与细节打磨
- [x] M5+ WinUI 线（`dotnet/`）：WinUI 外壳 + `MicaController` 材质、无边框自实现拖动/缩放、贴桌面层、
      托盘与设置窗口，**以及真实课表导入**（抓取 / 本地 JSON / 适配器探测 / 诊断）
- [ ] 后续：一键从 1 系统拉取、ICS/图片导出、多校适配器

## 许可

MIT © 2026 gzy31007
