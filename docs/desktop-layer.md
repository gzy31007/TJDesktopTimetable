# 桌面层级层：机制、验收与外观

> 2026-09-14 重写。本文是**结论 + 证据**的汇总，不是施工记录：改动原因、踩过的坑与硬约束
> 都在根目录 `AGENTS.md` 的"易错知识点"里，这里只写"现在是什么样、凭什么说它对、怎么复现"。
>
> ⚠️ **历史文档（2026-09-16 注）**：本文写于 Electron 线还在的时候，下文出现的
> `apps/desktop/src/main/win32/*.ts` 等路径**已随该线删除**（见 `AGENTS.md`「项目定位」）。
> 这里保留的是**机制结论与真机证据**，它们对现行 C# 实现（`dotnet/Tjt.App/Win32/Layer.cs` 等）
> 同样成立；要看当前代码请去 `dotnet/`。

## 1. 一句话

挂件是**桌面图标视图（`SHELLDLL_DefView`）的 owned window**：owned 窗口恒在 owner 之上，
也不属于"显示桌面"要最小化的那批独立顶层窗口 —— 所以 Win+D 之后它**压根没被隐藏**，
不需要任何"被隐藏后拉回来"的恢复逻辑。

## 2. 层级层架构

```
apps/desktop/src/main/win32/
  api.ts             koffi/user32 绑定的唯一入口 + 常量 + 句柄工具
  desktop-host.ts    桌面宿主解析与 owner 生命周期（存档 / 读回校验 / 还原 / 缓存自愈）
  resting-policy.ts  静息落点策略（纯函数，零依赖，有单测）
  resting.ts         z-order 原语（瞬时浮起 / 置底 / 插到某窗口之后 / NOACTIVATE 摘戴）
  layer.ts           编排：attachToDesktop / 静息 / 交互 / 诊断 / 消息订阅
apps/desktop/src/main/windows/
  geometry.ts        窗口几何规则（越界夹回 / 默认落点 / 尺寸下限，纯函数 + 单测）
  widget.ts          Electron 窗口本身（拖动、缩放、位置持久化、主题与材质）
```

行为契约：

| 场景 | 行为 |
|---|---|
| 启动 | 找 Explorer **已创建**的 `SHELLDLL_DefView`，写 owner 前存档原值、写后读回校验；失败则还原并退化为"无 owner + 置底" |
| 静息 | 按当前前台三选一：无前台/前台是桌面壳 → 回桌面层；前台是自己或本应用 → 只维护内部顺序；前台是第三方 → 插到它**之后** |
| 兜底巡检 | 每 5 秒查一次 owner 是否还在期望的宿主上；**正常时不动窗口**（没有每秒 `SetWindowPos`） |
| Explorer 重启 / 显示变化 | 订阅 `TaskbarCreated` / `WM_DISPLAYCHANGE` / `WM_SETTINGCHANGE`（去抖 300ms）→ 作废宿主缓存 → 重新静息 → 重新校验位置 |
| 交互 | `suspendRestingStyle()` 临时浮起；结束时 `resumeRestingStyle()` 重新落点。拖动本身走 Chromium 原生 move loop（`-webkit-app-region: drag`） |
| 越界 | 拖动松手 220ms 后把窗口夹回工作区（原生拖动不限制越界，一旦拖出去，右下角的缩放手柄就点不到了） |

明确**不做**的事（都有真机依据）：

- 不用 `SetParent`（子窗口会被桌面图标压住、拖动坐标错乱）；
- 不发 `0x052C` 催生 WorkerW（登录期会和 Explorer 恢复图标布局抢时序）；
- 不给静息态戴 `WS_EX_NOACTIVATE`（系统会跳过原生 move loop，挂件就拖不动了）；
- 不修 Shell 的 "last active popup"（那类补救要周期抢前台，本身就是干扰源）。

## 3. 真机验收（Windows 11 / 150% 缩放，全部为本机实测）

