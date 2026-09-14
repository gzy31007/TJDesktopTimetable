# DeskBox 作重构范本：工程规模与法律约束评估

> 评估对象：`/root/TJDesktopTimetable/.refs/DeskBox`（只读，未做任何修改与 git 操作），工作副本自述版本 **1.5.1**。
> 统计口径：`find` + `awk` 自算（非 cloc）。`code` = 非空且非整行注释（行首 `//` `/*` `*` `#` `--` `<!--`）；**偏差说明**：C# 的 `#if` 预处理器行也被计入"注释"，故 C# 注释数略偏高、代码行略偏低。
> 参照项目：`/root/TJDesktopTimetable`（Electron 44 + TS + Vue 3，MIT）。

> **来源声明**：本文是为 MIT 项目 `TJDesktopTimetable` 独立整理的工程规模、构建事实与许可约束评估，结论与数字均由本项目自行统计归纳。
> 上游 DeskBox 以 **GPL-3.0-only** 授权，**本文不包含其代码、注释或文档正文**（含译文），仅描述事实。
> 文中出现的路径、类型与 API 名称及 `文件:行号` 仅为定位用途；任何实现都须由本项目独立编写。

## 1. DeskBox 代码规模画像

**总量：1,328 个文件（不含 `.git`）。** 扩展名分布 top：`.cs` 952、`.md` 120、`.ps1` 42、`.xaml` 36、`.svg` 32、`.png` 30、`.json` 27、`.dll` 14（随仓库携带的第三方二进制）、`.resw` 12、`.iss` 12、`.isl` 10、`.rs` 8。

| 类别 | 文件数 | 总行 | 代码行 | 空行 | 注释行 |
|---|---:|---:|---:|---:|---:|
| C# 应用源码 `src/` | 637 | 236,826 | 203,923 | 24,920 | 7,983 |
| C# 测试 `tests/DeskBox.Tests` | 315 | 61,532 | 54,643 | 6,719 | 170 |
| XAML `src/` | 36 | 17,405 | 16,533 | 698 | 174 |
| Rust `native/`（`.rs`） | 8 | 7,018 | 5,846 | 619 | 553 |
| Rust C 头 `deskbox_native.h` | 1 | 532 | 344 | 42 | 146 |
| PowerShell `scripts/` | 42 | 35,605 | 33,274 | 2,223 | 108 |
| Inno Setup `installer/`（`.iss`/`.isl`） | 22 | 7,232 | 6,364 | 731 | 137 |
| Markdown 文档 | 120 | 22,714 | 14,570 | 6,140 | 2,004 |
| CI 工作流 yml | 3 | 331 | 287 | 40 | 4 |
| JSON / resw 资源 | 29 | 37,986 | 37,914 | 72 | 0 |

即：**手写托管代码约 20.4 万行 + XAML 1.65 万行 + Rust 0.58 万行 + 脚本 3.3 万行**，另有 5.5 万行测试。规模是我们的 **约 50 倍**（我们 `packages/core` 2,432 行、`apps/desktop` 2,406 行）。

**按模块分层（`src/DeskBox`，`.cs` + `.xaml`）**

| 模块 | 文件数 | 行数 |
|---|---:|---:|
| `Services/` | 277 | 77,258 |
| `Controls/`（含 `WidgetContents/`） | 113 | 54,175 |
| `Views/` | 82 | 46,648 |
| `ViewModels/` | 70 | 31,191 |
| `Helpers/` | 43 | 15,682 |
| `Models/` | 47 | 4,864 |
| `Contracts/` | 6 | 201 |

单文件极大：`WidgetShell.xaml.cs` 5,369、`FileSurfaceContent.xaml.cs` 4,967、`App.xaml.cs` 4,641、`WidgetWindowBase.Collapse.cs` 4,463、`SearchPopupWindow.xaml.cs` 4,432、`SettingsWindow.xaml` 3,492、`Win32Helper.cs` 2,172。`WidgetManager` 一个类被拆成 **15+ 个 partial 文件**（`.ZOrder` 972、`.Groups` 2,739、`.TrayAnimation` 567、`.CapsuleArrangement` 879、`.Storage` 782…）。测试侧：**2,019 个 `[Fact]` + 327 个 `[Theory]` + 1,278 条 `[InlineData]`**，分布在 315 个文件里。

