# 将 release 候选复制到 stable

本页用于逐候选批准后的操作与复核。实现是 [promote.py](../scripts/git/promote.py)，合约测试是 [test_promote.py](../scripts/git/test_promote.py)。首次创建版本线用 [bootstrap.py](../scripts/git/bootstrap.py)；日常任务仍按 [Git 工作流](git-workflow.md) 合并回 release，二者都不代表获准晋级。

## 批准之前

维护者先验收一个固定的 release 提交，在该提交中准备统一版本号和非空发布说明。批准必须绑定源 SHA、旧 stable SHA、版本、说明路径与验收证据。release 分支后来前移就重新验收、重新批准，不把旧批准套到新 tip。

脚本读取源提交的完整 tree，创建唯一父提交为旧 stable 的新提交。它不是开发分支 merge，不挑文件，不读 index 或工作区内容。源提交中的删除、点文件和二进制与其他已跟踪内容一起复制；开发中未提交内容保持原样。版本从源提交的 [Directory.Build.props](../dotnet/Directory.Build.props) 读取，版本 major.minor 必须对应 release 分支。提交消息保存批准与来源记录，目标 SHA 在命令回执中返回，避免自引用。

批准 JSON 是维护者声明，不是签名或权限认证。脚本只验证声明的结构和仓库事实，不验证证据结论是否通过、链接是否可信，也不替代维护者批准。批准文件使用公开身份与证据引用，不放凭据或个人数据。approved_by_email 用于确定性 Git 作者和提交者身份，可使用公开 noreply 地址。

## 操作

1. 核实 origin 的 fetch/push URL、release 与 stable 的本地及远端完整 SHA。源与基线提交对象必须已在本地；本工具不自动 fetch、checkout 或 reset。若 stable 在任何 worktree 被检出，先安全切到任务分支；不要为运行脚本新建源码副本。
2. 维护者对确切候选给出批准文件。以下仅是**不可执行的格式示意**，所有尖括号值必须来自本次批准，不能复制历史 SHA、猜测证据或替维护者填写批准：

schema 为整数 1，evidence 为非空字符串数组。仅允许以下键。示例中的尖括号使 SHA 与版本校验必然失败。

	{"schema":1,"origin":"https://github.com/gzy31007/TJDesktopTimetable.git","release_ref":"refs/heads/release/1.6","source_sha":"<已验收的40位小写SHA>","stable_base_sha":"<批准时stable的40位小写SHA>","version":"<源内版本>","release_notes_path":"docs/releases/<源内说明文件>.md","evidence":["<本次验收证据引用>"],"approved_by":"<维护者公开身份>","approved_by_email":"<公开Git邮箱>","approved_at":"<带时区的ISO8601时间>","approval_reference":"<明确批准的引用>"}

3. 先运行计划，核对回执中的源、tree、版本、说明、基线、target 与批准。默认模式不写对象、refs、index 或工作区，只用临时对象目录计算目标 SHA：

	python3 scripts/git/promote.py --approval /path/to/approval.json

4. **只有另行获准真实晋级时**，追加 --apply 创建对象并推进本地 stable，或追加 --apply --push 同时推送。写入使用同一 update-ref 事务验证本地 release 并对 stable 做 CAS；推送只更新 stable，使用精确旧 SHA lease、原子推送且不附带标签。目标经验证只有旧 stable 一个父提交，虽使用 lease 选项，也不能把远端改成无关历史。
5. 保存完整 JSON 回执，再从实际目标提交复核。该模式核验来源记录、原始提交头的唯一父提交、源与目标 tree、版本及说明，不依赖旧回执，也不鉴定批准真实性或发布结果。对象读取禁用 refs/replace，父链直接读取原始 commit，不采信 grafts 的历史视图：

	python3 scripts/git/promote.py --verify-target <完整目标SHA>

6. 读回本地及远端 stable，并核对 GitHub 上的运行或发布事实。只有这次真正推送成功才有回执 event；重复 no-op 不伪造一次新事件。

--expected-origin 只用于显式审计过的替代远端（例如合成 fixture），不能作为绕过真实仓库身份检查的日常选项。所有模式的参数以 --help 为准。

## 拒绝、并发与重跑

错误 fetch/push URL、缺批准、版本不明、源分支不等于获批 SHA、说明缺失/空/符号链接、stable 不在批准基线或同一候选上，均拒绝。目标存在无关提交时先停下核对，不强推恢复。远端 stable 必须已存在，首次创建走初始化流程。

同一批准的内容、作者与时间生成同一目标 SHA。本地与远端已在该目标时重跑保持 no-op。推送失败或结果不明保留本地候选，不自动回滚；先读回再按同一批准重跑。远端已在候选、本地还在基线或丢失时需人工协调本地状态，脚本不擅自修复。

远端源与目标在初检、写前及推前/后读回；本地 release 验证与 stable CAS 同事务。远端目标并发变化由精确 lease 拒绝。远端源的最后一次读取与跨仓推送之间仍有竞态窗口，无法声称本地与远端所有 refs 构成一个事务；推后发现源漂移会报错，但已经成功的 stable 推送可能保留。任何失败均按实际 refs 取证，不自动撤销别人提交或重新解释批准。

## 后续 Actions 的触发契约

本票只交付 Git 晋级与可追溯候选，**没有接通生产 Actions**。回执 actions.wired=false 是明确状态，不是发布成功。后续 #42 至 #44 的接入约定如下：

- 入口是维护者受信本地凭据对 refs/heads/stable 的真实 push，不由工作流内 GITHUB_TOKEN 推送串起另一工作流。本工具不检查凭据类型，运行者必须核实凭据来源；若改为 GitHub App/PAT，要在对应票落实授权，不把密钥放批准文件。
- 不使用或声称已实现 workflow_dispatch。GitHub 对 GITHUB_TOKEN 的递归触发有限制，参见 [官方触发说明](https://docs.github.com/en/actions/how-tos/write-workflows/choose-when-workflows-run/trigger-a-workflow)。
- 后续流水线检查 event=push、ref=refs/heads/stable，并固定事件的 github.sha（不是稍后再取 stable tip）。从该 SHA 复核晋级记录，将 source、target、version、批准及验收引用贯穿测试、双平台构建、草稿和资产核验。
- #44 用隔离入口真实取证 event/ref/SHA/run ID/run attempt，隔离入口映射到相同受检逻辑但不公开版本；本地 JSON event 不能当作已发生 GitHub 运行的证据。生产启用仍受 #44 的无发版上线条件约束。

stable tip 只表示候选。测试/构建失败或只有草稿时均未发布；正式发布事实以同一受检提交对应的正式 Release、不可变版本标签及资产核验为准。不得因本脚本退出成功、tree 相同或 stable 已前移就宣布发布。

## 本票验证边界

合成 fixture 通过公共 CLI 观察 refs、父链、tree、记录、index 和 dirty 文件，运行 python3 -m unittest discover -s scripts/git -p 'test_*.py' -v。竞态用临时 Git 包装或远端接收钩子注入，不访问真实远端、不触发 Actions、不创建真实版本。

#41 不执行真实 stable 晋级，不公开 1.6.2 或其他版本，不改 main、默认分支、保护规则或包内课表形式。脚本验收不代替 #42 至 #44 的真实隔离 Actions 与发布门禁验收。
