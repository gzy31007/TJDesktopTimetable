#!/usr/bin/env python3
"""发布工作流合约测试。

只读 4 个 .github/workflows/*.yml，另外真的执行两段内联脚本：

- release.yml preflight 的 prepare 段（假 gate.py 记录 CLI 参数，验证 tag / fault / 来源判定）；
- 两个平台工作流里的 build-proof 生成段（在临时 git 仓库里生成证明并逐字段核对）。

Gate 自身行为由 test_gate.py 覆盖，这里不重复实现。

依赖策略：CI 的 ubuntu runner 上 yamllint 装在 pipx 隔离环境里，系统 python3 没有 PyYAML，
所以这里自带一个只覆盖本仓库工作流用到的 YAML 子集的解析器（纯标准库，遇到不支持的构造直接报错）。
本机装了 PyYAML 时，LoaderCrossCheck 会交叉核对解析结果与 PyYAML 完全一致。
"""

from __future__ import annotations

import hashlib
import json
import os
import re
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

try:  # 仅用于本机交叉核对；CI 不依赖它
    import yaml
except ImportError:  # pragma: no cover - CI 走这条路
    yaml = None

REPO_ROOT = Path(__file__).resolve().parents[2]
WORKFLOW_DIR = REPO_ROOT / ".github" / "workflows"
RELEASE = "release.yml"
WINDOWS = "release-winui.yml"
LINUX = "release-linux.yml"
CI = "build-csharp.yml"

STABLE_REF = "refs/heads/stable"
REHEARSAL_PREFIX = "refs/heads/release-gate-test/"
CHECKOUT_SHA = "actions/checkout@11d5960a326750d5838078e36cf38b85af677262"

PROOF_KEYS = {"schema", "repo", "sha", "run_id", "run_attempt", "platform", "assets"}
PROOF_ASSET_KEYS = {"name", "size", "sha256"}


# --------------------------------------------------------------------------- YAML 子集解析


class WorkflowYamlError(ValueError):
    """解析器遇到不支持的 YAML 构造；失败而不是猜。"""


BLOCK_INDICATORS = ("|", "|-", "|+", ">", ">-", ">+")


def _is_ignorable(line: str) -> bool:
    return not line.strip() or line.lstrip().startswith("#")


def _split_entry(content: str) -> tuple:
    if ": " in content:
        key, value = content.split(": ", 1)
        return key.strip(), value.strip()
    if content.endswith(":"):
        return content[:-1].strip(), ""
    raise WorkflowYamlError("不支持的 YAML 行（既不是映射也不是列表项）：%r" % content)


def _read_block_scalar(lines: list, index: int, parent_indent: int, indicator: str) -> tuple:
    raw = []
    while index < len(lines):
        line = lines[index]
        if not line.strip():
            raw.append("")
            index += 1
            continue
        indent = len(line) - len(line.lstrip(" "))
        if indent <= parent_indent:
            break
        raw.append(line)
        index += 1
    while raw and not raw[-1].strip():
        raw.pop()
    body_indent = min(
        (len(line) - len(line.lstrip(" ")) for line in raw if line.strip()), default=0
    )
    body = [line[body_indent:] if line.strip() else "" for line in raw]
    if indicator.startswith(">"):
        value = ""
        for line in body:
            if not line.strip():
                value += "\n"
            else:
                if value and not value.endswith("\n"):
                    value += " "
                value += line.strip()
        value = value.strip("\n")
    else:
        value = "\n".join(body)
    if not indicator.endswith("-"):
        value += "\n"
    return value, index


def _tokenize(text: str) -> list:
    lines = text.splitlines()
    tokens = []
    index = 0
    while index < len(lines):
        line = lines[index]
        index += 1
        if _is_ignorable(line):
            continue
        if "\t" in line:
            raise WorkflowYamlError("不支持制表符缩进：%r" % line)
        indent = len(line) - len(line.lstrip(" "))
        content = line.strip()
        if content == "-" or content.startswith("- "):
            item = content[2:].strip() if content.startswith("- ") else ""
            quoted = item.startswith("'") or item.startswith('"')
            if item and not quoted and (": " in item or item.endswith(":")):
                key, value = _split_entry(item)
                if value in BLOCK_INDICATORS:
                    raise WorkflowYamlError("列表项不支持块标量：%r" % item)
                tokens.append(("seqstart", indent))
                tokens.append(("entry", indent + 2, key, "nested" if value == "" else "scalar", value))
            else:
                tokens.append(("item", indent, item))
            continue
        key, value = _split_entry(content)
        if value in BLOCK_INDICATORS:
            value, index = _read_block_scalar(lines, index, indent, value)
            tokens.append(("entry", indent, key, "block", value))
        else:
            tokens.append(("entry", indent, key, "nested" if value == "" else "scalar", value))
    return tokens