**项目结构（2 层）**

- `src/DeskBox/`：`Assets`、`Contracts`、`Controls`、`Helpers`、`Models`、`Resources`、`Services`、`Strings`、`Styles`、`ThirdParty`、`ViewModels`、`Views`
- `src/DeskBox.Updater/`：472 行的独立更新器
- `tests/DeskBox.Tests/`：平铺 315 个测试文件
- `native/`：`deskbox-native`、`deskbox-thumbnail-proxy`、`deskbox-audio-session-fixture`、`include`
- `installer/`：Inno Setup，12 语言，含依赖下载与卸载清理
- `scripts/`：42 个 PowerShell（AOT 审计 / 冒烟 / 发布 / 证据）
- `docs/`：`architecture`、`articles`、`baselines`、`press-kit`、`releases`、`requirements`、`support`
- `.github/workflows/`：`ci.yml`、`arm64-runtime.yml`、`distribution-audit.yml`

**构建要求（读 `global.json` / `Directory.Build.props` / `DeskBox.sln` / `rust-toolchain.toml` / `installer/` / `.github/workflows/`）**

- **SDK**：`global.json` 钉死 **.NET SDK 10.0.303**（`rollForward: latestPatch`，不允许预览版）。TFM `net10.0-windows10.0.22621.0`，`SupportedOSPlatformVersion 10.0.19044.0`（Win10 21H2+），`Platforms x64;ARM64`，`RuntimeIdentifiers win-x64;win-arm64`。`Directory.Build.props` 全局 `RestorePackagesWithLockFile=true`，AOT 时改读 `packages.aot.lock.json`。
- **托管依赖**：`Microsoft.WindowsAppSDK 2.4.0`、`Microsoft.Windows.SDK.BuildTools 10.0.28000.2270`、`WinUIEx 2.9.3`、`H.NotifyIcon.WinUI 2.5.0-beta.1`、`CommunityToolkit.Mvvm 8.4.2`、5 个 `CommunityToolkit.WinUI.* 8.2.251219`、`Microsoft.Extensions.DependencyInjection 8.0.1`、`Markdig 1.3.2`，外加随仓库携带的 Everything SDK 原生 DLL（x64/ARM64 二选一，有 MSBuild 目标校验架构配对）。
- **AOT 怎么打**：opt-in 的 `-p:DeskBoxAotAudit=true` 打开 `PublishAot=true; SelfContained=true; PublishTrimmed=true; JsonSerializerIsReflectionEnabledByDefault=false`。`PublishAot=true` 会定义 `DESKBOX_NATIVE_AOT`（138 处条件编译，遍布约 100 个文件）并 `Compile Remove` 掉 `**\*.Aot*Smoke.cs` 与 `Services\Aot*Fixture.cs`。MSBuild 目标 `ValidateDeskBoxNativeAotConfiguration` 在 `PublishAot=true` 且 `DeskBoxRustNative!=true` 时**直接 Error 失败**——即"要 AOT 就必须带 Rust 后端"。AOT 只接受 `x64/win-x64` 与 `ARM64/win-arm64` 配对。
- **Rust 原生层怎么进构建**：`rust-toolchain.toml` 钉死 **Rust 1.96.0**（profile minimal，targets `x86_64-pc-windows-msvc` + `aarch64-pc-windows-msvc`）；Cargo workspace 3 个 crate，`deskbox-native` 是 `crate-type=["cdylib"]` → `deskbox_native.dll`（依赖 `windows 0.62.2`，edition 2024）。由 MSBuild 在 `CoreCompile` 前用 `powershell.exe` 调 `scripts/build-rust-native.ps1`（另有一个独立 `deskbox-thumbnail-proxy.exe`）。**构建过程本身依赖 PowerShell**。
- **Windows App SDK 怎么带**：`WindowsPackageType=None`（非打包直装）+ Inno Setup。安装器两条路：捆绑私有 Windows App Runtime 组件离线装，或让 Setup **下载 .NET 10 Runtime + Windows App Runtime 2.4**。Store 路线走 MSIX（`DeskBoxDistribution=Store`，需 `makeappx.exe`）。
- **CI 多久多复杂**：三个工作流，**全部 Windows、全部锁 restore（`--locked-mode`）**。`ci.yml`（windows-latest，.NET 10.0.x，restore→build→`dotnet test --blame-hang --blame-hang-timeout 5m`，测试步 10 分钟超时）；`arm64-runtime.yml`（**60 分钟超时**，需要原生 ARM64 runner `windows-11-vs2026-arm`，校验 Rust host 必须是 `aarch64-pc-windows-msvc`）；`distribution-audit.yml`（**120 分钟超时 × 2 平台矩阵 + 20 分钟聚合 job**，runner 标签是 `windows-2025-vs2026`/`windows-11-vs2026-arm`——**非 GitHub 标准托管标签，疑似自定义/预览镜像**，需 Inno Setup 6 的 `ISCC.exe`、Windows SDK 的 `MakeAppx.exe`、原生 Rust 1.96.0 host）。结论：**Linux/WSL 内无法构建**，也没有任何缓存的日常快速验证路径。

