# 发布硬门禁

本流程用于新编排的可信发布，防止测试未完成、版本不一致或资产不完整时提前公开。它不把 GitHub 的 `contents:write` 变成“只能创建草稿”的权限，也不能限制仓库管理员、被盗写令牌或任意改动工作流的人。

## 发布链路

唯一入口为 `.github/workflows/release.yml`。真实 `v*` 标签触发后：

1. 从固定提交解析 `dotnet/Directory.Build.props` 的唯一版本，要求标签严格等于 `v<Version>`，标签解引用必须指向该提交。
2. 调用同提交的 `build-csharp.yml`，复用双系统核心测试、验收运行器安全回归与两外壳编译。不是查询另一次“同 SHA 最近成功”的运行，也不依赖 push 的路径过滤。发布门禁 Python 回归也必须成功。
3. 同时调用两个平台构建工作流。它们只有 `contents:read`，只上传本次运行的 Actions artifacts，不创建或更新 Release。
4. 所有前置 job 明确成功后，统一 `draft` job 校验本地三资产，分页枚举 Release 拒绝同标签的既有草稿或公开版本，再创建草稿、按新建 `release_id` 上传和重新枚举服务端资产，核对名称、非空大小与 SHA256。不能用只按 tag 的查询证明草稿不存在。缺少 API digest 时下载校验，不能把缺少 digest 当成功。任何错误都不转正式。
5. 成功的完整快照写入 `release-manifest` Actions artifact。`publish` job 使用 GitHub 原生 `release` 环境，等待人工审批。
6. 人工批准后，从固定 SHA 检出，再次检查当前运行的成功 job、环境审批规则、标签、草稿身份和资产快照。只在所有检查仍成立时将同一草稿转为正式；不重建、不补传、不覆盖资产。

必需资产为以下精确名称，不采用通配符找到第一项的策略：

- `TJDesktopTimetable-v<Version>-setup.exe`
- `TJDesktopTimetable-v<Version>-win-x64.zip`
- `TJDesktopTimetable-v<Version>-linux-x64.tar.gz`

额外资产、同名重复、空文件、符号链接、未完成上传、大小或摘要不同均失败。带预发布标识的版本保持 `prerelease`，不让 beta 被当作正式最新版。发布说明优先取 `docs/releases/v<Version>.md`，没有时用固定简短说明。

## 人工审批配置

`release` 环境必须真实存在，且 `required_reviewers` 规则的唯一审批人为 User `gzy31007`；额外用户或团队也会拒绝，避免“任一其他人批准即可”放宽约定。脚本在草稿前与批准后都会检查，不能只靠 YAML 的 `environment: release`。配置 API 见 [GitHub 环境文档](https://docs.github.com/en/rest/deployments/environments)。

本仓库单维护者兼任触发人和审批人，因此 `prevent_self_review=false`，仍需人在 GitHub 点击批准。不得用代理调用审批或 bypass API。环境当前允许管理员手动 bypass，属于管理员能力边界，不是自动发布通道；具备管理权限的人同样能修改规则与工作流。

审批前检查运行 SHA、版本、三资产摘要与发布说明。草稿 job 在独立 runner 上只做可信协调，不运行打包后的程序、不继承业务秘密；`GH_TOKEN` 只传给门禁脚本步骤。草稿 job 的写 token 技术上仍有公开权限，控制面代码和受信 Actions 也是信任边界。

GitHub 资产核验与转正式 PATCH 不是原子事务。本工作流按 ref 串行，不支持其他写者在审批期间修改同一草稿。脚本拒绝可观测到的改动，但不声称消灭管理员或外部写 token 在最后一次校验与 PATCH 之间的竞态。

## 故障演练

为了不用先合入主分支就验证新工作流，专用分支 `release-gate-test/<批次>/<场景>` 的 push 会触发同一发布编排。有效场景只有：

- `version-mismatch`：故意给出与 props 不同的候选标签，preflight 必须失败。
- `missing-asset`：真实构建成功，但收尾只下载 Windows 资产，必须因缺 Linux 资产失败。
- `platform-failure`：Linux 平台 job 主动非零退出，草稿及发布 job 必须跳过。
- `success`：完整真实构建与草稿读回成功，只读审核 job 停留在人工环境审批等待；真正的公开 job 在 YAML 层就跳过。

前三项均不得公开 Release。对照草稿用 `gate-test-<run_id>-v<Version>` 专属标签，指向本次 SHA；不是生产 `v*` 标签。演练也不是“公开的 prerelease”，必须始终为 draft。即使人工误批准，Python 发布入口仍拒绝演练、分支或非生产标签，不能公开。代理不得批准对照运行。

同一候选提交可以推至四个场景分支，不需要改业务代码来注入故障。不要打旧提交的 `v*` 标签做负向测试，不要运行旧发布工作流。

## 失败、取消与重跑

API 权限、网络、JSON、校验或前置结果不明一律失败。失败/取消/跳过都不是成功。上传或取消后的残留草稿保留用于排查，不自动清理、修补或接管。既有公开版本完全拒绝写入；既有草稿也拒绝覆写，无 `--clobber`。

门禁绑定 run attempt，拒绝失败 job 重跑拼接不同 attempt 的证明。需要重新演练时触发一个新 push 运行，得到新 run_id。生产部分失败后的恢复由维护者核对专属草稿与标签后处理，不直接重跑公开 job 来绕过完整快照与审批。

本地 Python 回归：

```bash
python3 -m unittest discover -s scripts/release -p 'test_*.py' -v
```

核心回归仍为 `dotnet test dotnet/TjtTimetable.slnx`。云端编译与合成安全回归不代替 Windows/Linux 真机行为验收，本任务不扩张到 GUI 或安装升级矩阵。

## 上线边界

只推测试分支不等于主分支生效。需要人工审查并合入主分支后，未来基于新提交的版本标签才使用本门禁。本轮不得推送 main、发布真实版本或改动历史 Release。

历史提交与历史 Actions run 留有旧工作流定义，不会被新 YAML 追溯改写。上线切换须审查旧入口退役、历史重跑权限及标签策略，禁止用旧 run 或旧提交发版；环境审批只能保护声明该环境的 job，不能阻止所有写权限者直接调用 Release API。不要将本次新链路验收表述成“仓库所有历史发布路径已被平台强制封死”。

本轮实施与真实 Actions 证据以[发布硬门禁任务](https://github.com/gzy31007/TJDesktopTimetable/issues/33)的最新正文为准。