def _strip_inline_comment(text: str) -> str:
    """YAML 里未加引号的标量不能含 ' #'，其后是注释；块标量不走这里。"""
    if text.startswith("'") or text.startswith('"'):
        return text
    index = text.find(" #")
    return text if index < 0 else text[:index].rstrip()


def _scalar(text: str):
    stripped = _strip_inline_comment(text.strip())
    if not stripped:
        return None
    if len(stripped) >= 2 and stripped[0] == "'" and stripped[-1] == "'":
        return stripped[1:-1].replace("''", "'")
    if len(stripped) >= 2 and stripped[0] == '"' and stripped[-1] == '"':
        return stripped[1:-1].replace('\\"', '"')
    if stripped.startswith("[") and stripped.endswith("]"):
        inner = stripped[1:-1].strip()
        if not inner:
            return []
        return [_scalar(part) for part in _split_flow(inner)]
    lowered = stripped.lower()
    if lowered == "true":
        return True
    if lowered == "false":
        return False
    if re.fullmatch(r"-?\d+", stripped):
        return int(stripped)
    return stripped


def _split_flow(text: str) -> list:
    parts = []
    current = ""
    quote = ""
    for char in text:
        if quote:
            current += char
            if char == quote:
                quote = ""
        elif char in ("'", '"'):
            quote = char
            current += char
        elif char == ",":
            parts.append(current.strip())
            current = ""
        else:
            current += char
    if current.strip():
        parts.append(current.strip())
    return parts


def _parse_node(tokens: list, index: int, indent: int) -> tuple:
    if tokens[index][0] in ("item", "seqstart"):
        return _parse_seq(tokens, index, indent)
    return _parse_map(tokens, index, indent)


def _parse_map(tokens: list, index: int, indent: int) -> tuple:
    result = {}
    while index < len(tokens):
        kind = tokens[index][0]
        if kind != "entry" or tokens[index][1] != indent:
            break
        _, _, key, kind, value = tokens[index]
        index += 1
        if kind == "nested":
            if index < len(tokens) and tokens[index][1] > indent:
                result[key], index = _parse_node(tokens, index, tokens[index][1])
            else:
                result[key] = None
        elif kind == "block":
            result[key] = value
        else:
            result[key] = _scalar(value)
    return result, index


def _parse_seq(tokens: list, index: int, indent: int) -> tuple:
    result = []
    while index < len(tokens):
        kind = tokens[index][0]
        if tokens[index][1] != indent or kind not in ("item", "seqstart"):
            break
        if kind == "item":
            result.append(_scalar(tokens[index][2]))  # 列表项本身是标量
            index += 1
            continue
        index += 1
        if index < len(tokens) and tokens[index][1] > indent:
            child, index = _parse_node(tokens, index, tokens[index][1])
        else:
            child = None
        result.append(child)
    return result, index


def load_text(text: str) -> dict:
    tokens = _tokenize(text)
    if not tokens:
        raise WorkflowYamlError("空的 YAML")
    if tokens[0][1] != 0:
        raise WorkflowYamlError("顶层缩进必须为 0：%r" % (tokens[0],))
    data, index = _parse_node(tokens, 0, 0)
    if index != len(tokens):
        raise WorkflowYamlError("解析在 %r 处停下，存在未消费的内容" % (tokens[index],))
    if not isinstance(data, dict):
        raise WorkflowYamlError("顶层不是映射")
    return data


def load_workflow(name: str) -> dict:
    return load_text((WORKFLOW_DIR / name).read_text(encoding="utf-8"))


# --------------------------------------------------------------------------- 结构访问


def jobs(workflow: dict) -> dict:
    return workflow["jobs"]