## 2. 架构分层

**官方主流程**（`docs/architecture/current_architecture.md`）：
`WidgetKind → WidgetRegistry → WidgetContentDescriptor → WidgetContentFactory / IWidgetContentProvider → IWidgetContent → ContentWidgetWindow → WidgetManager`

- **Contracts**（6 个文件、201 行，最薄也最值钱）：`IWidgetContent` / `IWidgetAddActionContent` / `IWidgetFeedbackSource` / `IWidgetHostContextMenuSource` / `IWidgetTransientStateContent` / `ICalendarPresentationSource`。这批接口的职责边界很清楚：窗口本身、z-order、动画与 DWM 相关行为都不归内容实现管，由宿主窗口一侧负责。
- **Views**：一个基类 `WidgetWindowBase`（按功能切 8 个 partial：`.Collapse` 4,463、`.Backdrop`、`.Bounds`、`.CoordinatedMove`、`.Foreground`、`.Grouping`、`.InputSuppression`、`.Interaction`）+ **两种平行宿主** `ContentWidgetWindow`（文件/Todo/音乐/天气/搜索）与 `QuickCaptureWidgetWindow`（随记）——z-order 文档明确警告：每条唤起/回落路径都有**两份平行实现**，改动必须同步。另有 Settings/Search/Onboarding/DesktopOrganization/ReleaseNotes/StackPopover 等窗口。
- **Controls**：`WidgetShell.xaml(.cs)` + `WidgetContents/*`（FileSurface/Todo/QuickCapture/Weather/Glance…）。
- **ViewModels**：70 个文件，CommunityToolkit.Mvvm。
- **Services**：277 个文件，含 `WidgetManager`（15+ partial）、`WidgetLayerService`（1,117 行，Z-order 原语）、`WidgetSessionManager`（会话状态机 + 交互深度计数）、`GlobalHotkeyService`、`SettingsService`(3,290)、`FileService`(2,777) 等。
- **Helpers**：`Win32Helper.cs`(2,172 行 P/Invoke 集合)、`IconHelper`(2,764)、`ShellContextMenuHelper` 等。共 104 处 `[LibraryImport]` + 136 处 `[DllImport]`。
- **native Rust**：`deskbox_native.dll` = `lib.rs` 3,045 + `shortcut` 895 + `music_volume` 703 + `quick_access` 606 + `recycle_bin` 398 + `explorer_shell_launch` 276；另有 `deskbox-thumbnail-proxy.exe` 860 行。

### 2.1 "因为 Native AOT 而不得不这样做"的写法（实测证据）

