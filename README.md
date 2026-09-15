# TJDesktopTimetable · 同济桌面课表小组件

把同济大学课表以半透明色块网格**固定在桌面上**的 Windows 小组件。贴桌面层、不抢焦点、可拖动缩放；课表手动获取一次即可；架构上把"学校适配器"与"窗口 / 渲染"解耦，新增一所学校只需加一个适配器文件。

> **主线 = WinUI 3（C#，`dotnet/`）**，自 v1.0.0 起正式发布，见 [Releases](../../releases)。
> 早期的 Electron + Vue 实现（`apps/desktop/` + `packages/core/`）**已于 2026-09-16 删除** ——
> 它的黄金 fixture 搬到了 `dotnet/fixtures/`，历史代码见 git 历史（`git log --diff-filter=D -- '*apps/desktop*'`）。

## 特性

- **贴桌面**：窗口挂到桌面图标层（Owner = `SHELLDLL_DefView`）——浮在桌面图标之上、被普通窗口正常覆盖，**按 Win+D 显示桌面后依然可见**；不抢焦点。
- **无边框 + 自实现拖动/缩放**：四边四角八块热区、缩放光标、最小尺寸，位置尺寸与显示器记忆。
- **系统材质**：Mica / Mica Alt / Acrylic / 实色四种，**运行时即时切换**（不重建窗口）；深浅主题各自正确。
- **一眼看懂今天**：顶部显示「学期 · 第 N 周 · 今日 N 节」，当前周与今日列高亮，非全周课用条纹区分。
- **导入即用**：粘贴一条浏览器请求从 1 系统抓取个人课表，或导入本地 JSON；解析成功立即上桌面，**无需挑选教学班**。
- **可扩展**：`TjtCore` 是平台无关的核心库（Linux 上就能 `dotnet test`），适配器注册表 + 统一课表模型。
- **离线**：数据落在本机 JSON，不联网、不上传；Cookie 只存本机、不进日志。

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

1. 从 [Releases](../../releases) 下载 `TJDesktopTimetable-v1.0.0-win-x64.zip`（自包含：**不需要**预装 .NET 或 Windows App Runtime）。
2. 解压到任意**本地磁盘**目录（别放在 `\\wsl.localhost\...` 这类 UNC 路径下）。
3. 双击 `Tjt.App.exe`。首次启动会打开设置窗口的「导入课表」页，按下一节导入一次即可。

> 卸载 = 删目录；数据在 `%APPDATA%\TJDesktopTimetable\`（`settings.json` / `timetable.json` / `credentials.json`），
> 想彻底清干净就一并删掉。
> 开发机上重新构建后启动：`C:\tjt-tools\TjtApp.cmd`（普通窗口）/ `TjtApp-desktop.cmd`（显式贴桌面层）/ `TjtApp-nobackdrop.cmd`（跳过材质）。

## 课表数据从哪来

本阶段不做自动登录抓取（1 系统 SSO + 短信验证码链路不适合放进桌面客户端）。手动获取一次：

**A. 从 1 系统直接获取（推荐）**

1. 浏览器登录 [1 系统](https://1.tongji.edu.cn/)，打开"我的课表"页面。
2. F12 → **Network** → 刷新页面 → 找到**返回 200 且内容是课程列表**的那条请求 → 右键 → **Copy → Copy as cURL**。
   - 课表页现在调的是 `GET /api/electionservice/reportManagement/findStudentTimetab?calendarId=…&studentCode=…`
     （研究生页是 `findSchoolTimetab2`）；旧接口 `…/student/xxxx/getDataBk` 同样支持。
   - 复制错请求（例如 `schoolCalendar/detail`——那是**校历**、里面没有课程）时，程序会在探测行里如实告诉你
     它看到了什么，而不是默默失败。
   - 只复制了第一行（没有 `-H 'cookie: …'`）时也会明确提示"看起来只复制了第一行"。
3. 打开"导入课表"页，把这条请求整段粘贴进去 → 点 **获取我的课表**。
4. 程序照原样请求一次（请求里自带 Cookie 与 `x-token`）→ 解析 → 立即应用到桌面挂件；
   学期 id 直接从请求 URL 的 `calendarId` 取，所以"当前第几周"也是准的。

> 粘贴内容只保存在本机 `%APPDATA%\TJDesktopTimetable\credentials.json`，不会上传、不进日志（日志里只记长度）；
> 用完可在浏览器退出登录使其失效。
> 命令行自检：`--fetch-check <请求文件>`（抓一次、把探测结果写日志、不落盘、退出码表成败）。

**B. 本地 JSON 导入**

1. 同上抓包，把课表接口响应另存为 JSON（可选再存一份校历响应）。
2. "导入课表"页 → 选择 JSON 文件（或直接粘贴 JSON）→ **导入并应用**。

> 导入结果落盘在 `%APPDATA%\TJDesktopTimetable\timetable.json`（camelCase），可直接手工替换 / 备份。

## 已知限制

- 不做自动登录：手动粘一条请求（一次即可）；登录态过期后需要重新复制。
- 研究生页的 `findSchoolTimetab2` 分支按前端源码实现，**未在真机实测**。
- 内置学期表只覆盖已知学期（当前 `122` / `124`）：更远的学期导入后课表正常，但顶部不显示"现在第几周"。
- Acrylic 需要透明窗口，观感可能弱于 Mica。
- 设置里的「显示周末 / 周次过滤」尚未实现。
- 仅 Windows x64（Windows 10 2004+）。

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
- [ ] 新学期自动取校历（不再依赖内置学期表）
- [ ] 「显示周末」与周次过滤
- [ ] ICS / 图片导出
- [ ] 多校适配器

## 许可

MIT © 2026 gzy31007