def job_steps(workflow: dict, job: str) -> list:
    steps = jobs(workflow)[job]["steps"]
    if not isinstance(steps, list):
        raise AssertionError("%s 的 steps 不是列表" % job)
    return steps


def find_step(workflow: dict, job: str, needle: str) -> dict:
    for step in job_steps(workflow, job):
        if needle in (step.get("name") or ""):
            return step
    raise AssertionError("%s 里没有名字包含 %r 的步骤" % (job, needle))


def step_run(step: dict) -> str:
    run = step.get("run")
    if not isinstance(run, str):
        raise AssertionError("步骤 %r 没有可执行 run" % (step.get("name"),))
    return run


def step_uses(step: dict) -> str:
    uses = step.get("uses")
    if not isinstance(uses, str):
        raise AssertionError("步骤 %r 不是 uses 步骤" % (step.get("name"),))
    return uses


def uses_steps(workflow: dict, job: str) -> list:
    return [step for step in job_steps(workflow, job) if "uses" in step]


def download_steps(workflow: dict, job: str) -> list:
    return [
        step
        for step in uses_steps(workflow, job)
        if step_uses(step).startswith("actions/download-artifact@")
    ]


def upload_steps(workflow: dict, job: str) -> list:
    return [
        step
        for step in uses_steps(workflow, job)
        if step_uses(step).startswith("actions/upload-artifact@")
    ]


def step_upload_paths(step: dict) -> list:
    raw = step["with"]["path"]
    if isinstance(raw, str):
        return [line.strip() for line in raw.splitlines() if line.strip()]
    raise AssertionError("上传路径不是文本块：%r" % (raw,))


# --------------------------------------------------------------------------- 可执行夹具


FAKE_GATE = """\
import json, os, sys
from pathlib import Path

RECORD = "{record}"
VERSION = "{version}"

args = sys.argv[1:]
Path(RECORD).write_text(json.dumps(args, ensure_ascii=False), encoding="utf-8")


def value(flag):
    return args[args.index(flag) + 1]


tag = value("--tag")
rehearsal = value("--rehearsal") == "true"
run_id = value("--run-id")
want = ("gate-test-" + run_id + "-v" + VERSION) if rehearsal else ("v" + VERSION)
if tag != want:
    sys.exit("tag 必须是 " + want + "，实际 " + tag)

out = os.environ.get("GITHUB_OUTPUT")
if out:
    with open(out, "a", encoding="utf-8") as handle:
        handle.write("version=" + VERSION + "\\ntag=" + tag + "\\n")
"""


class PrepareHarness:
    """在临时目录里执行 release.yml 的 prepare 段，gate.py 换成记录参数的假实现。"""

    def __init__(self, version: str = "1.6.2") -> None:
        self.version = version
        self._tmp = tempfile.TemporaryDirectory()
        self.root = Path(self._tmp.name)
        props = self.root / "dotnet" / "Directory.Build.props"
        props.parent.mkdir(parents=True, exist_ok=True)
        props.write_text(
            "<Project><PropertyGroup><Version>%s</Version></PropertyGroup></Project>\n" % version,
            encoding="utf-8",
        )
        self.record = self.root / "gate-args.json"
        gate = self.root / "scripts" / "release" / "gate.py"
        gate.parent.mkdir(parents=True, exist_ok=True)
        gate.write_text(
            FAKE_GATE.format(record=str(self.record), version=version), encoding="utf-8"
        )

    def close(self) -> None:
        self._tmp.cleanup()

    # 步骤级 env 由 Actions 在跑 run 之前注入；这里照做，并只认已支持的两种表达式，
    # 免得工作流偷偷依赖别的上下文而静态测试看不出来。
    EXPRESSIONS = {
        "${{ github.sha }}": "GITHUB_SHA",
        "${{ github.run_id }}": "GITHUB_RUN_ID",
    }

    def step_env(self, environment: dict) -> dict:
        step = find_step(load_workflow(RELEASE), "preflight", "Prepare")
        for key, raw in (step.get("env") or {}).items():
            if raw in self.EXPRESSIONS:
                environment[key] = environment[self.EXPRESSIONS[raw]]
            elif isinstance(raw, str) and "${{" in raw:
                raise AssertionError("harness 不会展开 %s=%r" % (key, raw))
            else:
                environment[key] = str(raw)
        return environment

    def run(self, env: dict, script: str) -> subprocess.CompletedProcess:
        environment = {
            "PATH": os.environ.get("PATH", "/usr/bin:/bin"),
            "HOME": os.environ.get("HOME", "/tmp"),
            "LANG": "C.UTF-8",
        }
        environment.update(env)
        self.step_env(environment)
        return subprocess.run(
            ["bash", "-c", script],
            cwd=self.root,
            env=environment,
            capture_output=True,
            text=True,
            timeout=120,
        )

    def gate_args(self) -> list:
        if not self.record.exists():
            raise AssertionError("prepare 没有调用 gate.py")
        return json.loads(self.record.read_text(encoding="utf-8"))