1. **手写绑定属性桥**：8 个 `*.AotBindableProperties.cs`，用 `[WinRT.GeneratedBindableCustomProperty([nameof(A), nameof(B), …])]` **把每个被 XAML 消费的属性名逐条列出**（Weather 一个桥就列 40+ 个）。文件注释交代的动机是：Native AOT 会在剪裁时移除反射元数据，绑定不能再靠反射发现属性。这是**最大的抄写成本源**：属性表与该 widget 的功能一一绑定，抄结构等于抄功能。
2. **集合投影降级**：多处 `VisibleItemsSource` 改成 **定长 `object[]`** 而非可观察集合，注释说明这是 WinRT ABI 投影在 AOT 下的限制所致——否则每个绑定到 ItemsSource 的 `{Binding}` 都建在空数组上。`SettingsViewModel.SelectionOptions.cs` 专门写了改用真实 `SettingsOption[]`，让 JIT 与 AOT 两条投影路径表现一致。
3. **反射被从设计里删掉**：整个 `src/` 只有 **7 处反射 API**（3 处读 `AssemblyInformationalVersion`，4 处 `Type.GetTypeFromProgID("Shell.Application")`/`Activator.CreateInstance`），而 `RequiresUnreferencedCode` / `DynamicallyAccessedMembers` / `UnconditionalSuppressMessage` 标注 **0 处**。说明作者不是"打标注压警告"，是**把反射路径整个换掉**。
4. **JSON 全源生成**：26 处 `JsonSerializerContext`/`JsonSerializable`，`JsonSerializerIsReflectionEnabledByDefault=false`，并配有 274 行的 `json-source-generation-baseline.md` 基线文档。
5. **缺口用 Rust 补**：`ShortcutNativeBackend.cs` 的注释定下纪律：进了 Native AOT 就必须走原生后端，绝不允许回退到旧的 ComImport 路径；AOT 模式下 legacy COM oracle 被 `DESKBOX_NATIVE_AOT` 编译掉，等价能力（快捷方式、音量、快速访问、精确回收站恢复）**全部由 Rust 重写**。这就是"要 AOT 就必须带 Rust"的由来。
6. **契约测试语言化**：**117 个 `*ContractTests.cs`**、**50 个 `Aot*` 测试文件**（两者合计约 22,531 行）、`docs/architecture/stage-reports/` **53 份阶段报告**。MSBuild 的校验错误消息里塞进了 60+ 个"已完成门禁阶段"的全名——AOT 兼容性被固化成了不可回退的契约。

### 2.2 外面的人照着抄结构有多难

**结论：Views/Services 那套 partial 切分没有抄的价值，真正要抄的是"补偿性工程"，而那部分与它的功能强耦合。**

- AOT 绑定桥、`object[]` 投影、源生成上下文、契约测试，四项合计是**为了绕开 AOT 剪裁而额外付出的成本**，每一项都要针对"自己的属性/自己的集合/自己的序列化模型"重写一遍，无法照搬。
- 它的窗口/会话/z-order 分层（基类 + 两种平行宿主 + Manager 按关注点切 partial + 独立 Z-order 原语服务）是**与 AOT 无关、可独立借鉴**的部分，也是最干净的部分。
- AOT 已知门槛：WinUI 3 + Native AOT 在公开资料里仍属"能跑但需大量手工补偿"的组合；DeskBox 用 53 份阶段报告和 117 个契约测试换来的，是**一个 solo 开发者数月量级的打磨**，不是可以按目录结构复制的模板。若照抄结构但不做这套补偿，结果是不能开 AOT、退回 JIT——那"抄 AOT 架构"这个前提就消失了。

## 3. 许可冲突

**事实认定（均已读原文）**

- `LICENSE`：GNU GPL **Version 3, 29 June 2007** 标准全文 674 行，**未附加任何例外条款或补充许可**（无 linking exception、无 "or later" 声明）。`README.md` L11/L35/L307 与 `README.zh-CN.md` 均标注 **`GPL-3.0-only`**。
- **变更时点可精确定位**：`CHANGELOG.md` 的 **1.2.0（2026-07-02）** 条目声明，项目自该版本起把未来源码与发行版的许可由 MIT 改为 GPL-3.0-only，同时明确此前已按 MIT 发布的 DeskBox 版本继续适用 MIT；上一版 **1.1.10（2026-06-29）** 是最后一个 MIT 版本。**当前工作副本自述 1.5.1，整体为 GPL-3.0-only。**
- 唯一性明示：`README` 写明当前阶段不接受外部 pull request，理由是保持架构一致、把版权边界留在单一来源——版权高度集中于单一作者。
- **不确定处**：`src/` 下 **0 个 `.cs` 文件带 license header 或 SPDX 标识**，逐文件归属依赖仓库级 `LICENSE` + `README`。这在实务上通常足够，但严格说，"only"（而非"or later"）的定性来自 README 表述而非许可证正文或源文件头。另：我未做（也被要求不做）git 操作，**1.1.10 与 1.2.0 之间是否存在中间提交的混合状态，无法从工作副本判定**。仓库内 14 个随包 `.dll`（如 Everything SDK）是各自独立的第三方许可，不在 GPL 覆盖内，但也不在可搬运范围内。

