#!/usr/bin/env python3
"""发布硬门禁（release gate）。

只在 ubuntu 协调阶段使用，三个子命令串起一次发布：

    gate.py prepare  --sha SHA --tag TAG --run-id RUN_ID --rehearsal true|false [--fault F]
    gate.py draft    --sha SHA --tag TAG --run-id RUN_ID --rehearsal true|false --assets DIR --manifest FILE
    gate.py publish  --sha SHA --tag TAG --run-id RUN_ID --rehearsal true|false --manifest FILE

`prepare` 只做本地事实校验（版本、标签、git HEAD、触发来源），不碰 GitHub API；
`draft` 先把「本次 attempt 的 CI job 全部成功」「release 环境有指定审核器」「tag 落在
这个 sha 上」「同 tag 没有既存 Release」都验过，才用 API 建 draft Release 并上传三件资产，
读回核验通过后才写本地 manifest（并把服务端 release body/title/prerelease 与每个 asset 的
id 一起绑进去）；`publish` 重新核验 manifest / 触发来源 / run & jobs / 环境 / tag / 服务端
资产与 release 快照，最后才 PATCH draft=false，再读回公开状态。演练永远不允许公开。

几条贯穿全局的规矩：

* 版本号唯一来源是 `dotnet/Directory.Build.props` 里唯一那一个 `<Version>`，标签严格是
  `v<Version>`；演练标签严格是 `gate-test-<run_id>-v<Version>`。
* 同一 tag 已有任何 Release（draft 或已公开）一律拒绝，重跑宁可失败，也不 clobber；
  因此只支持**整轮 rerun**（run id 本身不含 attempt，新 attempt 用新 run 更省事）。
* job 列表走 `runs/<run_id>/attempts/<attempt>/jobs` 分页，只认 `GITHUB_RUN_ATTEMPT`
  这一轮，避免「只重跑失败 job」造成的混合状态；本地没给 attempt 时按 1 处理。
* 任何「查不到」「读不回」「字段对不上」都按拒绝处理（fail closed）：只有明确的 HTTP 404
  才当「不存在」，其余错误一律抛出，绝不静默降级。
* 所有 GitHub 调用都走注入的 `GhCli`（`gh` 子进程 + `GH_TOKEN`，参数数组、`shell=True`
  绝不出现，带 timeout，并钉住 github.com 防宿主 GH_HOST 重定向），API 入参一律用
  `--input -` 从 stdin 传入 JSON。

路径解析：版本文件与发布说明始终按**脚本所在的仓库根**解析（与 cwd 无关）；`--assets` /
`--manifest` 按当前工作目录解析，工作流都从仓库根调用。

信任边界：`draft` / `publish` job 是发布控制面，`contents: write` 本身不区分草稿与正式；
本脚本防的是流程错序、重跑与资产漂移，不声称对抗仓库管理员或 token 失窃。

测试见 `scripts/release/test_gate.py`（`FakeGh` 注入，不需要网络与 gh）。
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import stat
import subprocess
import sys
import tempfile
import xml.etree.ElementTree as ET
from dataclasses import dataclass
from pathlib import Path
from typing import Any, Callable, Iterable, Mapping, Sequence
from urllib.parse import quote

SCRIPT_PATH = Path(__file__).resolve()
REPO_ROOT = SCRIPT_PATH.parent.parent.parent

PROPS_RELATIVE_PATH = Path("dotnet") / "Directory.Build.props"
NOTES_DIRECTORY = Path("docs") / "releases"

# 发布 run 里必须全部成功的 job 名（可复用 workflow 的 job 在 run 里显示成 `<调用方 job> / <job>`）。
# 名称一旦和实际 workflow 有偏差，只需改这里。
EXPECTED_JOB_NAMES: tuple[str, ...] = (
    "ci / acceptance-runner",
    "ci / test (ubuntu-latest)",
    "ci / test (windows-latest)",
    "ci / winui-shell",
    "ci / linux-shell",
    "ci / release-gate-tests",
    "windows / build",
    "linux / build",
)

RELEASE_WORKFLOW_PATH = ".github/workflows/release.yml"
RELEASE_ENVIRONMENT = "release"
REQUIRED_REVIEWER_LOGIN = "gzy31007"

REHEARSAL_BRANCH_PREFIX = "release-gate-test/"
REHEARSAL_TAG_PREFIX = "gate-test-"

API_HOSTNAME = "github.com"
UPLOADS_HOSTNAME = "uploads.github.com"
DEFAULT_TIMEOUT_SECONDS = 120
RUN_ATTEMPT_ENV = "GITHUB_RUN_ATTEMPT"

FAULTS: tuple[str, ...] = ("none", "version-mismatch", "missing-asset", "platform-failure")

DEFAULT_RELEASE_NOTES = (
    "TJDesktopTimetable v{version}。\n"
    "下载与升级说明见提交在本 tag 上的 `docs/releases/v{version}.md`；"
    "本发布由发布门禁自动创建，资产摘要以 manifest 为准。"
)

WHITELIST_ENV_KEYS = ("PATH", "HOME", "LANG", "LC_ALL", "TMPDIR", "TERM", "SYSTEMROOT")

ASSET_SUFFIXES: tuple[str, ...] = (
    "setup.exe",
    "win-x64.zip",
    "linux-x64.tar.gz",
)

SHA_RE = re.compile(r"^[0-9a-f]{40}$")
RUN_ID_RE = re.compile(r"^[0-9]+$")
REPO_RE = re.compile(r"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$")
SEMVER_RE = re.compile(
    r"^(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)"
    r"(?:-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$"
)
HTTP_STATUS_RE = re.compile(r"HTTP\s+(\d{3})")
SHA256_HEX_RE = re.compile(r"^[0-9a-f]{64}$")

MANIFEST_KEYS = frozenset(
    {
        "repo",
        "release_id",
        "tag",
        "sha",
        "version",
        "run_id",
        "run_attempt",
        "rehearsal",
        "release",
        "expected",
    }
)
MANIFEST_RELEASE_KEYS = frozenset({"name", "body", "prerelease"})
MANIFEST_ASSET_KEYS = frozenset({"name", "size", "sha256", "asset_id"})


class GateError(Exception):
    """门禁拒绝执行。所有拒绝路径都只抛这一个异常，调用方据此 fail closed。"""


# --------------------------------------------------------------------------- 版本与参数


def validate_version(version: str) -> str:
    """校验版本号：支持 semver 预发布，但拒绝 build metadata（标签会不明确）。"""
    if not isinstance(version, str) or not version:
        raise GateError("Version 为空")
    if "+" in version:
        raise GateError(f"Version 不支持 build metadata（+...）：{version!r}")
    if not SEMVER_RE.match(version):
        raise GateError(f"Version 不是明确的 semver：{version!r}")
    return version


def read_version(repo_root: Path) -> str:
    """从 `dotnet/Directory.Build.props` 读唯一那个 `<Version>`。

    文件里出现 0 个或多个 `<Version>` 都拒绝：发版这一路不允许「哪个才是真的」的歧义。
    """
    props = Path(repo_root) / PROPS_RELATIVE_PATH
    if not props.is_file() or props.is_symlink():
        raise GateError(f"找不到版本文件：{PROPS_RELATIVE_PATH}")
    try:
        tree = ET.parse(props)
    except ET.ParseError as exc:
        raise GateError(f"{PROPS_RELATIVE_PATH} 不是合法 XML：{exc}") from exc
    found: list[str] = []
    for element in tree.getroot().iter():
        tag = element.tag.split("}")[-1] if isinstance(element.tag, str) else ""
        if tag == "Version":
            found.append((element.text or "").strip())
    if len(found) != 1:
        raise GateError(f"{PROPS_RELATIVE_PATH} 必须恰好有一个 Version，实际 {len(found)} 个")
    return validate_version(found[0])


def is_prerelease(version: str) -> bool:
    return "-" in validate_version(version)


def production_tag(version: str) -> str:
    return f"v{validate_version(version)}"


def rehearsal_tag(run_id: str, version: str) -> str:
    return f"{REHEARSAL_TAG_PREFIX}{validate_run_id(run_id)}-v{validate_version(version)}"


def expected_tag(version: str, run_id: str, rehearsal: bool) -> str:
    return rehearsal_tag(run_id, version) if rehearsal else production_tag(version)


def validate_tag(tag: str, *, version: str, run_id: str, rehearsal: bool) -> str:
    want = expected_tag(version, run_id, rehearsal)
    if tag != want:
        kind = "演练" if rehearsal else "生产"
        raise GateError(f"{kind} tag 必须是 {want}，实际 {tag!r}")
    return tag


def validate_sha(sha: str) -> str:
    if not isinstance(sha, str) or not SHA_RE.match(sha):
        raise GateError(f"--sha 必须是 40 位小写十六进制，实际 {sha!r}")
    return sha


def validate_run_id(run_id: str) -> str:
    if not isinstance(run_id, str) or not RUN_ID_RE.match(run_id):
        raise GateError(f"--run-id 必须是数字，实际 {run_id!r}")
    return run_id


def parse_run_attempt(value: str | int | None) -> int:
    """解析 `GITHUB_RUN_ATTEMPT`；没给（本地跑测试）按第 1 次处理。"""
    if value is None or value == "":
        return 1
    if isinstance(value, bool):
        raise GateError(f"{RUN_ATTEMPT_ENV} 不是合法 attempt：{value!r}")
    if isinstance(value, int):
        attempt = value
    elif isinstance(value, str) and RUN_ID_RE.match(value):
        attempt = int(value)
    else:
        raise GateError(f"{RUN_ATTEMPT_ENV} 必须是非负整数，实际 {value!r}")
    if attempt < 1:
        raise GateError(f"{RUN_ATTEMPT_ENV} 必须 >= 1，实际 {attempt}")
    return attempt


def parse_rehearsal(value: str) -> bool:
    if value == "true":
        return True
    if value == "false":
        return False
    raise GateError(f"--rehearsal 只接受 true|false，实际 {value!r}")


def expected_asset_names(version: str) -> tuple[str, ...]:
    version = validate_version(version)
    return tuple(f"TJDesktopTimetable-v{version}-{suffix}" for suffix in ASSET_SUFFIXES)


@dataclass(frozen=True)
class GateContext:
    """一次门禁调用的不可变上下文；构造即校验，下游可以信任字段。"""

    version: str
    tag: str
    sha: str
    run_id: str
    run_attempt: int = 1
    rehearsal: bool = False
    fault: str = "none"
    repo: str = ""

    def __post_init__(self) -> None:
        validate_version(self.version)
        validate_sha(self.sha)
        validate_run_id(self.run_id)
        if not isinstance(self.run_attempt, int) or isinstance(self.run_attempt, bool):
            raise GateError(f"run_attempt 必须是整数，实际 {self.run_attempt!r}")
        if self.run_attempt < 1:
            raise GateError(f"run_attempt 必须 >= 1，实际 {self.run_attempt}")
        if not isinstance(self.rehearsal, bool):
            raise GateError("rehearsal 必须是布尔值")
        if self.fault not in FAULTS:
            raise GateError(f"--fault 只接受 {'|'.join(FAULTS)}，实际 {self.fault!r}")
        if self.fault != "none" and not self.rehearsal:
            raise GateError("--fault 非 none 只允许在演练中使用")
        validate_tag(self.tag, version=self.version, run_id=self.run_id, rehearsal=self.rehearsal)
        if self.repo and not REPO_RE.match(self.repo):
            raise GateError(f"GITHUB_REPOSITORY 格式不合法：{self.repo!r}")

    @property
    def rehearsal_flag(self) -> str:
        return "true" if self.rehearsal else "false"


def build_context(
    *,
    version: str,
    tag: str,
    sha: str,
    run_id: str,
    rehearsal: bool,
    run_attempt: int = 1,
    fault: str = "none",
    repo: str = "",
) -> GateContext:
    return GateContext(
        version=version,
        tag=tag,
        sha=sha,
        run_id=run_id,
        run_attempt=run_attempt,
        rehearsal=rehearsal,
        fault=fault,
        repo=repo,
    )


def context_from_args(
    args: argparse.Namespace, *, repo_root: Path, env: Mapping[str, str]
) -> GateContext:
    version = read_version(repo_root)
    return build_context(
        version=version,
        tag=args.tag,
        sha=args.sha,
        run_id=args.run_id,
        run_attempt=parse_run_attempt(env.get(RUN_ATTEMPT_ENV)),
        rehearsal=parse_rehearsal(args.rehearsal),
        fault=getattr(args, "fault", "none"),
        repo=env.get("GITHUB_REPOSITORY", ""),
    )


def ensure_repo(ctx: GateContext) -> str:
    if not ctx.repo:
        raise GateError("缺少 GITHUB_REPOSITORY（owner/repo）")
    if not REPO_RE.match(ctx.repo):
        raise GateError(f"GITHUB_REPOSITORY 格式不合法：{ctx.repo!r}")
    return ctx.repo


# --------------------------------------------------------------------------- 本地 git 事实


def _run_git(repo_root: Path, args: Sequence[str]) -> subprocess.CompletedProcess[str]:
    return subprocess.run(
        ["git", "-C", str(repo_root), *args],
        capture_output=True,
        text=True,
        timeout=DEFAULT_TIMEOUT_SECONDS,
        check=False,
    )


def git_head(repo_root: Path) -> str:
    proc = _run_git(repo_root, ["rev-parse", "HEAD"])
    if proc.returncode != 0:
        raise GateError(f"git rev-parse HEAD 失败：{proc.stderr.strip()}")
    return proc.stdout.strip()


def git_tag_commit(repo_root: Path, tag: str) -> str | None:
    """本地 `refs/tags/<tag>^{}` 指向的 commit；没有这个 tag 返回 None。"""
    proc = _run_git(repo_root, ["rev-parse", "--verify", f"refs/tags/{tag}^{{}}"])
    if proc.returncode != 0:
        return None
    return proc.stdout.strip()


def validate_trigger(ctx: GateContext, env: Mapping[str, str]) -> None:
    """只接受两种触发：演练走 `release-gate-test/**` 分支，生产走 `refs/tags/<tag>`。"""
    ref = env.get("GITHUB_REF", "")
    if ctx.rehearsal:
        prefix = f"refs/heads/{REHEARSAL_BRANCH_PREFIX}"
        if not ref.startswith(prefix) or not ref[len(prefix):]:
            raise GateError(
                f"演练必须由 {REHEARSAL_BRANCH_PREFIX}** 分支触发，实际 GITHUB_REF={ref!r}"
            )
        return
    want = f"refs/tags/{ctx.tag}"
    if ref != want:
        raise GateError(f"生产必须由 tag 推送触发（GITHUB_REF 必须是 {want}），实际 {ref!r}")


def write_github_output(values: Mapping[str, str], env: Mapping[str, str]) -> Path | None:
    path = env.get("GITHUB_OUTPUT")
    if not path:
        return None
    text = "".join(f"{key}={value}\n" for key, value in values.items())
    with open(path, "a", encoding="utf-8") as handle:
        handle.write(text)
    return Path(path)


def prepare(ctx: GateContext, *, repo_root: Path, env: Mapping[str, str]) -> dict[str, str]:
    """prepare：纯本地校验，输出 version/tag/sha/rehearsal/fault。不调用任何 API。"""
    head = git_head(repo_root)
    if head != ctx.sha:
        raise GateError(f"本地 git HEAD={head} 与 --sha={ctx.sha} 不符")
    validate_trigger(ctx, env)
    if not ctx.rehearsal:
        tag_sha = git_tag_commit(repo_root, ctx.tag)
        if tag_sha is None:
            raise GateError(f"本地没有 tag {ctx.tag}")
        if tag_sha != ctx.sha:
            raise GateError(f"本地 tag {ctx.tag} 指向 {tag_sha}，不是 {ctx.sha}")
    values = {
        "version": ctx.version,
        "tag": ctx.tag,
        "sha": ctx.sha,
        "rehearsal": ctx.rehearsal_flag,
        "fault": ctx.fault,
    }
    write_github_output(values, env)
    return values


# --------------------------------------------------------------------------- gh 注入层


def _decode_stderr(stderr: Any) -> str:
    """子进程 stderr 可能是 bytes（下载路径没开 text=True）也可能是 str。"""
    if isinstance(stderr, bytes):
        return stderr.decode("utf-8", "replace").strip()
    return (stderr or "").strip()


def _http_status(text: str) -> int | None:
    match = HTTP_STATUS_RE.search(text or "")
    return int(match.group(1)) if match else None


class GhCli:
    """唯一的外部调用缝隙：把 `gh` 子进程封在一个类里，测试注入同名方法的 FakeGh。

    * API 入参一律 `--input -` 从 stdin 传 JSON，不用 `-f` 拼参数；
    * 命令以参数数组交给 subprocess，绝不 `shell=True`，并带 timeout；
    * 钉住 github.com：`gh api` 用 `--hostname github.com`，`gh release upload` 用
      `--repo github.com/<owner>/<repo>`，再从环境里补一个 `GH_HOST`，三重防宿主改道；
    * 只有明确的 HTTP 404 返回 None（表示「不存在」），其他失败一律抛 GateError。
    """

    def __init__(
        self,
        repo: str,
        token: str | None = None,
        *,
        binary: str = "gh",
        timeout: float = DEFAULT_TIMEOUT_SECONDS,
    ) -> None:
        self.repo = repo
        self._token = token
        self._binary = binary
        self.timeout = timeout

    def environment(self) -> dict[str, str]:
        env = {key: value for key, value in os.environ.items() if key in WHITELIST_ENV_KEYS}
        env.setdefault("PATH", "/usr/local/bin:/usr/bin:/bin")
        token = self._token
        if not token:
            raise GateError("缺少 GH_TOKEN/GITHUB_TOKEN，拒绝调用 GitHub API")
        env["GH_TOKEN"] = token
        env["GH_HOST"] = API_HOSTNAME
        env["GH_PROMPT_DISABLED"] = "1"
        env["GH_NO_UPDATE_NOTIFIER"] = "1"
        return env

    def _call(
        self,
        command: Sequence[str],
        *,
        stdin_text: str | None,
        what: str,
        env: Mapping[str, str] | None = None,
    ) -> subprocess.CompletedProcess[str]:
        try:
            return subprocess.run(
                command,
                input=stdin_text,
                capture_output=True,
                text=True,
                env=dict(env) if env is not None else self.environment(),
                timeout=self.timeout,
                check=False,
            )
        except subprocess.TimeoutExpired as exc:
            raise GateError(f"{what} 超时（{self.timeout}s）") from exc

    def api(self, method: str, path: str, payload: Mapping[str, Any] | None = None) -> Any:
        method = method.upper()
        command = [self._binary, "api", "--hostname", API_HOSTNAME, "-X", method, path]
        stdin_text: str | None = None
        if payload is not None:
            command += ["--input", "-"]
            stdin_text = json.dumps(payload, ensure_ascii=False)
        proc = self._call(command, stdin_text=stdin_text, what=f"gh api {method} {path}")
        if proc.returncode != 0:
            detail = f"{proc.stderr or ''}{proc.stdout or ''}".strip()
            if _http_status(detail) == 404:
                return None
            raise GateError(f"gh api {method} {path} 失败（exit {proc.returncode}）：{detail}")
        out = (proc.stdout or "").strip()
        if not out:
            return {}
        try:
            return json.loads(out)
        except json.JSONDecodeError as exc:
            raise GateError(f"gh api {method} {path} 返回非 JSON：{out[:200]!r}") from exc

    def download_to_file(self, path: str, dest: Path) -> None:
        """把二进制资产流式落到文件。

        `gh api` **没有** `--output`（本机 `gh api --help` 已确认），所以直接把子进程 stdout
        绑到文件句柄：不进内存、不走 `text=True`，非 UTF-8 字节原样落盘。
        """
        dest = Path(dest)
        command = [
            self._binary,
            "api",
            "--hostname",
            API_HOSTNAME,
            "-H",
            "Accept: application/octet-stream",
            path,
        ]
        try:
            with open(dest, "wb") as handle:
                proc = subprocess.run(
                    command,
                    stdout=handle,
                    stderr=subprocess.PIPE,
                    env=self.environment(),
                    timeout=self.timeout,
                    check=False,
                )
        except subprocess.TimeoutExpired as exc:
            dest.unlink(missing_ok=True)
            raise GateError(f"下载资产 {path} 超时（{self.timeout}s）") from exc
        except OSError as exc:
            dest.unlink(missing_ok=True)
            raise GateError(f"下载资产 {path} 无法写入 {dest}：{exc}") from exc
        if proc.returncode != 0:
            dest.unlink(missing_ok=True)
            detail = _decode_stderr(proc.stderr)
            raise GateError(f"下载资产 {path} 失败（exit {proc.returncode}）：{detail}")
        if not dest.is_file() or dest.stat().st_size <= 0:
            dest.unlink(missing_ok=True)
            raise GateError(f"下载资产 {path} 得到空文件，拒绝")

    def upload_asset(self, release_id: int, name: str, path: Path | str) -> Mapping[str, Any]:
        """按 release id 上传单个资产：绝对 uploads 主机 URL + `--input` 文件。

        为什么不用 `gh release upload <tag>`：它按 tag 反查 release，而 draft 在 REST 的
        tag 端点上不可见（实测 404），只有 GraphQL 才有兜底。这里直接用创建响应里的 id，
        与 tag 彻底解耦，并且绝对 URL 天然不会落到 api.github.com。

        认证注意：gh 按**请求 URL 的主机**解析 token（go-gh 的 tokenForHost：
        `github.com`/企业租户/localhost 才认 `GH_TOKEN`，其它主机只认
        `GH_ENTERPRISE_TOKEN`），`uploads.github.com` 不在前者里，所以这里把同一个 token
        镜像到 `GH_ENTERPRISE_TOKEN`/`GITHUB_ENTERPRISE_TOKEN`，token 只走环境、不进 argv。
        """
        if not isinstance(release_id, int) or release_id <= 0:
            raise GateError(f"上传资产需要合法的 release id，实际 {release_id!r}")
        if not isinstance(name, str) or not name:
            raise GateError("上传资产需要文件名")
        source = Path(path)
        size = source.stat().st_size
        endpoint = (
            f"https://{UPLOADS_HOSTNAME}/repos/{self.repo}/releases/{release_id}"
            f"/assets?name={quote(name, safe='')}"
        )
        command = [
            self._binary,
            "api",
            "--hostname",
            API_HOSTNAME,
            "-X",
            "POST",
            endpoint,
            "-H",
            "Content-Type: application/octet-stream",
            "-H",
            "Accept: application/vnd.github+json",
            "--input",
            str(source),
        ]
        env = self.environment()
        token = env["GH_TOKEN"]
        env.setdefault("GH_ENTERPRISE_TOKEN", token)
        env.setdefault("GITHUB_ENTERPRISE_TOKEN", token)
        proc = self._call(
            command, stdin_text=None, what=f"上传资产 {name}", env=env
        )
        if proc.returncode != 0:
            detail = f"{proc.stderr or ''}{proc.stdout or ''}".strip()
            raise GateError(f"上传资产 {name} 失败（exit {proc.returncode}）：{detail}")
        out = (proc.stdout or "").strip()
        try:
            body = json.loads(out)
        except json.JSONDecodeError as exc:
            raise GateError(f"上传资产 {name} 返回非 JSON：{out[:200]!r}") from exc
        if not isinstance(body, Mapping):
            raise GateError(f"上传资产 {name} 的响应不是对象")
        if not isinstance(body.get("id"), int):
            raise GateError(f"上传资产 {name} 的响应缺少 id")
        if body.get("name") != name:
            raise GateError(f"上传资产 {name} 的响应名字不符：{body.get('name')!r}")
        if body.get("size") != size:
            raise GateError(
                f"上传资产 {name} 的响应大小不符：{body.get('size')!r} != 本地 {size}"
            )
        state = body.get("state")
        if state is not None and state != "uploaded":
            raise GateError(f"上传资产 {name} 的状态不是 uploaded：{state!r}")
        return body


# --------------------------------------------------------------------------- 远端事实


def _repo_path(ctx: GateContext, suffix: str) -> str:
    return f"repos/{ensure_repo(ctx)}{suffix}"


def list_releases(gh: Any, ctx: GateContext) -> list[Mapping[str, Any]]:
    """分页列 Release；REST 的 tag 端点看不到 draft，只有清单能拿到。"""
    collected: list[Mapping[str, Any]] = []
    for page in range(1, 101):
        body = gh.api("GET", _repo_path(ctx, f"/releases?per_page=100&page={page}"))
        if body is None:
            # 404 只代表"这次查询失败"，不代表"没有 Release"：清单拿不全就不能判既存。
            raise GateError(f"Release 清单第 {page} 页返回 404，无法确认是否已存在同 tag Release")
        if not isinstance(body, list):
            raise GateError(f"Release 清单第 {page} 页不是数组")
        collected.extend(body)
        if len(body) < 100:
            break
    else:
        raise GateError("Release 清单分页超过 100 页，拒绝继续")
    for release in collected:
        if not isinstance(release, Mapping):
            raise GateError("Release 清单里有非对象条目")
    return collected


def find_release_by_tag(gh: Any, ctx: GateContext) -> Mapping[str, Any] | None:
    """按 tag 精确查 Release（含 draft）；同 tag 多份也拒绝，绝不静默挑一个。"""
    matches = [r for r in list_releases(gh, ctx) if r.get("tag_name") == ctx.tag]
    if not matches:
        return None
    if len(matches) > 1:
        raise GateError(f"同 tag 存在多个 Release（{len(matches)} 个），拒绝覆写：{ctx.tag}")
    return matches[0]


def list_run_jobs(gh: Any, ctx: GateContext) -> list[Mapping[str, Any]]:
    """按 attempt 取 job，分页；只认 `GITHUB_RUN_ATTEMPT` 这一轮。"""
    jobs: list[Mapping[str, Any]] = []
    base = f"/actions/runs/{ctx.run_id}/attempts/{ctx.run_attempt}/jobs?per_page=100&page="
    for page in range(1, 21):
        body = gh.api("GET", _repo_path(ctx, f"{base}{page}"))
        if not isinstance(body, Mapping):
            raise GateError(f"run {ctx.run_id} 第 {ctx.run_attempt} 次 attempt 的 jobs 响应不是对象")
        chunk = body.get("jobs") or []
        if not isinstance(chunk, list):
            raise GateError(f"run {ctx.run_id} jobs 字段不是数组")
        jobs.extend(chunk)
        total = body.get("total_count")
        if len(chunk) < 100:
            break
        if isinstance(total, int) and len(jobs) >= total:
            break
    else:
        raise GateError(f"run {ctx.run_id} jobs 分页过多，拒绝继续")
    return jobs


def verify_jobs(jobs: Iterable[Mapping[str, Any]], attempt: int) -> dict[str, Mapping[str, Any]]:
    """只取指定 attempt 的 job，要求期望列表里每个名字恰好成功一次。"""
    latest: dict[str, list[Mapping[str, Any]]] = {}
    for job in jobs:
        if job.get("run_attempt") != attempt:
            continue
        name = job.get("name")
        if not isinstance(name, str):
            raise GateError("job 缺少 name 字段")
        latest.setdefault(name, []).append(job)
    problems: list[str] = []
    verified: dict[str, Mapping[str, Any]] = {}
    for name in EXPECTED_JOB_NAMES:
        entries = latest.get(name, [])
        if not entries:
            problems.append(f"缺少 job：{name}")
            continue
        if len(entries) > 1:
            problems.append(f"job 重名：{name} × {len(entries)}")
            continue
        conclusion = entries[0].get("conclusion")
        if conclusion != "success":
            problems.append(f"job 未成功：{name}（conclusion={conclusion!r}）")
            continue
        verified[name] = entries[0]
    if problems:
        raise GateError("；".join(problems))
    return verified


def _is_release_workflow_path(path: str) -> bool:
    """只认这个 workflow 本身，或可复用调用写进 run 的 `<path>@<ref>`；`release.yml.bak` 之类一律拒绝。"""
    return path == RELEASE_WORKFLOW_PATH or path.startswith(RELEASE_WORKFLOW_PATH + "@")


def verify_run(gh: Any, ctx: GateContext) -> Mapping[str, Any]:
    run = gh.api("GET", _repo_path(ctx, f"/actions/runs/{ctx.run_id}"))
    if run is None:
        raise GateError(f"找不到 run {ctx.run_id}")
    if not isinstance(run, Mapping):
        raise GateError(f"run {ctx.run_id} 响应不是对象")
    head_sha = run.get("head_sha")
    if head_sha != ctx.sha:
        raise GateError(f"run head_sha={head_sha!r} 与 --sha={ctx.sha} 不符")
    if run.get("event") != "push":
        raise GateError(f"run 触发事件必须是 push，实际 {run.get('event')!r}")
    path = run.get("path")
    if not isinstance(path, str) or not _is_release_workflow_path(path):
        raise GateError(f"run 必须来自 {RELEASE_WORKFLOW_PATH}，实际 {path!r}")
    run_attempt = run.get("run_attempt")
    if not isinstance(run_attempt, int) or run_attempt < 1:
        raise GateError(f"run 缺少合法的 run_attempt：{run_attempt!r}")
    if run_attempt != ctx.run_attempt:
        raise GateError(
            f"run 已是第 {run_attempt} 次 attempt，与本次 {RUN_ATTEMPT_ENV}={ctx.run_attempt} 不符"
            "（疑似只重跑了部分 job，拒绝）"
        )
    verify_jobs(list_run_jobs(gh, ctx), ctx.run_attempt)
    return run


def verify_environment(gh: Any, ctx: GateContext) -> Mapping[str, Any]:
    """release 环境的审批人必须**恰好**是指定维护者，否则 fail closed。

    原生环境里任何被列出的 User/Team 都能审批，所以「包含 gzy31007」远远不够：这里要求
    恰好一条 `required_reviewers` 规则、规则里恰好一个结构合法的 `type="User"` 审核器、
    且 `reviewer.login` 就是指定维护者。多一个 User、加一个 Team、未知 type、缺 type、
    重复规则、空规则，全部拒绝，不做任何静默忽略。
    """
    environment = gh.api("GET", _repo_path(ctx, f"/environments/{RELEASE_ENVIRONMENT}"))
    if environment is None:
        raise GateError(f"环境 {RELEASE_ENVIRONMENT} 不存在（审核器缺失，拒绝发布）")
    if not isinstance(environment, Mapping):
        raise GateError(f"环境 {RELEASE_ENVIRONMENT} 响应不是对象")
    rules = environment.get("protection_rules") or []
    if not isinstance(rules, list):
        raise GateError(f"环境 {RELEASE_ENVIRONMENT} 的 protection_rules 不是数组")
    required: list[Mapping[str, Any]] = []
    for rule in rules:
        if not isinstance(rule, Mapping):
            raise GateError(f"环境 {RELEASE_ENVIRONMENT} 的 protection_rules 里有非对象条目")
        if rule.get("type") == "required_reviewers":
            required.append(rule)
    if len(required) != 1:
        raise GateError(
            f"环境 {RELEASE_ENVIRONMENT} 必须恰好有一条 required_reviewers 规则，"
            f"实际 {len(required)} 条（拒绝发布）"
        )
    reviewers = required[0].get("reviewers")
    if not isinstance(reviewers, list) or len(reviewers) != 1:
        raise GateError(
            f"环境 {RELEASE_ENVIRONMENT} 的 required_reviewers 必须恰好列一个审核器"
            f"（实际 {len(reviewers) if isinstance(reviewers, list) else reviewers!r}，拒绝发布）"
        )
    entry = reviewers[0]
    if not isinstance(entry, Mapping):
        raise GateError(f"环境 {RELEASE_ENVIRONMENT} 的审核器条目不是对象")
    kind = entry.get("type")
    if kind != "User":
        raise GateError(
            f"环境 {RELEASE_ENVIRONMENT} 的审核器必须是 User，实际 type={kind!r}（拒绝发布）"
        )
    reviewer = entry.get("reviewer")
    if not isinstance(reviewer, Mapping):
        raise GateError(f"环境 {RELEASE_ENVIRONMENT} 的 User 审核器缺少 reviewer 对象")
    login = reviewer.get("login")
    if login != REQUIRED_REVIEWER_LOGIN:
        raise GateError(
            f"环境 {RELEASE_ENVIRONMENT} 的唯一审核器必须是 {REQUIRED_REVIEWER_LOGIN}，"
            f"实际 {login!r}（拒绝发布）"
        )
    return environment


def resolve_remote_tag(gh: Any, ctx: GateContext, tag: str) -> str | None:
    """远端 tag 解引用到 commit sha；tag 不存在返回 None。"""
    ref = gh.api("GET", _repo_path(ctx, f"/git/ref/tags/{quote(tag, safe='')}"))
    if ref is None:
        return None
    if not isinstance(ref, Mapping):
        raise GateError(f"git ref {tag} 响应不是对象")
    target = ref.get("object")
    if not isinstance(target, Mapping):
        raise GateError(f"git ref {tag} 缺少 object")
    kind = target.get("type")
    sha = target.get("sha")
    depth = 0
    while kind == "tag":
        depth += 1
        if depth > 5:
            raise GateError(f"tag {tag} 解引用层数过多")
        if not isinstance(sha, str):
            raise GateError(f"tag {tag} 的对象 sha 不合法")
        tag_object = gh.api("GET", _repo_path(ctx, f"/git/tags/{sha}"))
        if tag_object is None:
            raise GateError(f"tag 对象 {sha} 不存在")
        if not isinstance(tag_object, Mapping):
            raise GateError(f"tag 对象 {sha} 响应不是对象")
        target = tag_object.get("object") or {}
        if not isinstance(target, Mapping):
            raise GateError(f"tag 对象 {sha} 缺少 object")
        kind = target.get("type")
        sha = target.get("sha")
    if kind != "commit" or not isinstance(sha, str):
        raise GateError(f"远端 tag {tag} 未指向 commit（type={kind!r}）")
    return sha


def ensure_tag_ref(gh: Any, ctx: GateContext) -> str:
    """保证远端 `refs/tags/<tag>` 指向这个 sha。

    生产：tag 必须已存在且指向 sha（漂移即拒绝）。
    演练：专属 tag 不存在才用 POST git/refs 创建；已存在必须同 sha。
    """
    remote = resolve_remote_tag(gh, ctx, ctx.tag)
    if ctx.rehearsal:
        if remote is None:
            created = gh.api(
                "POST",
                _repo_path(ctx, "/git/refs"),
                {"ref": f"refs/tags/{ctx.tag}", "sha": ctx.sha},
            )
            if not isinstance(created, Mapping):
                raise GateError(f"创建演练 tag {ctx.tag} 的响应无效")
            return ctx.sha
        if remote != ctx.sha:
            raise GateError(f"演练 tag {ctx.tag} 已存在但指向 {remote}，不是 {ctx.sha}")
        return remote
    if remote is None:
        raise GateError(f"生产 tag {ctx.tag} 在远端不存在")
    if remote != ctx.sha:
        raise GateError(f"生产 tag {ctx.tag} 漂移：远端 {remote} != 本次 {ctx.sha}")
    return remote


@dataclass(frozen=True)
class ExpectedAsset:
    name: str
    size: int
    sha256: str
    asset_id: int | None = None


@dataclass(frozen=True)
class LocalAsset:
    name: str
    size: int
    sha256: str
    path: Path

    def as_expected(self) -> ExpectedAsset:
        return ExpectedAsset(name=self.name, size=self.size, sha256=self.sha256)


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with open(path, "rb") as handle:
        for chunk in iter(lambda: handle.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def collect_assets(assets_dir: Path, version: str) -> list[LocalAsset]:
    """本地资产恰好三件：非空普通文件，拒绝 symlink，也拒绝任何意外文件。"""
    assets_dir = Path(assets_dir)
    if assets_dir.is_symlink():
        raise GateError(f"资产目录是符号链接，拒绝：{assets_dir}")
    if not assets_dir.is_dir():
        raise GateError(f"资产目录不存在：{assets_dir}")
    expected = expected_asset_names(version)
    entries = {entry.name: entry for entry in assets_dir.iterdir()}
    missing = [name for name in expected if name not in entries]
    if missing:
        raise GateError(f"缺少资产：{', '.join(missing)}")
    extra = sorted(set(entries) - set(expected))
    if extra:
        raise GateError(f"资产目录里有意外文件：{', '.join(extra)}")
    collected: list[LocalAsset] = []
    for name in expected:
        path = entries[name]
        if path.is_symlink():
            raise GateError(f"资产是符号链接，拒绝：{name}")
        info = os.lstat(path)
        if not stat.S_ISREG(info.st_mode):
            raise GateError(f"资产不是普通文件，拒绝：{name}")
        if info.st_size <= 0:
            raise GateError(f"资产为空文件，拒绝：{name}")
        collected.append(
            LocalAsset(name=name, path=path, size=info.st_size, sha256=sha256_file(path))
        )
    return collected


def list_release_assets(gh: Any, ctx: GateContext, release_id: int) -> list[Mapping[str, Any]]:
    assets: list[Mapping[str, Any]] = []
    for page in range(1, 21):
        body = gh.api(
            "GET",
            _repo_path(ctx, f"/releases/{release_id}/assets?per_page=100&page={page}"),
        )
        if not isinstance(body, list):
            raise GateError(f"Release {release_id} 资产列表响应不是数组")
        if not body:
            break
        assets.extend(body)
        if len(body) < 100:
            break
    else:
        raise GateError(f"Release {release_id} 资产分页过多，拒绝继续")
    return assets


def asset_sha256(gh: Any, ctx: GateContext, asset: Mapping[str, Any]) -> str:
    """优先用服务端 digest；缺失时把二进制资产下载到隔离临时文件再算 sha256。"""
    digest = asset.get("digest")
    if isinstance(digest, str) and digest.startswith("sha256:"):
        value = digest[len("sha256:"):].strip().lower()
        if SHA256_HEX_RE.match(value):
            return value
    asset_id = asset.get("id")
    if not isinstance(asset_id, int):
        raise GateError(f"资产 {asset.get('name')!r} 缺少 id，无法核验摘要")
    with tempfile.TemporaryDirectory(prefix="release-gate-asset-") as tmp:
        dest = Path(tmp) / f"asset-{asset_id}.bin"
        gh.download_to_file(_repo_path(ctx, f"/releases/assets/{asset_id}"), dest)
        if not dest.is_file() or dest.stat().st_size <= 0:
            raise GateError(f"下载资产 {asset.get('name')!r} 失败")
        return sha256_file(dest)


def verify_remote_assets(
    gh: Any, ctx: GateContext, release_id: int, expected: Sequence[ExpectedAsset]
) -> list[Mapping[str, Any]]:
    """核验服务端三件资产：名称、asset id（有绑定时）、状态、大小、摘要，且没有多余资产。"""
    remote = list_release_assets(gh, ctx, release_id)
    by_name: dict[str, list[Mapping[str, Any]]] = {}
    for asset in remote:
        name = asset.get("name")
        if not isinstance(name, str):
            raise GateError("远端资产缺少 name 字段")
        by_name.setdefault(name, []).append(asset)
    wanted = {asset.name: asset for asset in expected}
    problems: list[str] = []
    for name in sorted(wanted):
        entries = by_name.get(name, [])
        if not entries:
            problems.append(f"远端缺少资产：{name}")
            continue
        if len(entries) > 1:
            problems.append(f"远端资产重名：{name} × {len(entries)}")
            continue
        asset = entries[0]
        if wanted[name].asset_id is not None and asset.get("id") != wanted[name].asset_id:
            problems.append(
                f"资产 id 变了：{name}（服务端 {asset.get('id')!r}，manifest {wanted[name].asset_id}）"
            )
        if asset.get("state") != "uploaded":
            problems.append(f"资产状态不是 uploaded：{name}（{asset.get('state')!r}）")
        if asset.get("size") != wanted[name].size:
            problems.append(
                f"资产大小不符：{name}（远端 {asset.get('size')!r}，本地 {wanted[name].size}）"
            )
        try:
            actual = asset_sha256(gh, ctx, asset)
        except GateError as exc:
            problems.append(f"资产摘要无法核验：{name}（{exc}）")
        else:
            if actual != wanted[name].sha256:
                problems.append(f"资产摘要不符：{name}（远端 {actual}，本地 {wanted[name].sha256}）")
    extra = sorted(set(by_name) - set(wanted))
    if extra:
        problems.append(f"远端有额外资产：{', '.join(extra)}")
    if problems:
        raise GateError("；".join(problems))
    return remote


# --------------------------------------------------------------------------- Release 内容


def release_notes(repo_root: Path, version: str) -> str:
    notes_path = Path(repo_root) / NOTES_DIRECTORY / f"v{version}.md"
    if notes_path.is_file() and not notes_path.is_symlink():
        text = notes_path.read_text(encoding="utf-8").strip()
        if text:
            return text
    return DEFAULT_RELEASE_NOTES.format(version=validate_version(version))


def release_body(ctx: GateContext, notes: str) -> str:
    marker = (
        f"<!-- release-gate sha={ctx.sha} run-id={ctx.run_id} "
        f"attempt={ctx.run_attempt} rehearsal={ctx.rehearsal_flag} -->\n"
        f"- 提交：`{ctx.sha}`\n"
        f"- run：[{ctx.run_id}](../actions/runs/{ctx.run_id})\n"
        f"- 演练：{ctx.rehearsal_flag}"
    )
    return f"{notes.strip()}\n\n{marker}\n"


def release_title(ctx: GateContext) -> str:
    title = f"TJDesktopTimetable v{ctx.version}"
    if ctx.rehearsal:
        title += f"（演练 {ctx.tag}）"
    return title


def verify_release_metadata(release: Any, ctx: GateContext, *, require_draft: bool) -> None:
    if not isinstance(release, Mapping):
        raise GateError("Release 响应不是对象")
    problems: list[str] = []
    if release.get("tag_name") != ctx.tag:
        problems.append(f"Release tag 不符：{release.get('tag_name')!r} != {ctx.tag!r}")
    if release.get("draft") is not require_draft:
        problems.append(f"Release draft 状态不符：{release.get('draft')!r}")
    target = release.get("target_commitish")
    if target != ctx.sha:
        problems.append(f"Release target_commitish 不符：{target!r} != {ctx.sha!r}")
    want_prerelease = is_prerelease(ctx.version)
    if bool(release.get("prerelease")) is not want_prerelease:
        problems.append(f"Release prerelease 不符：{release.get('prerelease')!r}")
    if problems:
        raise GateError("；".join(problems))


def release_snapshot(release: Mapping[str, Any]) -> dict[str, Any]:
    """把服务端 release 的 name/body/prerelease 冻结成 manifest 里可比对的一份快照。"""
    name = release.get("name")
    body = release.get("body")
    prerelease = release.get("prerelease")
    if not isinstance(name, str) or not isinstance(body, str):
        raise GateError("Release 缺少 name/body，无法写入 manifest")
    if not isinstance(prerelease, bool):
        raise GateError("Release 缺少合法的 prerelease，无法写入 manifest")
    return {"name": name, "body": body, "prerelease": prerelease}


def verify_release_snapshot(release: Mapping[str, Any], snapshot: Mapping[str, Any]) -> None:
    problems: list[str] = []
    if release.get("name") != snapshot["name"]:
        problems.append("Release title 在 draft 之后变了")
    if release.get("body") != snapshot["body"]:
        problems.append("Release body 在 draft 之后变了")
    if release.get("prerelease") is not snapshot["prerelease"]:
        problems.append("Release prerelease 在 draft 之后变了")
    if problems:
        raise GateError("；".join(problems))


def create_draft_release(gh: Any, ctx: GateContext, body: str) -> Mapping[str, Any]:
    """只用 draft=true 的 API 建 Release；绝不使用会自动公开的 gh release create。"""
    payload = {
        "tag_name": ctx.tag,
        "target_commitish": ctx.sha,
        "name": release_title(ctx),
        "body": body,
        "draft": True,
        "prerelease": is_prerelease(ctx.version),
    }
    release = gh.api("POST", _repo_path(ctx, "/releases"), payload)
    if not isinstance(release, Mapping):
        raise GateError("创建 draft Release 的响应无效")
    release_id = release.get("id")
    if not isinstance(release_id, int):
        raise GateError("创建 draft Release 的响应缺少 id")
    if release.get("draft") is not True:
        raise GateError("创建后的 Release 不是 draft，立即中止")
    return release


def upload_assets(
    gh: Any, ctx: GateContext, release_id: int, assets: Sequence[LocalAsset]
) -> list[Mapping[str, Any]]:
    """逐个按 release id 上传；回包逐个核对名字与大小，任何偏差立即中止。"""
    uploaded: list[Mapping[str, Any]] = []
    for asset in assets:
        uploaded.append(gh.upload_asset(release_id, asset.name, asset.path))
    return uploaded


def fetch_release(gh: Any, ctx: GateContext, release_id: int) -> Mapping[str, Any]:
    release = gh.api("GET", _repo_path(ctx, f"/releases/{release_id}"))
    if release is None:
        raise GateError(f"找不到 Release {release_id}")
    if not isinstance(release, Mapping):
        raise GateError(f"Release {release_id} 响应不是对象")
    return release


# --------------------------------------------------------------------------- manifest


def manifest_payload(
    ctx: GateContext,
    *,
    release: Mapping[str, Any],
    expected: Sequence[ExpectedAsset],
) -> dict[str, Any]:
    """manifest = 这次 draft 的完整快照；publish 只能照着它核对，不能自行放宽。"""
    if not isinstance(release.get("id"), int):
        raise GateError("Release 缺少 id，无法写入 manifest")
    entries: list[dict[str, Any]] = []
    for asset in expected:
        if not isinstance(asset.asset_id, int):
            raise GateError(f"资产 {asset.name} 缺少服务端 id，无法写入 manifest")
        entries.append(
            {
                "name": asset.name,
                "size": asset.size,
                "sha256": asset.sha256,
                "asset_id": asset.asset_id,
            }
        )
    return {
        "repo": ensure_repo(ctx),
        "release_id": release["id"],
        "tag": ctx.tag,
        "sha": ctx.sha,
        "version": ctx.version,
        "run_id": ctx.run_id,
        "run_attempt": ctx.run_attempt,
        "rehearsal": ctx.rehearsal,
        "release": release_snapshot(release),
        "expected": entries,
    }


def write_manifest(path: Path, payload: Mapping[str, Any]) -> None:
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    temp = path.with_name(path.name + ".tmp")
    temp.write_text(
        json.dumps(payload, ensure_ascii=False, indent=2, sort_keys=True) + "\n",
        encoding="utf-8",
    )
    os.replace(temp, path)


def read_manifest(path: Path) -> dict[str, Any]:
    path = Path(path)
    try:
        raw = path.read_text(encoding="utf-8")
    except OSError as exc:
        raise GateError(f"读不到 manifest：{path}（{exc}）") from exc
    try:
        data = json.loads(raw)
    except json.JSONDecodeError as exc:
        raise GateError(f"manifest 不是合法 JSON：{path}（{exc}）") from exc
    if not isinstance(data, dict):
        raise GateError("manifest 顶层不是对象")
    missing = sorted(MANIFEST_KEYS - set(data))
    if missing:
        raise GateError(f"manifest 缺少字段：{', '.join(missing)}")
    if not isinstance(data["repo"], str) or not REPO_RE.match(data["repo"]):
        raise GateError("manifest repo 不合法")
    if not isinstance(data["release_id"], int) or data["release_id"] <= 0:
        raise GateError("manifest release_id 不是正整数")
    for key in ("tag", "sha", "version", "run_id"):
        if not isinstance(data[key], str) or not data[key]:
            raise GateError(f"manifest {key} 不是非空字符串")
    if not isinstance(data["run_attempt"], int) or isinstance(data["run_attempt"], bool):
        raise GateError("manifest run_attempt 不是整数")
    if data["run_attempt"] < 1:
        raise GateError("manifest run_attempt 必须 >= 1")
    if not isinstance(data["rehearsal"], bool):
        raise GateError("manifest rehearsal 不是布尔值")
    validate_sha(data["sha"])
    validate_run_id(data["run_id"])
    validate_version(data["version"])
    release = data["release"]
    if not isinstance(release, dict) or set(release) != MANIFEST_RELEASE_KEYS:
        raise GateError("manifest release 必须是 name/body/prerelease 三键对象")
    if not isinstance(release["name"], str) or not isinstance(release["body"], str):
        raise GateError("manifest release name/body 必须是字符串")
    if not isinstance(release["prerelease"], bool):
        raise GateError("manifest release prerelease 必须是布尔值")
    expected = data["expected"]
    if not isinstance(expected, list) or not expected:
        raise GateError("manifest expected 不是非空数组")
    names: list[str] = []
    for entry in expected:
        if not isinstance(entry, dict):
            raise GateError("manifest expected 条目不是对象")
        if set(entry) != MANIFEST_ASSET_KEYS:
            raise GateError("manifest expected 条目字段必须恰好是 name/size/sha256/asset_id")
        name, size, digest, asset_id = (
            entry["name"],
            entry["size"],
            entry["sha256"],
            entry["asset_id"],
        )
        if not isinstance(name, str) or not name:
            raise GateError("manifest expected 条目 name 不合法")
        if not isinstance(size, int) or isinstance(size, bool) or size <= 0:
            raise GateError(f"manifest 资产 {name} 的 size 不合法")
        if not isinstance(digest, str) or not SHA256_HEX_RE.match(digest):
            raise GateError(f"manifest 资产 {name} 的 sha256 不合法")
        if not isinstance(asset_id, int) or isinstance(asset_id, bool) or asset_id <= 0:
            raise GateError(f"manifest 资产 {name} 的 asset_id 不合法")
        names.append(name)
    if sorted(names) != sorted(expected_asset_names(data["version"])):
        raise GateError(f"manifest 资产清单与 v{data['version']} 的三件资产不符")
    return data


def verify_manifest(manifest: Mapping[str, Any], ctx: GateContext, *, version: str) -> None:
    problems: list[str] = []
    if manifest.get("repo") != ctx.repo:
        problems.append(f"manifest repo={manifest.get('repo')!r} 与 GITHUB_REPOSITORY={ctx.repo!r} 不符")
    if manifest.get("tag") != ctx.tag:
        problems.append(f"manifest tag={manifest.get('tag')!r} 与 --tag={ctx.tag!r} 不符")
    if manifest.get("sha") != ctx.sha:
        problems.append(f"manifest sha={manifest.get('sha')!r} 与 --sha={ctx.sha!r} 不符")
    if manifest.get("version") != version:
        problems.append(f"manifest version={manifest.get('version')!r} 与当前版本 {version!r} 不符")
    if manifest.get("run_id") != ctx.run_id:
        problems.append(
            f"manifest run_id={manifest.get('run_id')!r} 与 --run-id={ctx.run_id!r} 不符"
        )
    if manifest.get("run_attempt") != ctx.run_attempt:
        problems.append(
            f"manifest run_attempt={manifest.get('run_attempt')!r}"
            f" 与 {RUN_ATTEMPT_ENV}={ctx.run_attempt} 不符"
        )
    if manifest.get("rehearsal") is not ctx.rehearsal:
        problems.append("manifest rehearsal 与本次调用不符")
    if problems:
        raise GateError("；".join(problems))


def manifest_expected(manifest: Mapping[str, Any]) -> list[ExpectedAsset]:
    return [
        ExpectedAsset(
            name=entry["name"],
            size=entry["size"],
            sha256=entry["sha256"],
            asset_id=entry["asset_id"],
        )
        for entry in manifest["expected"]
    ]


def bind_remote_assets(
    expected: Sequence[ExpectedAsset], remote: Sequence[Mapping[str, Any]]
) -> list[ExpectedAsset]:
    """把服务端 asset id 绑进 expected 条目（draft 写 manifest 用）。"""
    by_name = {asset["name"]: asset for asset in remote if isinstance(asset.get("name"), str)}
    bound: list[ExpectedAsset] = []
    for asset in expected:
        entry = by_name.get(asset.name)
        if entry is None:
            raise GateError(f"远端缺少资产 {asset.name}，无法写入 manifest")
        asset_id = entry.get("id")
        if not isinstance(asset_id, int):
            raise GateError(f"远端资产 {asset.name} 缺少 id，无法写入 manifest")
        bound.append(
            ExpectedAsset(
                name=asset.name, size=asset.size, sha256=asset.sha256, asset_id=asset_id
            )
        )
    return bound


# --------------------------------------------------------------------------- 三个子命令


def draft(
    ctx: GateContext,
    gh: Any,
    *,
    assets_dir: Path,
    manifest_path: Path,
    repo_root: Path,
) -> dict[str, Any]:
    ensure_repo(ctx)
    assets = collect_assets(Path(assets_dir), ctx.version)
    verify_run(gh, ctx)
    verify_environment(gh, ctx)
    ensure_tag_ref(gh, ctx)
    existing = find_release_by_tag(gh, ctx)
    if existing is not None:
        if existing.get("draft") is True:
            state = "draft"
        elif existing.get("draft") is False:
            state = "已公开"
        else:
            state = "状态不明的"
        raise GateError(f"同 tag 已存在 Release（{state}），拒绝覆写：{ctx.tag}")
    notes = release_notes(repo_root, ctx.version)
    release = create_draft_release(gh, ctx, release_body(ctx, notes))
    release_id = int(release["id"])
    upload_assets(gh, ctx, release_id, assets)
    snapshot_release = fetch_release(gh, ctx, release_id)
    verify_release_metadata(snapshot_release, ctx, require_draft=True)
    remote = verify_remote_assets(
        gh, ctx, release_id, [asset.as_expected() for asset in assets]
    )
    payload = manifest_payload(
        ctx,
        release=snapshot_release,
        expected=bind_remote_assets([asset.as_expected() for asset in assets], remote),
    )
    write_manifest(Path(manifest_path), payload)
    return payload


def publish(
    ctx: GateContext,
    gh: Any,
    *,
    manifest_path: Path,
    repo_root: Path,
    env: Mapping[str, str],
) -> dict[str, Any]:
    ensure_repo(ctx)
    if ctx.rehearsal:
        raise GateError("演练 Release 永远不得公开（即使被误批准）")
    validate_trigger(ctx, env)
    manifest = read_manifest(Path(manifest_path))
    verify_manifest(manifest, ctx, version=read_version(repo_root))
    verify_run(gh, ctx)
    verify_environment(gh, ctx)
    remote = resolve_remote_tag(gh, ctx, ctx.tag)
    if remote is None:
        raise GateError(f"生产 tag {ctx.tag} 在远端不存在")
    if remote != ctx.sha:
        raise GateError(f"生产 tag {ctx.tag} 漂移：远端 {remote} != 本次 {ctx.sha}")
    release_id = int(manifest["release_id"])
    release = fetch_release(gh, ctx, release_id)
    if release.get("draft") is not True:
        raise GateError(f"Release {release_id} 不是 draft（可能已公开），拒绝重复发布")
    verify_release_metadata(release, ctx, require_draft=True)
    verify_release_snapshot(release, manifest["release"])
    expected = manifest_expected(manifest)
    verify_remote_assets(gh, ctx, release_id, expected)
    gh.api("PATCH", _repo_path(ctx, f"/releases/{release_id}"), {"draft": False})
    published = fetch_release(gh, ctx, release_id)
    if published.get("draft") is not False:
        raise GateError(f"PATCH 后 Release {release_id} 仍不是公开状态")
    verify_release_metadata(published, ctx, require_draft=False)
    verify_release_snapshot(published, manifest["release"])
    verify_remote_assets(gh, ctx, release_id, expected)
    return {
        "release_id": release_id,
        "tag": ctx.tag,
        "sha": ctx.sha,
        "version": ctx.version,
        "run_id": ctx.run_id,
        "run_attempt": ctx.run_attempt,
        "rehearsal": ctx.rehearsal,
        "url": published.get("html_url") or "",
    }


# --------------------------------------------------------------------------- 入口


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        prog="gate.py",
        description="TJDesktopTimetable 发布硬门禁（prepare / draft / publish）",
    )
    subparsers = parser.add_subparsers(dest="command", required=True)

    def add_common(sub: argparse.ArgumentParser) -> None:
        sub.add_argument("--sha", required=True, help="本次发布的 commit sha（40 位十六进制）")
        sub.add_argument("--tag", required=True, help="发布标签")
        sub.add_argument("--run-id", required=True, dest="run_id", help="GitHub Actions run id")
        sub.add_argument("--rehearsal", required=True, choices=("true", "false"))

    prepare_parser = subparsers.add_parser("prepare", help="本地预检并输出上下文")
    add_common(prepare_parser)
    prepare_parser.add_argument(
        "--fault", default="none", choices=FAULTS, help="演练注入的故障标签"
    )

    draft_parser = subparsers.add_parser("draft", help="创建 draft Release 并上传资产")
    add_common(draft_parser)
    draft_parser.add_argument("--assets", required=True, help="三件资产所在目录")
    draft_parser.add_argument("--manifest", required=True, help="输出的 manifest 路径")

    publish_parser = subparsers.add_parser("publish", help="把 draft Release 公开")
    add_common(publish_parser)
    publish_parser.add_argument("--manifest", required=True, help="draft 阶段写出的 manifest 路径")
    return parser


def _default_gh_factory(ctx: GateContext, env: Mapping[str, str]) -> GhCli:
    token = env.get("GH_TOKEN") or env.get("GITHUB_TOKEN")
    return GhCli(repo=ensure_repo(ctx), token=token)


def main(
    argv: Sequence[str] | None = None,
    *,
    env: Mapping[str, str] | None = None,
    repo_root: Path | None = None,
    gh_factory: Callable[[GateContext, Mapping[str, str]], Any] | None = None,
) -> int:
    environment = dict(os.environ) if env is None else dict(env)
    root = Path(repo_root) if repo_root is not None else REPO_ROOT
    args = build_parser().parse_args(argv)
    try:
        ctx = context_from_args(args, repo_root=root, env=environment)
        factory = gh_factory or _default_gh_factory
        if args.command == "prepare":
            payload: dict[str, Any] = prepare(ctx, repo_root=root, env=environment)
        elif args.command == "draft":
            payload = draft(
                ctx,
                factory(ctx, environment),
                assets_dir=Path(args.assets),
                manifest_path=Path(args.manifest),
                repo_root=root,
            )
        elif args.command == "publish":
            payload = publish(
                ctx,
                factory(ctx, environment),
                manifest_path=Path(args.manifest),
                repo_root=root,
                env=environment,
            )
        else:  # pragma: no cover - argparse 已经限制了取值
            raise GateError(f"未知子命令：{args.command}")
    except GateError as exc:
        print(f"gate: 拒绝：{exc}", file=sys.stderr)
        return 1
    print(json.dumps(payload, ensure_ascii=False, sort_keys=True))
    return 0


if __name__ == "__main__":  # pragma: no cover
    raise SystemExit(main())