def prepare_script() -> str:
    return step_run(find_step(load_workflow(RELEASE), "preflight", "Prepare"))


def prepare_env(ref: str, *, run_id: str = "1001", sha: str = "a" * 40) -> dict:
    name = ref[len("refs/heads/") :] if ref.startswith("refs/heads/") else ref
    return {
        "GITHUB_REF": ref,
        "GITHUB_REF_TYPE": "branch",
        "GITHUB_REF_NAME": name,
        "GITHUB_RUN_ID": run_id,
        "GITHUB_RUN_ATTEMPT": "1",
        "GITHUB_SHA": sha,
        "GITHUB_REPOSITORY": "gzy31007/TJDesktopTimetable",
    }


def init_repo(root: Path, files: dict) -> str:
    for name, payload in files.items():
        path = root / name
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(payload)
    subprocess.run(["git", "init", "-q"], cwd=root, check=True)
    subprocess.run(["git", "add", "-A"], cwd=root, check=True)
    subprocess.run(
        [
            "git",
            "-c",
            "user.email=release@example.invalid",
            "-c",
            "user.name=release gate test",
            "commit",
            "-q",
            "-m",
            "fixture",
        ],
        cwd=root,
        check=True,
    )
    head = subprocess.run(
        ["git", "rev-parse", "HEAD"], cwd=root, check=True, capture_output=True, text=True
    )
    return head.stdout.strip()


def run_proof_script(platform: str, script: str, files: dict, *, sha_override=None):
    tmp = tempfile.TemporaryDirectory()
    root = Path(tmp.name)
    head = init_repo(root, files)
    env = {
        "PATH": os.environ.get("PATH", "/usr/bin:/bin"),
        "HOME": os.environ.get("HOME", "/tmp"),
        "LANG": "C.UTF-8",
        "EXPECTED_SHA": head if sha_override is None else sha_override,
        "GITHUB_REPOSITORY": "gzy31007/TJDesktopTimetable",
        "GITHUB_RUN_ID": "987654321",
        "GITHUB_RUN_ATTEMPT": "3",
    }
    if platform == "windows":
        step_file = root / "step.py"
        step_file.write_text(script, encoding="utf-8")
        proc = subprocess.run(
            [sys.executable, str(step_file)],
            cwd=root,
            env=env,
            capture_output=True,
            text=True,
            timeout=120,
        )
    else:
        step_file = root / "step.sh"
        step_file.write_text(script, encoding="utf-8")
        proc = subprocess.run(
            ["bash", str(step_file)],
            cwd=root,
            env=env,
            capture_output=True,
            text=True,
            timeout=120,
        )
    return tmp, root, head, proc


def proof_script(workflow_name: str) -> str:
    return step_run(find_step(load_workflow(workflow_name), "build", "proof"))


def assert_proof(
    case: unittest.TestCase, proof: dict, *, platform: str, head: str, files: dict
) -> None:
    case.assertEqual(set(proof), PROOF_KEYS)
    case.assertEqual(proof["schema"], 1)
    case.assertEqual(proof["repo"], "gzy31007/TJDesktopTimetable")
    case.assertEqual(proof["sha"], head)
    case.assertEqual(proof["run_id"], "987654321")
    case.assertEqual(proof["run_attempt"], 3)
    case.assertEqual(proof["platform"], platform)
    entries = {entry["name"]: entry for entry in proof["assets"]}
    case.assertEqual(set(entries), set(files), "proof 必须正好列出本平台产物")
    for name, payload in files.items():
        entry = entries[name]
        case.assertEqual(set(entry), PROOF_ASSET_KEYS)
        case.assertEqual(entry["size"], len(payload))
        case.assertEqual(entry["sha256"], hashlib.sha256(payload).hexdigest())