**逐条后果**

| 动作 | 法律后果 |
|---|---|
| 把 **GPL 源码逐字/改改变量名后搬进 MIT 项目** | GPLv3 §5 要求衍生作品**整体**以 GPLv3 授权；§6 要求向接收者提供对应源码；§10 禁止对下游附加限制。这里的 MIT 声明与 GPL 义务直接冲突：MIT 允许他人闭源再分发，GPL 不允许。结果是要么**整个项目改 GPL-3.0-only**（MIT 声明作废），要么**停止分发**。不存在"只给这一个文件标 GPL、其余保留 MIT"的干净解法——被同一进程加载、互相 import 的两个模块通常被认定为**单一衍生作品**；"mere aggregation"抗辩只适用于彼此独立、非组合的程序，TS/C# 里 `import` 即组合。 |
| 搬**片段**（函数体、几十行） | 同样触发。版权保护的是表达，达到最低独创性的片段即受保护；逐行改写（改名/换顺序）在法律上是"非字面复制"，仍可能被认定侵权。安全边界只能是"不复制任何表达式，独立实现"。 |
| 只借鉴**架构思路 / 交互设计 / 视觉风格** | **一般不触发 GPL**。理由：版权不保护思想、方法、系统、操作过程（idea/expression dichotomy；17 U.S.C. §102(b)；TRIPS §9(2)）。GPL 是**通过版权行使**的许可，没有可版权表达就没有 GPL 义务。模块边界、状态机设计、"瞬态 TOPMOST→NOTOPMOST 浮起"这类**机制**、交互流程与功能行为，通常落在"思想/方法"侧。 |
| 边界风险区（须谨慎） | (a) **具体表达**可能被认定：具体的数据结构定义、成套的接口签名序列、类/方法命名的独特编排、有独创性的注释与文档正文。该仓库 **120 个 `.md`、22,714 行文档也是仓库 work 的一部分**——**不要复制或翻译其文档正文**；只提取事实（"Windows 有这个 API 语义"），事实不受版权保护，文字表述受保护。(b) **美术与标识**：`Assets/` 下 32 个 svg / 30 个 png / 3 个 ico 及截图有独立版权；风格（"Mica 卡片 + 网格"）不受保护，但**资源和 "DeskBox" 名称/logo 不能搬**（名称/logo 另受商标与不正当竞争约束，本次未做商标检索，属不确定项）。(c) **专利**：GPLv3 §11 的专利许可只授予其代码的接收者，不复制代码即无此授权；若作者持有相关专利（无公开证据，未检索），独立实现仍可能侵权。 |
| **不分发**（仅自己内部使用/魔改） | GPLv3 §0/§2 的义务只在**分发/传播**时触发。内部私有使用不产生开源义务。但对"要在 GitHub Public / 面向用户分发"的本项目，这条不适用。 |
| 网络服务 | GPLv3 **没有** AGPL §13 的网络条款，SaaS 化可规避——但本项目是桌面分发，无此豁免。 |

**明确结论与建议**

1. **不要复制 DeskBox 1.2.0 及之后（含当前 1.5.1）的任何代码、注释、文档文本或美术资源**，包括"改改变量名/翻译注释/逐行对照改写"。本报告已通读其源码与架构文档，这一事实本身削弱了将来主张 clean-room 独立实现的说服力——若真要严格走借鉴路线，应**先写只含事实与机制需求的中文规格，再据此实现**，并保留独立实现的过程记录。
2. **可以且应当借鉴的是**：分层契约思路（`IWidgetContent` 这种"宿主拥有窗口/z-order/DWM"的划界）、窗口层级机制（TOPMOST→NOTOPMOST 浮起、成组 `DeferWindowPos` 排序、相对前台回落策略、恢复监视器 + 鼠标边沿采样器 + 交互泄漏看门狗）、以及文档里记录的 **Win32 事实与踩坑**（这些是自然规律，不是表达）。
3. **若确实需要其代码**，只有三条路：(a) 本项目整体改 **GPL-3.0-only**（法律上干净，但等于放弃 MIT 公共仓库定位，且要求我们自己的全部源码以 GPL 分发）；(b) 取用 **≤1.1.10 的 MIT 版本**代码（但那是 AOT 大重构之前的代码，架构参考价值大幅下降）；(c) **联系作者单独授权/双许可**（版权集中、无外部贡献者，技术上可行；但作者明确"暂不接受外部 PR"以保持"清晰版权边界"，成功率低）。
4. **不确定处（如实标注）**：衍生作品的边界在各国司法实践中是连续谱而非清晰切线，"片段多大算侵权"没有可依赖的量化阈值；商标与专利风险本次未做检索。任何真金白银的投入前，涉及实际搬运代码的决策应经法律专业判断。

