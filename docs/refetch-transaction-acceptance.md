# Windows 课表重新获取：实施与验收记录

工单：[Windows：区分课表重新获取与本地载入，保存成功后整份替换](https://github.com/gzy31007/TJDesktopTimetable/issues/13)；所属地图：[Windows 桌面课表后续方向：同济优先、日常查看与交互优先](https://github.com/gzy31007/TJDesktopTimetable/issues/5)。

用户另行授权实施本票。前项[下一次上课摘要](https://github.com/gzy31007/TJDesktopTimetable/issues/12)的完整门禁已核对，不只依据关闭状态；其记录见[前项验收记录](next-class-summary-acceptance.md)。本票不发布版本、不扩展 Linux、不改变原有视图、假期或 ADR。

## 实现

- `MainWindow.ApplyImportedTimetable` 先保存，保存失败直接返回，不改当前载入对象，也不重画新表。成功后整份替换，不合并旧课程。持久化仍是 `TimetableStore.Save` 的临时文件写入与原文件替换，不新增存储抽象或跨文件事务。
- 既有导入页明确区分「重新载入课表」的本机读取与「重新获取课表」的联网取得。成功和失败提示反映实际采用结果；未知开学日不承诺准确教学周。
- 仅有 HTTP 401/403 证据时判为登录失效。粘贴请求引导学校网页重登、复制完整请求；内置窗口引导关闭后从导入页重新打开，不承诺在已停止捕获的旧窗口重试。普通网络或数据错误不一律归为登录失效。
- 内置捕获完成后通过 `ImportService.CapturedResponseCompleted` 更新导入页结果和当前课表摘要。先缓存结果，再逐一通知观察者；观察者异常不能反转已保存成功的结果。页面挂载时先订阅、重读摘要并回放缓存，卸载退订；其他导入/载入动作清缓存，避免回放过期结果。

## 主验收接缝与隔离

新增显式 `--session-check <plan> --diagnostic-dir <dir>`，在挂件和设置窗口挂载后驱动真实导入页的 `ButtonAutomationPeer.Invoke`，走生产 `ImportService`、保存、切换与渲染编排。不是建窗前 CLI 导入，不是直接调用应用新表的私有函数，也不是另造一份测试导入实现。

观察值来自当前载入对象、当前挂载 XAML 的实际 TextBlock 文本/测量尺寸/相对坐标、导入页结果及磁盘字节与 SHA256；不重新计算布局来冒充画面。

目录覆盖设置、课表、凭据、WebView2 profile，且必须在任何存储访问前生效。拒绝真实数据目录的上下级、别名、UNC/device、符号链接/junction；首次只接受空目录，复用须有工具的精确 marker。诊断启动不初始化托盘、自启对账或版本检查。合成请求仅允许 loopback。本机脚本只启动/结束自己的验收进程，不停止已安装应用。

## 平台无关回归与原生构建

- SDK 10.0.401：`dotnet test dotnet/TjtTimetable.slnx --no-restore --verbosity minimal`，419 passed / 0 failed / 0 skipped。包含同源链接生产 `ImportService` 的消息、保存 false、零课程、两入口、事件异常/缓存测试及核心捕获入口的纯函数测试。测试中的外壳 stub 不作为 Windows IO 或真实 UI 的通过证据。
- 存在 NU1900 警告：NuGet 漏洞数据缓存路径访问被拒；未成功完成依赖漏洞数据检查，不能据此宣称依赖安全审计通过。
- 最终 Windows 原生构建开始于 2026-10-17 15:34:50 +08:00：`.tools/build-winui.ps1 -NoRestore -RunSmoke`，build exit 0、0 warnings / 0 errors、smoke exit 0。原始日志 `C:\tjt-tools\build.log`。后续仅改验收脚本的断言/留证，不改业务源码。
- `git diff --check` 通过。实现基准 `00e1f89c2dd68d0c8b786a08dabada7676785fb4`；验收完成时源码尚未提交/推送、未发版，后续提交以 Git 历史为准。原有 `AGENTS.md`、`CONTEXT.md`、`docs/agents/` 和图片的未提交改动不属于本票，未覆盖。

## Windows 完整矩阵

2026-10-17 16:06:38 +08:00 最终独立全量复跑通过：PowerShell exit 0，509 条断言，6 个应用进程全部 exit 0，共 40 步完整观察。独立验收员另行解析原始 JSON：40/40 的 Loaded 与 Persisted 相同，40/40 的文件文本 SHA 与记录自洽；23 个失败/恢复后的 A 观察均保持各自基线 SHA。

原始目录：`C:\tjt-tools\issue13-final-verifier-r3-20261017`。`verification.txt:510-538` 留存交大降级、未知学期、真实三个文件 hash 不变、安装版 PID 18704 仍运行及最终总数。六份 `transaction.observations.json`、`restart.observations.json`、`login.observations.json`、`sjtu.observations.json`、`sjtu-degraded.observations.json`、`unknown-term.observations.json` 独立保留，不受共用数据目录的重启覆盖。对应步骤数为 18 / 2 / 14 / 2 / 2 / 2，进程 PID 为 8348 / 38544 / 40864 / 18076 / 39852 / 24800。

固定 `--today 2026-09-21 --week-view current --size 1120x760`。本机工具为 `.tools/import-session-server.py` 和 `.tools/import-session-check.ps1`，服务仅 `127.0.0.1:51413`。工具及生成输入是仓库既有约定下忽略的本机辅助文件，不随发布包分发；新增的应用内诊断驱动位于 `dotnet/Tjt.App/Diagnostics/ImportSessionCheck.cs`。

1. 先由实际导入按钮使 A 成为当前画面和落盘数据；新增 B 两门替换 A 一门，随后同数 C 两门同时改名、教室、星期和节次。检查画面和载入对象无旧项，文件一致，导入页成功与摘要更新，独立重启读回 C。
2. 在同一已载入 A 的挂件中覆盖临时文件写入锁、原文件替换锁；手动 JSON、联网重新获取、内置登录三路径均经过实际保存失败。释放锁后 A 文件 SHA256 不变、画面仍 A；替换失败时临时文件已有完整 B，证明没有把写临时文件失败误当作替换失败。
3. 合成网络失败、401/403、500、HTML、非法 JSON、未识别响应和零课程输入均保留 A。仅认证状态给登录失效引导。本地重新载入及重启前后服务计数完全相等，联网取得产生合成服务请求。
4. 真实 WebView2 合成页覆盖失败保留 A、内置认证提示、关闭旧窗后由既有按钮新开窗口并成功取得 B/C。检查导入页成功摘要、页面卸载重建后的缓存回放和后续手动导入清除旧结果。
5. 交大整学期课表保留 1–16 周位掩码 65535、校历开学日 2026-09-14、17 周；辅助学期/校历 HTTP 503 而 URL 学期已知时，仍成功采用课程并显示开学日未知警告。未来未知同济学期不编造开学日，也不宣称准确当前教学周。
6. 验收前后真实设置、课表、凭据文件 SHA256 不变；已安装应用 PID 未被停止，WebView2 使用隔离 profile。原始日志检查合成 Cookie 不泄漏。

## 最终构建指纹

独立核对 14 份本票 C# 源文件与 `C:\tjt-tools\work\dotnet` 构建镜像 SHA256 全部相等；最终验收前后二进制指纹一致，未在验收中重建。输出目录为 `C:\tjt-tools\work\dotnet\Tjt.App\bin\Release\net10.0-windows10.0.22621.0\win-x64`。

- Tjt.App.exe：`5B3D9AECD2843F0680B2D33CF52BEDF794F5C1C8E0F459B350FCC9B59D4ABEC8`
- Tjt.App.dll：`B6D2456FA33B9FE06C3E0FA153AF7F13EE5D45B22F61013129C0C4FB0A2EE710`
- Tjt.Core.dll：`373E98E7CC5AF2F977A9732B22E1EFBEEDBE194FE0032B62A51D7D133A099269`
- Tjt.Widget.dll：`F618E07F7BDAA5AF976B84374B9CF84495598AEB27A7AB0AA18FF9B8650AD704`

B→C 两个课程格的实测 X 从 74.333→374.333、224.333→524.333，Y 保持 103.667 / 207.667；宽高均大于零。行标签由 `1 · 08:00`、`2 · 08:50`、`3 · 10:00`、`4 · 10:50` 换成 `5 · 13:30`、`6 · 14:20`、`7 · 15:30`、`8 · 16:20`，对应不同星期与时段，而非只改课程数量。

## 独立审查与反例

Standards 和 Spec 最终只读复核均无剩余发现，两轴分别判断，不以其中一轴代替另一轴。曾发现导入页构造到挂载之间会丢失内置完成事件，已经用「先缓存后通知、挂载先订阅再读取」修复；Spec 代理最终静态复核未见该问题剩余阻断。安全复核促成隔离目录的 marker 和路径别名/reparse 拒绝。静态审查没有冒称运行 Windows。

最终测试前的失败原样保留，不计入通过：

- 首轮错误要求同数改节次后 X、Y 坐标同时增大。生产既有 `TrimEmptySlots=true`，B 显示第 1–4 节、C 显示第 5–8 节，裁剪空行后 Y 相同完全合理。修为核查星期列 X、实际四个节次/时间标签的替换及载入日/节次，不修改生产布局。
- 第二轮错误要求界面包含诊断内部 Code。生产 `ImportPage` 显示 Diagnostic.Message 而不显示 Code，实际已显示开学日未知警告。改为断言真实可见警告文本，不改生产文案来迎合脚本。

## 取证边界

真实 Windows/XAML、真实 WebView2 与真实文件锁均由合成数据驱动；没有使用学校账号、真实 Cookie 或线上 SSO。这里的按钮验证是已挂载控件的自动化 Invoke，不是物理鼠标点击或逐像素美术验收。不证明所有线上认证响应形状、断电/崩溃/文件系统损坏恢复。保留未知开学日与既有假期兜底，也不处理教务之外临时通知。