# --------------------------------------------------------------------------- 解析器交叉核对


class LoaderCrossCheck(unittest.TestCase):
    @unittest.skipUnless(yaml is not None, "本机没有 PyYAML；CI 不依赖 PyYAML，跳过交叉核对")
    def test_subset_parser_matches_pyyaml(self) -> None:
        for name in (RELEASE, CI, WINDOWS, LINUX):
            with self.subTest(workflow=name):
                text = (WORKFLOW_DIR / name).read_text(encoding="utf-8")
                expected = yaml.safe_load(text)
                if True in expected:  # YAML 1.1 把裸 on 解析成布尔 True
                    expected["on"] = expected.pop(True)
                self.assertEqual(load_text(text), expected)


# --------------------------------------------------------------------------- release.yml 形状


class ReleaseWorkflowShape(unittest.TestCase):
    def setUp(self) -> None:
        self.workflow = load_workflow(RELEASE)

    def test_only_stable_and_rehearsal_branch_push(self) -> None:
        self.assertEqual(set(self.workflow["on"]), {"push"})
        push = self.workflow["on"]["push"]
        self.assertEqual(set(push), {"branches"})
        self.assertEqual(list(push["branches"]), ["stable", "release-gate-test/**"])

    def test_jobs_are_coordination_only(self) -> None:
        self.assertEqual(
            list(jobs(self.workflow)), ["preflight", "ci", "windows", "linux", "draft"]
        )
        for name, job in jobs(self.workflow).items():
            self.assertNotIn("environment", job, "%s 不得使用人工审批环境" % name)
            self.assertNotIn("secrets", job, "%s 不得注入 secrets" % name)
        draft = jobs(self.workflow)["draft"]
        self.assertEqual(draft["needs"], ["preflight", "ci", "windows", "linux"])
        self.assertNotIn("if", draft, "draft 不得被条件跳过")

    def test_only_draft_may_write(self) -> None:
        self.assertEqual(self.workflow["permissions"], {"contents": "read"})
        for name, job in jobs(self.workflow).items():
            permissions = job.get("permissions")
            if name == "draft":
                self.assertEqual(permissions, {"contents": "write", "actions": "read"})
            else:
                self.assertIn(
                    permissions,
                    (None, {"contents": "read"}),
                    "%s 只能只读（实际 %r）" % (name, permissions),
                )

    def test_concurrency_is_repo_wide_and_serial(self) -> None:
        concurrency = self.workflow["concurrency"]
        self.assertIsInstance(concurrency, dict)
        self.assertEqual(concurrency["group"], "release-coordination")
        self.assertNotIn("${{", concurrency["group"], "并发组必须固定，不能按 ref 拆开")
        self.assertIs(concurrency["cancel-in-progress"], False)

    def test_no_publish_or_approval_job(self) -> None:
        names = set(jobs(self.workflow))
        self.assertEqual(names & {"publish", "approve", "approval", "rehearsal-review"}, set())
        text = json.dumps(self.workflow, ensure_ascii=False)
        self.assertNotIn("gate.py publish", text)


# --------------------------------------------------------------------------- preflight


class PreflightContract(unittest.TestCase):
    def setUp(self) -> None:
        self.workflow = load_workflow(RELEASE)
        self.preflight = jobs(self.workflow)["preflight"]
        self.checkout = job_steps(self.workflow, "preflight")[0]
        self.prepare = find_step(self.workflow, "preflight", "Prepare")

    def test_checkout_pins_event_sha_with_history(self) -> None:
        self.assertEqual(step_uses(self.checkout), CHECKOUT_SHA)
        self.assertEqual(self.checkout["with"]["ref"], "${{ github.sha }}")
        self.assertEqual(self.checkout["with"]["fetch-depth"], 0)
        self.assertIs(self.checkout["with"]["persist-credentials"], False)

    def test_prepare_uses_source_record_fetch(self) -> None:
        script = step_run(self.prepare)
        self.assertIn("scripts/release/gate.py prepare", script)
        self.assertIn("--fetch-source", script)
        self.assertIn('--sha "$SOURCE_SHA"', script)
        self.assertEqual(self.prepare["env"]["SOURCE_SHA"], "${{ github.sha }}")
        self.assertIn("set -euo pipefail", script)

    def test_version_comes_from_candidate_props(self) -> None:
        script = step_run(self.prepare)
        self.assertIn("dotnet/Directory.Build.props", script)
        self.assertNotIn("1.6", script, "preflight 不得硬编码版本")

    def test_outputs_are_gate_prepare_outputs(self) -> None:
        outputs = self.preflight["outputs"]
        for key in ("version", "tag", "sha", "rehearsal", "fault"):
            self.assertEqual(outputs[key], "${{ steps.prepare.outputs.%s }}" % key)
        self.assertEqual(self.prepare["id"], "prepare")