## 4. 三条路线的工作量与代价

| 维度 | 路线 A：重写窗口层级机制 | 路线 B：视觉/交互向 DeskBox 靠拢 | 路线 C：整体重写为 C#/WinUI 3 |
|---|---|---|---|
| **改动范围** | `apps/desktop/src/main/win32/`（现 823 行：`layer.ts`/`dwm.ts`）+ 新增 `zorder.ts`/`session.ts`/`monitor.ts`；`main/windows/widget.ts`(520 行区域)、`main/index.ts`、`shared/ipc.ts`、preload；renderer 侧接 `layer.pause()` 与交互深度上报。**需移植 8 个机制**：瞬态浮起、成组 Z-order（`BeginDeferWindowPos`/`DeferWindowPos`）、相对前台回落三态策略、200ms 恢复监视器、50ms 鼠标边沿采样器、交互深度计数 + 泄漏看门狗、桌面固定层（WorkerW attach）双模式、owner 修复（Progman / last-active-popup）。 | 渲染层为主：`renderer/shared/fluent.css`、`board.css`、`widget/widget.css`、`manage/manage.css`、`TimetableBoard.vue`、`ManageApp.vue`、`AppearancePanel.vue`；主进程 `windows/widget.ts`/`manage.ts` 的材质与自绘标题栏；`docs/` 视觉规范同步。 | **全部作废重建**：`packages/core` 2,432 行 + 8 个 spec 771 行 + 2 个 fixture；`apps/desktop` 2,406 行；根配置与 CI；新增 C# 解决方案（参考 DeskBox：Contracts/Models/Services/ViewModels/Views/Helpers + Rust 原生层）。 |
| **人日粗估** | **8–15 人日**（按 DeskBox 对应实现 `WidgetLayerService` 1,117 + `WidgetManager.ZOrder` 972 + `TrayAnimation` 567 + `WidgetSessionManager` + `Win32Helper` Z-order 部分 ≈ 2,500–3,000 行 C# 折算；其中**一半以上花在 Windows 真机验收**） | **5–10 人日**（DeskBox 视觉层对应的 `Controls` 54,175 行 / XAML 16,533 行中，可迁移到 web 的是令牌与布局思路，不是代码） | **80–150 人日**（下限 = 用 C# 复刻当前功能；**若要做到 DeskBox 同等的 AOT + 契约测试水准，需 200+ 人日**——参照其 53 份阶段报告、117 个契约测试、22,531 行 AOT/契约测试） |
| **core 逻辑可原样翻译性** | 无关（core 不动） | 无关（core 不动） | **可直译**：`model.ts` 147（类型→`record`）、`weeks.ts` 125（位掩码→`uint`，反而更简单：无 JS `1<<31` 变负数问题，但边界测试要重写）、`colors.ts` 57（哈希→色，直译）、`layout.ts` 304（几何可直译，但要改 XAML 测量模型）、`time.ts` 216（东八区/`DateOnly` 语义需按 `TimeZoneInfo` 重写并重测）。**必须重写**：`adapters/tongji-student.ts` 508（字段探测评分、教师正则、calendarId→学期映射、times 合并——思想可抄，508 行全重写并重新对真实抓包校准）、`http-request.ts` 175（PowerShell/curl 命令行解析，C# 无等价 tokenizer）、`adapters/preview-html.ts` 226（HTML 内括号配对扫描 DATA）、`generic.ts` 235、`import.ts`/`registry.ts`/`types.ts`（适配器注册改走 DI 或静态注册）。 |
| **测试迁移** | 现有 61 个 vitest 用例不受影响，新增该领域的测试（真机行为为主，单测覆盖有限） | 现有 61 个用例不受影响；视觉回归靠截图对比 | **61 个用例 → 约 55–65 个 xUnit/MSTest 用例，6–10 人日**。**fixtures 可原样搬**（`tongji-2026-1-personal.json` 28,482 B + `tongji-school-calendar.json` 16,364 B 是抓包数据，不是代码）；但黄金断言（`formatWeeksLabel` 与既有 Python `fmt_weeks` 逐字一致、147 条 → 128 教学班逐条一致）需逐条重写并重新对齐。 |
| **主要风险** | **真机验收不可省**：我们的挂件走 owner=Progman 方案，DeskBox 走 WorkerW attach，两套机制的失败模式不同；AGENTS.md 已记录 Acrylic/Mica 在 Win+D 后 DWM 合成失效（两轮回归）、`SetWindowRgn` 致主进程 12 秒后崩溃——**这些 Electron/Chromium 侧特有的坑，DeskBox 的经验不能直接迁移**。WSL 内无法验证任何 z-order 行为。 | 材质问题**已被硬结论封死**：挂件不能开 `backgroundMaterial`（Win+D 后丢窗口，Electron 侧无法感知），因此"Mica/Acrylic 卡片"只能落在管理窗口 + 渲染层伪玻璃，观感天然打折。**"格子布局"是产品定位变化而非换肤**：从"7 列 × 11 节时间表"改成"DeskBox 式功能格子容器"会重定义产品。 | **开发/调试降级**：失去 CDP 探针（AGENTS.md 明确用它量 `.cell` 尺寸排查网格塌陷）与 DevTools；`WinUI 3` 的 XAML 编译在 Linux 上官方不支持，**WSL 内无法构建**（Electron 侧我们能在 WSL 交叉出 `win-unpacked` 且不需 wine）。**CI 变 Windows-only**：需 windows runner + 原生 Rust host + Inno Setup 6 + Windows SDK `MakeAppx.exe`，参考 DeskBox 的 `distribution-audit.yml`：2 平台 × **120 分钟** + 聚合 job，且用了非标准 runner 标签。**Win 版本依赖**：floor 提到 Win10 21H2（19044）+ Win11 SDK 22621，AOT 下实机验证成本高于 Electron。 |
| **会被放弃的东西** | 无（Electron + MIT + 61 测试全保留）；仅"不引入任何 DeskBox 代码"这一纪律成本 | 现有课表网格的**时间轴语义**（节次纵向轴、单双周并排、条纹特殊块的既有基准）；`docs` 里的视觉既有基准部分失效 | **MIT 许可自由**（若搬运其代码则整体转 GPL）；Electron 跨平台与 CDP 调试；61 个已绿单测；同济适配器的实测校准（重新抓包验证）；Vue 组件生态与 electron-builder 打包链；`packages/core` 的"零平台依赖纯 TS"可移植性（C# 后绑定 .NET/WinUI） |
| **推荐度** | **性价比最高**：直击本项目最大痛点（贴桌面 + Win+D 可见 + 不压屏），范围可控，许可干净 | 可作为 A 的附属项；单独做收益从属于"是否要改产品定位" | 仅在"决定放弃 Electron 且愿意把项目改 GPL 或重写全部逻辑"时才成立；否则是 50 倍代码量级的目标 |