| 项 | 结果 | 证据 |
|---|---|---|
| Win+D 后仍可见 | ✅ | 按下后前台变为 `Progman`（证明按键生效），挂件全程 `IsWindowVisible=True / IsIconic=False`、owner 不变；**启动日志中 hide/minimize 行数为 0** |
| 贴桌面、不被图标遮挡 | ✅ | owner 指向桌面图标视图，z-order 恒在其上 |
| 拖动 | ✅ | 合成鼠标拖 +130,+100 → 窗口 `739,283 → 859,375`，与鼠标位移一致 |
| 缩放 | ✅ | 缩放手柄走渲染层自实现循环（日志"开始缩放"），窗口尺寸随之变化 |
| 越界夹回 | ✅ | 拖出右边界 104px 后日志 `夹回 {"from":"1102,412","to":"960,412"}`，手柄恢复可达 |
| 材质 Mica | ✅ | 按下 Win+D 后截取的窗口矩形里"壁纸 + 完整挂件内容"同屏 |
| 材质 Acrylic | ✅ | 同上，截图是完整课表 + 工具条 + 亚克力面板 |
| 最小尺寸 | ✅ | 缩到下限 320×220 后头部只剩操作按钮，无溢出/裁切 |
| 深色主题 | ✅ | `glass` + `mica` 下文字与网格线对比度正常 |

复现方式（PowerShell 脚本不入库，见 `AGENTS.md`）：起应用 → 用 `mouse_event` 合成真实鼠标输入
（`SetCursorPos` 不产生输入消息）→ `keybd_event` 发 Win+D → 比对 `IsWindowVisible/IsIconic`
与日志。注意 PowerShell 不是 DPI 感知进程（虚拟坐标 vs 日志里的物理坐标差 1.5 倍），
截图一律"全屏抓 → 按物理 rect 裁剪"。

## 4. 外观

设置面板「显示与行为」里可调：

| 设置 | 取值 | 说明 |
|---|---|---|
| 主题 | 浅色 / 深色玻璃 / 水晶 | 渲染层色板 |
| 窗口材质 | 纯色（默认）/ 云母 / 云母 Alt / 亚克力 | 走 DWM；**切换材质要重建窗口**（`backgroundMaterial` 只在创建时生效） |
| 窗口圆角 | 跟随系统 / 标准 / 小 / 直角 | 走 `DWMWA_WINDOW_CORNER_PREFERENCE`，可运行时切换 |
| 背景不透明度 | 30%–100% | **只影响底板**，文字与色块始终清晰；有 0.55 的下限保证对比度 |

材质在挂件上的定位是**色调层**：主题底板保留约 6 成，材质负责把壁纸色调与模糊带进来。
全交给材质会失控 —— 材质明暗跟壁纸走，深色壁纸 + 浅色主题会让文字直接糊掉。

卡片头的语言对齐 DeskBox：标题前有品牌色块、次级操作（周次过滤 / 含周末 / 隐藏）**悬停才出现**，
静止时只留标题、摘要与「设置」。

想要 DeskBox 那种**深色亚克力卡片**观感：主题切「深色玻璃」+ 材质切「亚克力」
（亚克力的代价是窗口透明 → 拿不到 DWM 圆角，这是系统限制）。

## 5. 已知限制与可选的后续

- 亚克力模式下圆角档位不生效（DWM 不给透明窗口裁圆角）；失焦时会被 DWM 切成不活跃色。
- 缩放上限按"工作区右/下边界到窗口左/上边界的距离"计算，且不低于当前尺寸；被夹回是唯一的越界处理。
- 可选 v2（纯审美，未做）：按钮图标化、网格表面与密度档位。

## 6. 与 DeskBox 的关系

机制参考了 DeskBox（C#/WinUI 3）的**已公开行为**：owner 挂桌面图标视图、静息/唤起分层、
瞬时 `HWND_TOPMOST → HWND_NOTOPMOST` 浮起、不在登录期催生 WorkerW。
**代码为独立实现，不含其源码或文档正文**：DeskBox 自 1.2.0 起为 GPL-3.0-only，
本项目保持 MIT，只借鉴机制（思想），不搬运表达。相关的机制摘要与规模/许可评估见
`docs/deskbox-win-d-analysis.md` 与 `docs/deskbox-refactor-assessment.md`。