class PreflightExecution(unittest.TestCase):
    def setUp(self) -> None:
        self.harness = PrepareHarness()
        self.addCleanup(self.harness.close)
        self.script = prepare_script()

    def test_stable_push_produces_production_tag(self) -> None:
        proc = self.harness.run(prepare_env(STABLE_REF), self.script)
        self.assertEqual(proc.returncode, 0, proc.stderr)
        args = self.harness.gate_args()
        self.assertEqual(args[0], "prepare")
        self.assertIn("--fetch-source", args)
        self.assertEqual(args[args.index("--sha") + 1], "a" * 40)
        self.assertEqual(args[args.index("--tag") + 1], "v1.6.2")
        self.assertEqual(args[args.index("--run-id") + 1], "1001")
        self.assertEqual(args[args.index("--rehearsal") + 1], "false")
        self.assertEqual(args[args.index("--fault") + 1], "none")

    def test_rehearsal_success_uses_gate_test_tag(self) -> None:
        proc = self.harness.run(prepare_env(REHEARSAL_PREFIX + "b42/success"), self.script)
        self.assertEqual(proc.returncode, 0, proc.stderr)
        args = self.harness.gate_args()
        self.assertEqual(args[args.index("--tag") + 1], "gate-test-1001-v1.6.2")
        self.assertEqual(args[args.index("--rehearsal") + 1], "true")
        self.assertEqual(args[args.index("--fault") + 1], "none")

    def test_faults_are_rehearsal_only(self) -> None:
        for scenario in ("missing-asset", "platform-failure"):
            with self.subTest(scenario=scenario):
                proc = self.harness.run(
                    prepare_env(REHEARSAL_PREFIX + "b42/" + scenario), self.script
                )
                self.assertEqual(proc.returncode, 0, proc.stderr)
                args = self.harness.gate_args()
                self.assertEqual(args[args.index("--fault") + 1], scenario)
                self.assertEqual(args[args.index("--tag") + 1], "gate-test-1001-v1.6.2")

    def test_version_mismatch_rehearsal_is_rejected(self) -> None:
        proc = self.harness.run(
            prepare_env(REHEARSAL_PREFIX + "b42/version-mismatch"), self.script
        )
        self.assertNotEqual(proc.returncode, 0, "version-mismatch 必须让 preflight 失败")
        self.assertIn("tag", proc.stderr)

    def test_unknown_ref_is_rejected_before_gate(self) -> None:
        for ref in ("refs/heads/main", "refs/tags/v1.6.2", REHEARSAL_PREFIX + "b42/unknown"):
            with self.subTest(ref=ref):
                proc = self.harness.run(prepare_env(ref), self.script)
                self.assertNotEqual(proc.returncode, 0, "%s 必须被拒绝" % ref)


# --------------------------------------------------------------------------- 调用链


