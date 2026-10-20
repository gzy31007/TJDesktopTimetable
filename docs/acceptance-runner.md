# Windows 安全验收运行器

工单：[#32](https://github.com/gzy31007/TJDesktopTimetable/issues/32)。正式入口在 [scripts/acceptance](../scripts/acceptance/)，不依赖忽略目录中的本机脚本。范围为构建/冒烟、整表会话、更新会话，不收编历史截图或人工拖缩探针。

## 从新检出复跑

前置环境：Windows x64、Windows PowerShell 5.1、.NET 10 SDK、Windows SDK 22621、Windows App Runtime 1.8。运行器本身只用系统 PowerShell/.NET Framework，不要求 Pester、额外 NuGet 测试包或自定义本机目录。缺少应用构建/运行前置条件时应失败，不会自动安装系统工具。

在检出根目录打开 PowerShell：

```powershell
# 不启动 WinUI；与 GitHub Actions 相同的运行器安全回归。
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\acceptance\Test-Runner.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\acceptance\Test-EntryGuards.ps1 -RunRoot "$env:TEMP\tjt-entry-$([Guid]::NewGuid().ToString('N'))"

# 构建一次，再依次运行真实 WinUI 冒烟、整表、更新会话。
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\acceptance\Invoke-Acceptance.ps1 -Today 2026-09-21
```

每次默认在本机临时目录创建唯一 `tjt-acceptance-*` 运行根。可以显式传 `-RunRoot`，但必须是**尚不存在、无路径别名或 junction 的本机绝对路径**，不能与真实应用数据或源码输入重叠。源码检出可位于 WSL UNC 路径；应用的隔离数据和构建输出必须在 Windows 本机盘。

常用参数：

| 参数 | 用途 |
| --- | --- |
| `-Mode All / Smoke / Board / Update` | 默认 All；单项也默认先构建，不会暗取上次的 exe |
| `-SourceRoot` | 默认由入口位置推导检出根目录 |
| `-DotnetPath` | 默认 PATH 中的 dotnet；非标准安装可显式传 SDK 的 dotnet.exe |
| `-NugetPackages` | 可选共享包缓存；默认在本轮目录内。拒绝与真实数据重叠、路径别名及缓存内链接 |
| `-SkipBuild -AppPath` | 明确复用某个已有构建；两者必须配对，不作为干净构建证据 |
| `-Today` | 固定日历，默认 2026-09-21；格式 yyyy-MM-dd |
| `-BuildTimeoutSeconds` / `-TimeoutSeconds` | 每个复制/restore/build 阶段及每个应用会话的期限，默认 900 / 240 秒 |

例如已有 SDK 和包缓存的离线机器，可显式选择它们；参数值属于本机配置，不要写进脚本或提交文件。WSL 调用需使用 Windows PowerShell 的 `-File` 入口，并传 Windows 形式的目录；不要把 `/mnt/c/...` 当成应用路径。

## 安全与失败契约

[Invoke-Acceptance.ps1](../scripts/acceptance/Invoke-Acceptance.ps1) 只创建新目录，复制使用 `/E` 而非 `/MIR`，不扫描或停止任何既有应用。restore/build 也受同一进程拥有权和期限控制，禁用常驻 MSBuild/编译服务器。

[Process.ps1](../scripts/acceptance/Process.ps1) 通过 `PROC_THREAD_ATTRIBUTE_JOB_LIST` 在创建子进程的同一操作中将其放入带 `KILL_ON_JOB_CLOSE` 的 Windows Job Object，没有“先创建、再挂入”的空窗。子孙进程继承归属；超时、正常退出后的遗留子孙、Ctrl+C、运行器被强杀都由该 Job 回收。Job 句柄不传给子进程。不使用进程名称、全局 PID 枚举或杀掉用户同名应用的做法。若当前宿主不允许建立所需 Job，启动应失败，不降级为不受控运行。

启动失败、非零退出、超时、取消和会话断言失败均非成功。公开进程 CLI 的退出码为：0 成功、1 子进程失败、2 启动失败、3 超时、130 Ctrl+C、4 参数错误；调用函数时失败抛异常。强杀时由操作系统给出非零退出，不能保证执行 PowerShell finally 或补写最终日志。运行根的 `result.json` 从开始就写 `Success=false`，只有完整通过并核对配置后才为 true；缺失/未完成的结果也不是成功。

真实数据从未被拿来先改后恢复：冒烟显式使用 `--smoke --diagnostic-dir`，会话始终配对 `--session-check --diagnostic-dir`；写入点在应用访问存储前切换。各模式分别使用新数据子目录，关闭桌面层和材质，不启动托盘、自启或周期检查。入口前后只读核对真实默认数据目录全部文件的存在性与 SHA-256，不输出或保存哈希、不自动恢复用户数据；若发现并发用户操作或其他变化，也按失败处理。

整表计划采用[虚构课表](../dotnet/fixtures/board-content-synthetic.json)，断言读取真实挂载的 XAML 观测，包括门槛、末行可达、固定星期表头及偏移。更新计划使用当前运行器进程内的 loopback 临时端口响应，断言真实按钮、跳过落盘与错误状态保持；下载动作在应用已有限定诊断边界捕获 URL，不打开浏览器、下载或运行安装器。

## 产物与清理

运行根保留每阶段合并输出、进程结构结果、应用日志、解析后的计划、隔离数据和总结果。失败时先看 `result.json` 的 Stage，再看相同前缀日志。运行器回收自己启动的进程树，但**不自动删除证据目录**；进程结束后可人工删除明确的本轮根目录，不要用宽泛通配清理。

仓库内的 `.artifacts/`、`.tools/`、`*.local.json` 继续忽略；不要把本机路径、课表、凭据、截图或构建输出提交。Actions 的独立 `acceptance-runner` job 仅执行合成进程安全测试和入口参数保护，上传其隔离日志；WinUI job 仍只做编译门禁，不把云端编译或安全测试称为真机控件通过。

原 `.tools/build-winui.ps1`、`.tools/update-session-check.ps1` 等本机脚本不再是正式入口，历史验收记录中的路径只用于解释当时证据；不需要恢复它们才能复跑本入口。

## 本轮验证记录

2026-10-20，在本机 Windows PowerShell 5.1 / .NET 10 环境完成：

- 公开进程 CLI 安全回归：11/11；独立验收员另跑一轮，同样 11/11。真实控制台 Ctrl+C、强杀运行器、超时、正常退出遗留子孙、既有同名进程及合成配置哨兵均通过，不使用 mock。
- 危险 NuGet 缓存入口回归：1/1；确认因 `NugetPackages must not overlap real application data.` 拒绝，且未创建运行产物。
- 从 HEAD 加本任务文件生成独立候选 Git 检出，排除 `.tools/`、旧构建、用户其他未提交改动及本机产物。通过该检出执行正式 `-Mode All`，重新复制、restore、构建，真实 Smoke / Board / Update 全部退出 0；总结果 `Success=true`、`Stage=complete`、`RealDataUnchanged=true`，更新合成 API 请求数为 7。
- 同一干净候选检出的安全回归 11/11、入口保护 1/1，平台无关测试 559/559；独立隔离冒烟回归也已退出 0。
- TDD 中记录了启动失败、非零退出、参数引用、超时和 Ctrl+C 的先失败后通过；其余由 Job 机制直接满足的保护用例首次执行即通过，不伪称逐项均观察过 red。

候选检出用于证明不依赖本机忽略脚本，不代表主分支已提交或远端已发布。本机验证结束时，GitHub Actions 安全 job 已配置但尚未推送触发，未计为云端通过；后续入库及云端结果以 [#32](https://github.com/gzy31007/TJDesktopTimetable/issues/32) 的读回状态和 Actions 运行记录为准。真实拖缩、桌面层、材质、安装器及其他 DPI 人工矩阵不在本次运行器任务范围。原始日志仅保存在本机忽略的隔离产物中。
