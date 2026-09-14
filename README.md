# TJDesktopTimetable · 同济桌面课表小组件

把同济大学课表以半透明色块网格**固定在桌面上**的 Windows 小组件。置底、不抢焦点、可拖动缩放；数据手动获取一次即可；架构上把"学校适配器"与"窗口 / 渲染"解耦，新增一所学校只需加一个适配器文件。

> 视觉与交互基准：`select_preview.html`（同济专业课表 7 列 × 11 节网格、色块、单双周差分、同格多块并排）。

## 特性

- **贴桌面**：窗口挂到桌面图标层（Owner = `SHELLDLL_DefView`）——浮在桌面图标之上、被普通窗口覆盖，**按 Win+D 显示桌面后依然可见**；可拖动（拖标题栏）、可缩放，位置尺寸与显示器记忆。
- **一眼看懂今天**：当前教学周高亮、今日列高亮、单周 / 双周 / 全部周次过滤、非全周课用条纹虚线区分。
- **导入即用**：粘贴或导入同济课表 JSON（专业课表 + 校历）→ 自动归一化 → 勾选自己的教学班（自动冲突拦截）→ 保存。
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
                                                   └─ 管理窗口（导入 / 勾选 / 外观）
```

```
packages/core/          @tjt/core：模型、周次掩码、冲突检测、网格布局、时间推算、适配器注册表
  src/adapters/         每个学校一个文件；tongji-major（已实现）、tongji-student（占位）、generic（通用 JSON/ICS）
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

1. 浏览器登录 [1 系统](https://1.tongji.edu.cn/)，打开个人专业课表页面（`/StudentMajorTimeTable`）。
2. F12 → Network，抓取课表接口响应（`timetable/major`，约 147 条排课记录）与校历接口响应，各存一份 JSON。
3. 在小组件"管理窗口 → 导入"里粘贴或选择这两个文件。
4. 勾选自己实际要上的教学班（平行班会自动做时间冲突拦截），保存。

> 拿到"个人已选课表"接口的抓包结果后，只需在 `packages/core/src/adapters/` 里补字段映射，界面与窗口层无需改动。

## 扩展其他学校

见 [`docs/adapter-guide.md`](docs/adapter-guide.md)：实现一个 `SchoolAdapter`（`detect` + `parse`）→ 在 `registry.ts` 注册 → 放一份 fixture → 写一个 spec。核心算法与 UI 全部复用。

## 路线图

- [x] M1 核心库：统一模型、周次/冲突/布局/时间算法、同济适配器 + 单测（50 用例全绿）
- [x] M2 管理窗口：导入面板、教学班勾选、冲突拦截、外观设置
- [x] M3 桌面挂件窗口：置底、拖动缩放、托盘、开机自启
- [x] M4 Windows 交叉打包出 `win-unpacked`、CI（typecheck + test）
- [ ] M5 Windows 真机验收与细节打磨
- [ ] 后续：一键从 1 系统拉取、ICS/图片导出、多校适配器

## 许可

MIT © 2026 gzy31007
