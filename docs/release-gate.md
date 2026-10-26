# stable 候选到受检草稿

本轮为 [#42](https://github.com/gzy31007/TJDesktopTimetable/issues/42) 的中间交付，复用 #33 的统一编排与门禁，不启用真实公开。自动公开政策由 #43 替换，最终同候选真实 Actions 验收由 #44 完成。本地通过不代表工作流已上线。

## 唯一编排与来源

入口为 `.github/workflows/release.yml`，仅接收 stable 或专用演练分支的 push；不接收生产标签、手工 dispatch 或首次初始化事件。stable 的初始化提交没有晋级记录，门禁会拒绝，即使 GitHub 将首次创建该分支也表达为 push。

1. 固定事件 SHA，读取该提交的唯一 Version 与对应非空发布说明，不自动 bump。生产路径调用 #41 的 `scripts/git/promote.py --verify-target`，核验原始单父提交、目标树、来源 SHA、stable base、origin、版本和发布说明关联，不用可移动 release 分支当前值代替来源。
2. stable 的树复制提交不以来源提交为祖先。协调步骤先按记录里的完整来源 SHA 补取缺失对象（不取“最近 release 成功版本”），然后通过 #41 原始对象验证；来源对象不可取时停止。Git replace 不参与对象身份判定。
3. 同提交 reusable CI 运行双系统核心测试、验收运行器安全回归和两外壳编译；平台构建同样检出事件 SHA。所有必需 job 仅明确 success 可通过，失败、取消、跳过和未知不算成功。
4. Windows/Linux 仅读取代码并上传本轮 Actions artifacts；只有 draft 协调 job 取得必要写权限。下载按平台隔离、不合并目录，也不查询另一次最近成功运行。
5. 先检查本地包与双平台证明，再核对当前 run/attempt、仓库和分支，以及保留的人工环境政策。分页枚举 Release：同标签的既有草稿或公开版本一律拒绝，不接管、不补传。
6. 无冲突才创建或核对标签，并且仅创建 draft=true 的全新 Release。按响应 release_id 上传、分页枚举并核对完整资产，再读回草稿身份/发布说明与标签，成功后原子写入 manifest。

生产标签为 v<Version>，绑定受检 stable SHA；只允许不存在时创建或已有同 SHA 时核对，不移动/删除。上传失败可能留下孤立标签或部分草稿，停止且保留现场；已有同 SHA 孤立标签可以再次核对，但已有 Release 不能重用。标签创建与打包、草稿在同次编排中完成，不依赖 GITHUB_TOKEN 创建标签触发旧工作流。使用全局并发组串行协调不同 stable/演练分支的版本竞争；外部管理员写入不在此锁内。

## 包与构建证明

唯一发布资产名称：

- TJDesktopTimetable-v<Version>-setup.exe
- TJDesktopTimetable-v<Version>-win-x64.zip
- TJDesktopTimetable-v<Version>-linux-x64.tar.gz

Actions artifact 分别为 release-windows-<run_id>-<attempt> 与 release-linux-<run_id>-<attempt>，落在 downloaded/windows 与 downloaded/linux。每个平台额外携带 build-proof.json（证明文件不是 Release 资产）：

```json
{"schema":1,"repo":"owner/repo","sha":"实际检出完整 SHA","run_id":"本次运行 ID","run_attempt":1,"platform":"windows 或 linux","assets":[{"name":"精确包名","size":123,"sha256":"64 位摘要"}]}
```

证明来自本轮实际构建，协调时逐字绑定 repo/SHA/run/attempt/platform，与磁盘包大小和 SHA256 交叉核对。Windows 恰好两个包，Linux 恰好一个包；拒绝额外根目录/文件、旧证明、缺失、重复、空文件、符号链接、上传未完成和大小/摘要差异。服务端没有 digest 时按 asset_id 下载校验，不能直接放行。预发布版本保持 prerelease 身份。

## manifest 交接契约

成功草稿输出 schema=2 的 release-manifest-<run_id>-<attempt>，包含：

- repo、sha、version、tag、run_id（字符串）、run_attempt（正整数）、ref、event=push、rehearsal。
- provenance_kind=promotion 时 source_sha 为 #41 复核的来源，stable_sha=sha，release_ref 和 release_notes_path 取晋级记录。
- provenance_kind=rehearsal 时 source_sha=sha、stable_sha=null、release_ref=null；不伪造生产晋级证明。
- builds.windows / builds.linux 保存以上两份完整构建证明。
- release_id、release.name/body/prerelease，以及 expected 三条 name/size/sha256/asset_id，绑定上传后服务端读回的身份及摘要。

#43 必须按此新版契约复核来源与本轮身份后消费草稿。历史 read_manifest/publish 辅助函数为 #33 的旧契约回归保留，不是可用的新公开路径；CLI publish 在读取凭据或调用 API 前无条件拒绝，工作流也没有 publish/审批/bypass job。不得绕过冻结直接调用旧辅助函数来发版。

## 既有环境政策与信任边界

本中间版本仍只读检查 release 环境真实存在，required_reviewers 唯一为 User gzy31007；不会代批、bypass 或更改保护政策。该要求不表示本轮有公开步骤。旧政策正式替换属于 #43。

contents:write 不是“只能建草稿”的平台权限。管理员、外部写令牌或任意修改工作流的人仍可越过本控制面；打包程序不在写 token runner 上执行，GH_TOKEN 仅传协调脚本。API 核验与后续写入不是跨服务原子事务，脚本只能拒绝可观测的漂移，不能声称消灭外部写者竞态。API/分页/JSON/网络异常一律停止，不把未知当不存在。

## 隔离演练与验证边界

专用分支 release-gate-test/<批次>/<场景> 仍走同一编排，支持 version-mismatch、missing-asset、platform-failure、success；分别验证版本拒绝、缺资产拒绝、单平台失败、完整受检草稿。演练不要求生产晋级记录，但要求固定 SHA、干净版本和非空发布说明。使用 gate-test-<run_id>-v<Version> 标签，永远是 draft，不是“公开 prerelease”。YAML 不含公开 job，Python CLI 同时冻结所有公开，包括误批后的演练。重跑 attempt 不能拼接旧构建证明；新 push 才得到新 run_id。真实运行只可隔离演练，不能推真实 stable 或触发旧生产入口。

本地检查：

```bash
python3 -m unittest discover -s scripts/release -p 'test_*.py'
python3 -m unittest discover -s scripts/git -p 'test_*.py'
python3 -m py_compile scripts/release/gate.py scripts/release/test_*.py
dotnet test dotnet/TjtTimetable.slnx
```

本地 Git fixtures/API 替身和工作流静态契约测试不等于 GitHub Actions、Windows 打包或真实来源对象跨网络补取已验证。#44 负责最终同候选真实链路证据；本轮不扩张 GUI、安装升级或真机矩阵。

历史提交与历史 run 的工作流不会追溯重写。正式切换须继续审查旧入口退役、历史重跑权限和标签政策，不能表述为“仓库所有历史发布入口已被平台强制封死”。