## 5. 我们已沉淀、重写就会丢的资产

（来源：`/root/TJDesktopTimetable/AGENTS.md` 15,114 B、`packages/core/src` 2,432 行、`packages/core/test` 771 行）

1. **同济个人课表适配器** `adapters/tongji-student.ts`（508 行，v3.0.0，`TONGJI_STUDENT_ADAPTER_ID`/`VERSION`）：多格式字段探测与评分（`detectScore`）、`classify` 输入分流、`weekBenginDay`（周从周几开始，同济 = 2）与 `beginDay` 毫秒时间戳处理、`teachingClassId`/`code`/`courseCode` **三键不可混用**的语义、教师正则 `([\u4e00-\u9fa5]{2,8})\((\d{3,6})\)`、同格不同周次/不同老师的多条 `times` 合并成一块、军训（无排课时间段）跳过并给提示、没有教室时 `room` 为空而不是 `0`。
2. **周次掩码逻辑** `weeks.ts`（125 行）：`bit0 = 第 1 周`（与同济 `weekState` 一致，实测 65535 = 1–16 周）、32 周上限、`normalizeMask` 与 `weeksToMask` 的语义区别、`maskToWeeks` 在掩码超校历周数时不丢数据、**`formatWeeksLabel` 与既有 Python `fmt_weeks` 格式逐字一致（黄金格式约定）**、单双周掩码按 `term.totalWeeks` 生成**不得硬编码 `0x5555`/`0xAAAA`**。
3. **布局算法** `layout.ts`（304 行）：`fitGeometry` 自适应列宽、`buildBoard` 的 `trimEmptySlots`/隐藏周末/同格多课并排标 `special`/周次过滤并统计隐藏数、`blockRect` 几何计算、`currentSlotIndex`、`defaultShortName` 课程名压缩。
4. **冲突检测** `conflict.ts`（103 行）：`findConflicts`、`weeksOverlap`，以及 `toggleCourse` **复刻基准版 `tryToggle` 的三态语义**（selected / switched / blocked）。
5. **时间推算** `time.ts`（216 行）：东八区纯日期算术（不受宿主时区影响）、校历毫秒时间戳换算、教学周推算与边界、按日期取当天课程（含周次过滤）、下一节课（含"正在上课"与跨天）、头部摘要。
6. **黄金 fixtures**：`tongji-2026-1-personal.json`（28,482 B）+ `tongji-school-calendar.json`（16,364 B）——**脱敏后的真实抓包**，是全部数据映射断言的基准（禁止放入个人信息）。
7. **61 个单测 / 8 个 spec（771 行）**：其中 `e2e-timetable.spec.ts`（176 行）端到端覆盖 **导入 → 布局 → 时间 → 单双周过滤**，并含**兼容降级**用例（排课服务扁平 `weekState` 格式仍可解析、未知 `calendarId` 退化为默认 16 周并提示、完全不含课表数据时正确报错）。
8. **可扩展适配器架构**：`adapters/types.ts`(128) + `registry.ts`(42) + `generic.ts`(235) + `preview-html.ts`(226) + `import.ts`(82) —— "新增一所学校只加一个适配器文件"，且已实现**探测优先级**（同济 > 排课导出 > 通用）。
9. **一手 Win32 实测结论**（AGENTS.md，无法从公开文档查到、重写必丢）：Acrylic/Mica 在"Win+D 隐藏 → 恢复"后 DWM 合成失效（acrylic 版与"照抄设置窗口加回 mica"版各回归一次）、`SetWindowRgn` 裁圆角致主进程约 12 秒后无日志重启、owner 必须用 **Progman**（非子窗口 SHELLDLL_DefView）、`SetWindowPos` 的 `hWndInsertAfter` 是"插到其后"、鼠标按下期间须临时摘除 Shell owner、须周期修 Shell 的 last active popup、`WS_EX_NOACTIVATE` 会让窗口拖不动、`transparent:false` 时 alpha 被忽略得到黑底、材质只在 `new BrowserWindow()` 声明时有效、UNC 路径不可运行、`userData` 路径需显式固定、`-webkit-app-region: drag` vs `SendMessageW(WM_NCLBUTTONDOWN)` 的实测差异。
10. **打包链路知识**：WSL 内交叉出 `win-unpacked`（**不需要 wine**）、`koffi` 走 `optionalDependencies` + `asarUnpack: ["**/*.node"]`、`electronVersion` 必须精确锁定、沙箱下重定向 electron-builder 缓存、NSIS 交给 `build-win.yml`。
11. **视觉令牌体系** `fluent.css`（色彩分级/圆角/阴影/明暗主题/`.f-btn`/`.f-pill`/`.f-switch`/`.f-card`）+ `board.css` 课表皮肤 + `widget.css` 挂件外壳，含 `--tint/--edge/--ink` 染色契约与 **`lift()` 必须返回 `#rrggbb`**（返回 `rgb()` 会让下游混色 NaN、色块变透明）的踩坑记录。

---

**一句话结论**：DeskBox 是 20 万行级、Windows-only、AOT + Rust 双原生层的成熟产品，其**思路可自由借鉴**（架构分层与 Win32 层级机制最值得抄），**代码与文档文本受 GPL-3.0-only 约束不可搬**（当前版本无任何例外条款）；对应的最优点在**路线 A（8–15 人日，只重写窗口层级机制）**，而路线 C（80–150 人日，且若抄代码则 MIT 作废）只有在我们同时接受"放弃 Electron 生态 + 放弃 61 个已绿单测 + 改 GPL 或全部重写"时才值得启动。