class InvocationContract(unittest.TestCase):
    def setUp(self) -> None:
        self.workflow = load_workflow(RELEASE)

    def test_ci_and_platforms_pin_preflight_sha(self) -> None:
        expected = "${{ needs.preflight.outputs.sha }}"
        for name, workflow in (("ci", CI), ("windows", WINDOWS), ("linux", LINUX)):
            with self.subTest(job=name):
                job = jobs(self.workflow)[name]
                self.assertEqual(job["uses"], "./.github/workflows/%s" % workflow)
                self.assertEqual(job["with"]["sha"], expected)
                self.assertEqual(job["needs"], "preflight")

    def test_linux_fault_only_for_platform_failure(self) -> None:
        linux = jobs(self.workflow)["linux"]
        self.assertEqual(
            linux["with"]["fail-build"],
            "${{ needs.preflight.outputs.fault == 'platform-failure' }}",
        )
        self.assertEqual(set(jobs(self.workflow)["windows"]["with"]), {"sha"})

    def test_build_csharp_accepts_optional_sha(self) -> None:
        call = load_workflow(CI)["on"]["workflow_call"]
        self.assertIn("sha", call["inputs"])
        sha_input = call["inputs"]["sha"]
        self.assertIs(sha_input.get("required", False), False)
        self.assertEqual(sha_input["type"], "string")
        self.assertEqual(sha_input.get("default"), "")

    def test_build_csharp_checkouts_honour_input_sha(self) -> None:
        workflow = load_workflow(CI)
        refs = [
            step["with"]["ref"]
            for job in jobs(workflow)
            for step in job_steps(workflow, job)
            if "uses" in step and step_uses(step).startswith("actions/checkout@")
        ]
        self.assertTrue(refs, "build-csharp 至少有一个 checkout")
        for ref in refs:
            self.assertEqual(ref, "${{ inputs.sha || github.sha }}")

    def test_gate_tests_run_every_release_test_module(self) -> None:
        workflow = load_workflow(CI)
        step = find_step(workflow, "release-gate-tests", "Release gate regression")
        self.assertIn(
            "python3 -m unittest discover -s scripts/release -p 'test_*.py' -v", step_run(step)
        )


# --------------------------------------------------------------------------- draft


class DraftContract(unittest.TestCase):
    def setUp(self) -> None:
        self.workflow = load_workflow(RELEASE)
        self.steps = job_steps(self.workflow, "draft")

    def test_checkout_pins_preflight_sha(self) -> None:
        checkout = self.steps[0]
        self.assertEqual(step_uses(checkout), CHECKOUT_SHA)
        self.assertEqual(checkout["with"]["ref"], "${{ needs.preflight.outputs.sha }}")
        self.assertIs(checkout["with"]["persist-credentials"], False)

    def test_artifacts_are_attempt_scoped_and_not_merged(self) -> None:
        downloads = download_steps(self.workflow, "draft")
        self.assertEqual(len(downloads), 2)
        seen = {}
        for step in downloads:
            with_block = step["with"]
            self.assertNotIn("pattern", with_block, "禁止通配下载")
            self.assertNotIn("merge-multiple", with_block, "禁止把平台产物摊平合并")
            seen[with_block["name"]] = with_block["path"]
        self.assertEqual(
            seen,
            {
                "release-windows-${{ github.run_id }}-${{ github.run_attempt }}": "downloaded/windows",
                "release-linux-${{ github.run_id }}-${{ github.run_attempt }}": "downloaded/linux",
            },
        )

    def test_missing_asset_rehearsal_omits_linux_download_not_a_fake_gate_error(self) -> None:
        windows, linux = download_steps(self.workflow, "draft")
        self.assertNotIn("if", windows)
        self.assertEqual(linux.get("if"), "${{ needs.preflight.outputs.fault != 'missing-asset' }}")

    def test_draft_uses_same_context_and_fault(self) -> None:
        step = find_step(self.workflow, "draft", "Create draft")
        script = step_run(step)
        self.assertIn("scripts/release/gate.py draft", script)
        for flag in ("--sha", "--tag", "--run-id", "--rehearsal", "--fault", "--assets", "--manifest"):
            self.assertIn(flag, script, "draft 缺少 %s" % flag)
        self.assertEqual(step["env"]["SOURCE_SHA"], "${{ needs.preflight.outputs.sha }}")
        self.assertEqual(step["env"]["RELEASE_TAG"], "${{ needs.preflight.outputs.tag }}")
        self.assertEqual(step["env"]["REHEARSAL"], "${{ needs.preflight.outputs.rehearsal }}")
        self.assertEqual(step["env"]["FAULT"], "${{ needs.preflight.outputs.fault }}")
        self.assertIn("--assets downloaded", script)
        self.assertIn("--manifest release-manifest.json", script)

    def test_manifest_artifact_is_attempt_scoped(self) -> None:
        uploads = upload_steps(self.workflow, "draft")
        self.assertEqual(len(uploads), 1, "协调侧只上传一份 manifest 交接记录")
        upload = uploads[0]
        self.assertEqual(
            upload["with"]["name"],
            "release-manifest-${{ github.run_id }}-${{ github.run_attempt }}",
        )
        self.assertEqual(step_upload_paths(upload), ["release-manifest.json"])
        self.assertEqual(upload["with"]["if-no-files-found"], "error")


# --------------------------------------------------------------------------- 平台 artifact


class PlatformArtifactContract(unittest.TestCase):
    def _check(self, workflow_name: str, platform: str) -> None:
        workflow = load_workflow(workflow_name)
        self.assertEqual(workflow["permissions"], {"contents": "read"})
        uploads = upload_steps(workflow, "build")
        self.assertEqual(len(uploads), 1, "%s 只应有一个上传步骤" % workflow_name)
        upload = uploads[0]
        self.assertEqual(
            upload["with"]["name"],
            "release-%s-${{ github.run_id }}-${{ github.run_attempt }}" % platform,
        )
        paths = step_upload_paths(upload)
        self.assertIn("build-proof.json", paths, "证明必须进同一个 artifact")
        self.assertGreaterEqual(len(paths), 2)

    def test_windows_artifact(self) -> None:
        self._check(WINDOWS, "windows")

    def test_linux_artifact(self) -> None:
        self._check(LINUX, "linux")


class BuildProofSourceContract(unittest.TestCase):
    def test_windows_proof_script_contract(self) -> None:
        script = proof_script(WINDOWS)
        for token in (
            "build-proof.json",
            "hashlib",
            '"schema"',
            "GITHUB_REPOSITORY",
            "GITHUB_RUN_ID",
            "GITHUB_RUN_ATTEMPT",
            "rev-parse",
            '"platform": "windows"',
            "sha256",
            "size",
        ):
            self.assertIn(token, script)
        self.assertEqual(find_step(load_workflow(WINDOWS), "build", "proof").get("shell"), "python")

    def test_linux_proof_script_contract(self) -> None:
        script = proof_script(LINUX)
        for token in (
            "build-proof.json",
            "hashlib",
            '"schema"',
            "GITHUB_REPOSITORY",
            "GITHUB_RUN_ID",
            "GITHUB_RUN_ATTEMPT",
            "rev-parse",
            '"platform": "linux"',
            "python3",
        ):
            self.assertIn(token, script)
        self.assertNotIn("shell", find_step(load_workflow(LINUX), "build", "proof"))


class BuildProofExecution(unittest.TestCase):
    WINDOWS_FILES = {
        "TJDesktopTimetable-v1.6.2-win-x64.zip": b"windows zip payload" * 32,
        "TJDesktopTimetable-v1.6.2-setup.exe": b"windows setup payload" * 40,
    }
    LINUX_FILES = {"TJDesktopTimetable-v1.6.2-linux-x64.tar.gz": b"linux tar payload" * 24}

    def _run(self, platform: str, files: dict, *, sha_override=None):
        workflow = WINDOWS if platform == "windows" else LINUX
        tmp, root, head, proc = run_proof_script(
            platform, proof_script(workflow), files, sha_override=sha_override
        )
        self.addCleanup(tmp.cleanup)
        return root, head, proc

    def test_windows_proof_generation(self) -> None:
        root, head, proc = self._run("windows", self.WINDOWS_FILES)
        self.assertEqual(proc.returncode, 0, proc.stderr)
        proof = json.loads((root / "build-proof.json").read_text(encoding="utf-8"))
        assert_proof(self, proof, platform="windows", head=head, files=self.WINDOWS_FILES)

    def test_linux_proof_generation(self) -> None:
        root, head, proc = self._run("linux", self.LINUX_FILES)
        self.assertEqual(proc.returncode, 0, proc.stderr)
        proof = json.loads((root / "build-proof.json").read_text(encoding="utf-8"))
        assert_proof(self, proof, platform="linux", head=head, files=self.LINUX_FILES)

    def test_proof_rejects_wrong_checkout_sha(self) -> None:
        root, _head, proc = self._run("windows", self.WINDOWS_FILES, sha_override="b" * 40)
        self.assertNotEqual(proc.returncode, 0, "proof 必须拒绝与 checkout 不符的 SHA")
        self.assertFalse((root / "build-proof.json").exists())


if __name__ == "__main__":
    unittest.main()
