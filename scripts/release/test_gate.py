#!/usr/bin/env python3
"""`scripts/release/gate.py` 的单测。

全部在本地跑：GitHub 一侧用 `FakeGh` 注入（同名方法：api / upload_asset / download_to_file），
`gh` 子进程用 `unittest.mock` 打桩。命令：

    python3 scripts/release/test_gate.py
    python3 -m unittest discover -s scripts/release -t scripts/release -p 'test_gate.py'
"""

from __future__ import annotations

import copy
import hashlib
import json
import os
import re
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path
from typing import Any, Callable, Mapping
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parent))

import gate  # noqa: E402  （测试缝隙：先加脚本目录到 sys.path）

REPO = "gzy31007/TJDesktopTimetable"
SHA = "0123456789abcdef0123456789abcdef01234567"
OTHER_SHA = "89abcdef0123456789abcdef0123456789abcdef"
RUN_ID = "9001"
VERSION = "1.6.2"
PRE_VERSION = "1.7.0-rc.1"
TAG = f"v{VERSION}"
REHEARSAL_TAG = f"gate-test-{RUN_ID}-v{VERSION}"
_TAG_BY_VERSION = {VERSION: TAG, PRE_VERSION: f"v{PRE_VERSION}"}
_UNSET = object()


# --------------------------------------------------------------------------- 夹具


def fake_run(
    *,
    run_id: str = RUN_ID,
    sha: str = SHA,
    attempt: int = 1,
    event: str = "push",
    path: str = ".github/workflows/release.yml",
) -> dict[str, Any]:
    return {
        "id": int(run_id),
        "head_sha": sha,
        "event": event,
        "path": path,
        "run_attempt": attempt,
        "status": "in_progress",
        "conclusion": None,
    }


def fake_job(name: str, *, attempt: int = 1, conclusion: Any = "success") -> dict[str, Any]:
    return {
        "id": abs(hash((name, attempt))) % 10_000_000,
        "name": name,
        "run_attempt": attempt,
        "status": "completed",
        "conclusion": conclusion,
    }


def fake_jobs(
    *,
    attempt: int = 1,
    overrides: Mapping[str, Any] | None = None,
    extra: int = 0,
) -> list[dict[str, Any]]:
    overrides = overrides or {}
    jobs = [
        fake_job(name, attempt=attempt, conclusion=overrides.get(name, "success"))
        for name in gate.EXPECTED_JOB_NAMES
    ]
    for index in range(extra):
        jobs.append(fake_job(f"other / filler-{index}", attempt=attempt))
    return jobs


def fake_environment(logins: tuple[str, ...] | None = ("gzy31007",)) -> dict[str, Any]:
    if logins is None:
        return {"name": gate.RELEASE_ENVIRONMENT, "protection_rules": []}
    return {
        "name": gate.RELEASE_ENVIRONMENT,
        "protection_rules": [
            {
                "id": 1,
                "type": "required_reviewers",
                "reviewers": [
                    {"type": "User", "reviewer": {"type": "User", "login": login, "id": 1}}
                    for login in logins
                ],
            }
        ],
    }


def fake_environment_with_rules(*rules: Any) -> dict[str, Any]:
    return {"name": gate.RELEASE_ENVIRONMENT, "protection_rules": list(rules)}


def reviewer_rule(*reviewers: Any) -> dict[str, Any]:
    return {"id": 1, "type": "required_reviewers", "reviewers": list(reviewers)}


def user_reviewer(login: Any = "gzy31007", *, type_: Any = "User") -> dict[str, Any]:
    return {"type": type_, "reviewer": {"type": "User", "login": login, "id": 42}}


def team_reviewer(login: str = "gzy31007") -> dict[str, Any]:
    return {"type": "Team", "reviewer": {"type": "Team", "login": login, "id": 43}}


def seeded_release(
    tag: str,
    *,
    draft: bool = True,
    sha: str = SHA,
    prerelease: bool = False,
    release_id: int = 7000,
    target: str | None = None,
) -> dict[str, Any]:
    return {
        "id": release_id,
        "tag_name": tag,
        "target_commitish": sha if target is None else target,
        "name": f"TJDesktopTimetable {tag}",
        "body": "release body",
        "draft": draft,
        "prerelease": prerelease,
        "published_at": None if draft else "2026-10-20T00:00:00Z",
        "html_url": f"https://example.invalid/releases/{release_id}",
    }


class FakeGh:
    """最小 GitHub API 假实现：只覆盖 gate.py 用到的那些路径。"""

    def __init__(
        self,
        *,
        run: Mapping[str, Any] | None = None,
        jobs: list[Mapping[str, Any]] | None = None,
        environment: Any = _UNSET,
        tags: Mapping[str, Any] | None = None,
        releases_by_tag: Mapping[str, Any] | None = None,
        patch_effect: Callable[[dict[str, Any], Mapping[str, Any]], None] | None = None,
        on_upload: Callable[[dict[str, Any]], None] | None = None,
    ) -> None:
        self.repo = REPO
        self.run = copy.deepcopy(dict(run)) if run is not None else fake_run()
        attempt = self.run.get("run_attempt", 1)
        self.jobs = copy.deepcopy(list(jobs)) if jobs is not None else fake_jobs(attempt=attempt)
        self.environment = fake_environment() if environment is _UNSET else copy.deepcopy(environment)
        self.tags: dict[str, Any] = copy.deepcopy(dict(tags or {}))
        self.tag_objects: dict[str, Any] = {}
        self.releases_by_tag = copy.deepcopy(dict(releases_by_tag or {}))
        self.releases: dict[int, dict[str, Any]] = {}
        self.assets: dict[int, list[dict[str, Any]]] = {}
        for release in self.releases_by_tag.values():
            if isinstance(release, Mapping) and isinstance(release.get("id"), int):
                self.releases[release["id"]] = copy.deepcopy(release)
                self.assets.setdefault(release["id"], [])
        self.downloads: dict[int, bytes] = {}
        self.patch_effect = patch_effect
        self.on_upload = on_upload
        self.requests: list[tuple[str, str, Any]] = []
        self.uploads: list[tuple[int, list[str]]] = []
        self.download_calls: list[str] = []
        self._next_id = 9000

    # -- 断言辅助 ---------------------------------------------------------
    def publish_patches(self) -> list[tuple[str, str, Any]]:
        return [
            request
            for request in self.requests
            if request[0] == "PATCH"
            and isinstance(request[2], Mapping)
            and request[2].get("draft") is False
        ]

    def release_count(self) -> int:
        return len(self.releases)

    def release_posts(self) -> list[tuple[str, str, Any]]:
        return [item for item in self.requests if item[0] == "POST" and item[1].endswith("/releases")]

    # -- GhCli 同名方法 ---------------------------------------------------
    def api(self, method: str, path: str, payload: Mapping[str, Any] | None = None) -> Any:
        self.requests.append((method, path, copy.deepcopy(payload)))
        base = f"repos/{self.repo}"
        if not path.startswith(base):
            raise AssertionError(f"意外的 API 路径：{path}")
        rest = path[len(base):]
        if method == "GET":
            match = re.fullmatch(r"/actions/runs/(\d+)", rest)
            if match:
                return copy.deepcopy(self.run) if int(match.group(1)) == self.run["id"] else None
            match = re.fullmatch(
                r"/actions/runs/(\d+)/attempts/(\d+)/jobs\?per_page=(\d+)&page=(\d+)", rest
            )
            if match:
                attempt, per, page = int(match.group(2)), int(match.group(3)), int(match.group(4))
                selected = [job for job in self.jobs if job.get("run_attempt") == attempt]
                return {
                    "total_count": len(selected),
                    "jobs": copy.deepcopy(selected[(page - 1) * per: page * per]),
                }
            match = re.fullmatch(rf"/environments/{re.escape(gate.RELEASE_ENVIRONMENT)}", rest)
            if match:
                return copy.deepcopy(self.environment)
            match = re.fullmatch(r"/git/ref/tags/(.+)", rest)
            if match:
                tag = match.group(1)
                entry = self.tags.get(tag)
                if entry is None:
                    return None
                if isinstance(entry, Mapping):
                    return {"ref": f"refs/tags/{tag}", "object": copy.deepcopy(dict(entry))}
                return {"ref": f"refs/tags/{tag}", "object": {"type": "commit", "sha": entry}}
            match = re.fullmatch(r"/git/tags/([0-9a-f]{40})", rest)
            if match:
                obj = self.tag_objects.get(match.group(1))
                return copy.deepcopy(obj) if obj is not None else None
            match = re.fullmatch(r"/releases\?per_page=(\d+)&page=(\d+)", rest)
            if match:
                per, page = int(match.group(1)), int(match.group(2))
                ordered = [self.releases[key] for key in sorted(self.releases)]
                return copy.deepcopy(ordered[(page - 1) * per: page * per])
            match = re.fullmatch(r"/releases/tags/(.+)", rest)
            if match:
                # 真实 GitHub：draft Release 在 REST 的 tag 端点上看不到，只能靠清单/id。
                release = self.releases_by_tag.get(match.group(1))
                if release is None or release.get("draft") is True:
                    return None
                return copy.deepcopy(release)
            match = re.fullmatch(r"/releases/(\d+)", rest)
            if match:
                release = self.releases.get(int(match.group(1)))
                return copy.deepcopy(release) if release is not None else None
            match = re.fullmatch(r"/releases/(\d+)/assets\?per_page=(\d+)&page=(\d+)", rest)
            if match:
                per, page = int(match.group(2)), int(match.group(3))
                chunk = self.assets.get(int(match.group(1)), [])[(page - 1) * per: page * per]
                return copy.deepcopy(chunk)
        if method == "POST" and rest == "/git/refs":
            ref = str(payload["ref"])
            tag = ref[len("refs/tags/"):]
            if tag in self.tags:
                raise gate.GateError("创建 ref 失败（HTTP 422）")
            self.tags[tag] = {"type": "commit", "sha": payload["sha"]}
            return {"ref": ref, "object": {"type": "commit", "sha": payload["sha"]}}
        if method == "POST" and rest == "/releases":
            release_id = self._next_id
            self._next_id += 1
            release = {
                "id": release_id,
                "tag_name": payload["tag_name"],
                "target_commitish": payload["target_commitish"],
                "name": payload["name"],
                "body": payload["body"],
                "draft": bool(payload["draft"]),
                "prerelease": bool(payload["prerelease"]),
                "published_at": None,
                "html_url": f"https://example.invalid/releases/{release_id}",
            }
            self.releases[release_id] = release
            self.assets[release_id] = []
            self.releases_by_tag[release["tag_name"]] = copy.deepcopy(release)
            return copy.deepcopy(release)
        if method == "PATCH":
            match = re.fullmatch(r"/releases/(\d+)", rest)
            if match:
                release = self.releases[int(match.group(1))]
                if self.patch_effect is not None:
                    self.patch_effect(release, payload)
                elif payload.get("draft") is False:
                    release["draft"] = False
                    release["published_at"] = "2026-10-20T00:00:00Z"
                if release["tag_name"] in self.releases_by_tag:
                    self.releases_by_tag[release["tag_name"]] = copy.deepcopy(release)
                return copy.deepcopy(release)
        raise AssertionError(f"未处理的 API 调用：{method} {rest}")

    def upload_asset(self, release_id: int, name: str, path: Any) -> dict[str, Any]:
        if release_id not in self.releases:
            raise gate.GateError(f"upload 时 Release 不存在：{release_id}")
        self.uploads.append((release_id, [str(path)]))
        data = Path(path).read_bytes()
        asset_id = self._next_id
        self._next_id += 1
        asset = {
            "id": asset_id,
            "name": name,
            "size": len(data),
            "state": "uploaded",
            "digest": f"sha256:{hashlib.sha256(data).hexdigest()}",
        }
        self.assets[release_id].append(asset)
        self.downloads[asset_id] = data
        if callable(self.on_upload):
            self.on_upload(self.releases[release_id])
        return copy.deepcopy(asset)

    def download_to_file(self, path: str, dest: Path) -> None:
        self.download_calls.append(path)
        match = re.fullmatch(rf"repos/{re.escape(self.repo)}/releases/assets/(\d+)", path)
        if not match:
            raise AssertionError(f"意外的下载路径：{path}")
        Path(dest).write_bytes(self.downloads[int(match.group(1))])


def make_repo_root(
    tmp: Path,
    *,
    version: str = VERSION,
    props_body: str | None = None,
    notes: bool = True,
) -> Path:
    root = Path(tmp) / "repo"
    props = root / "dotnet" / "Directory.Build.props"
    props.parent.mkdir(parents=True, exist_ok=True)
    body = props_body
    if body is None:
        body = (
            "<Project>\n  <PropertyGroup>\n"
            f"    <Version>{version}</Version>\n"
            "  </PropertyGroup>\n</Project>\n"
        )
    props.write_text(body, encoding="utf-8")
    if notes:
        notes_dir = root / "docs" / "releases"
        notes_dir.mkdir(parents=True, exist_ok=True)
        (notes_dir / f"v{version}.md").write_text(f"# v{version} 发布说明\n", encoding="utf-8")
    return root


def make_assets(directory: Path, names: list[str] | tuple[str, ...], *, size: int = 32) -> Path:
    directory.mkdir(parents=True, exist_ok=True)
    for index, name in enumerate(names):
        (directory / name).write_bytes(bytes([65 + index % 20]) * (size + index))
    return directory


def standard_assets(version: str = VERSION) -> list[str]:
    return list(gate.expected_asset_names(version))


def make_ctx(
    *,
    rehearsal: bool = False,
    version: str = VERSION,
    run_id: str = RUN_ID,
    run_attempt: int = 1,
    sha: str = SHA,
    tag: str | None = None,
    fault: str = "none",
    repo: str = REPO,
) -> gate.GateContext:
    return gate.GateContext(
        version=version,
        tag=tag if tag is not None else gate.expected_tag(version, run_id, rehearsal),
        sha=sha,
        run_id=run_id,
        run_attempt=run_attempt,
        rehearsal=rehearsal,
        fault=fault,
        repo=repo,
    )


def trigger_env(tag: str = TAG) -> dict[str, str]:
    return {"GITHUB_REF": f"refs/tags/{tag}", "GITHUB_REPOSITORY": REPO}


class GateTestCase(unittest.TestCase):
    """统一提供临时目录与临时 repo 根（含 Directory.Build.props 和发布说明）。"""

    def setUp(self) -> None:
        self._tmpdir = tempfile.TemporaryDirectory(prefix="gate-test-")
        self.addCleanup(self._tmpdir.cleanup)
        self.tmp = Path(self._tmpdir.name)
        self.repo_root = make_repo_root(self.tmp)

    def manifest_path(self) -> Path:
        return self.tmp / "manifest.json"

    def assets_dir(self, version: str = VERSION) -> Path:
        return make_assets(self.tmp / "assets", standard_assets(version))

    def draft(self, ctx: gate.GateContext, gh: FakeGh, *, assets_dir: Path | None = None) -> dict[str, Any]:
        return gate.draft(
            ctx,
            gh,
            assets_dir=assets_dir if assets_dir is not None else self.assets_dir(),
            manifest_path=self.manifest_path(),
            repo_root=self.repo_root,
        )

    def publish(self, ctx: gate.GateContext, gh: FakeGh, env: Mapping[str, str] | None = None) -> dict[str, Any]:
        return gate.publish(
            ctx,
            gh,
            manifest_path=self.manifest_path(),
            repo_root=self.repo_root,
            env=dict(env if env is not None else trigger_env(ctx.tag)),
        )


# --------------------------------------------------------------------------- 版本 / 标签 / 参数


class VersionAndTagTests(GateTestCase):
    def test_read_version_returns_single_declared_version(self) -> None:
        self.assertEqual(gate.read_version(self.repo_root), VERSION)

    def test_read_version_accepts_semver_prerelease(self) -> None:
        root = make_repo_root(self.tmp / "pre", version=PRE_VERSION)
        self.assertEqual(gate.read_version(root), PRE_VERSION)

    def test_read_version_rejects_build_metadata(self) -> None:
        root = make_repo_root(
            self.tmp / "meta",
            props_body=(
                "<Project><PropertyGroup><Version>1.6.2+build.7</Version></PropertyGroup></Project>"
            ),
        )
        with self.assertRaises(gate.GateError) as caught:
            gate.read_version(root)
        self.assertIn("build metadata", str(caught.exception))

    def test_read_version_rejects_duplicate_version_elements(self) -> None:
        root = make_repo_root(
            self.tmp / "dup",
            props_body=(
                "<Project><PropertyGroup><Version>1.6.2</Version></PropertyGroup>"
                "<PropertyGroup><Version>1.6.3</Version></PropertyGroup></Project>"
            ),
        )
        with self.assertRaises(gate.GateError) as caught:
            gate.read_version(root)
        self.assertIn("恰好有一个 Version", str(caught.exception))

    def test_read_version_rejects_missing_props(self) -> None:
        empty = self.tmp / "empty"
        empty.mkdir()
        with self.assertRaises(gate.GateError):
            gate.read_version(empty)

    def test_read_version_rejects_non_semver(self) -> None:
        root = make_repo_root(
            self.tmp / "bad",
            props_body="<Project><PropertyGroup><Version>1.6</Version></PropertyGroup></Project>",
        )
        with self.assertRaises(gate.GateError):
            gate.read_version(root)

    def test_tags_follow_version(self) -> None:
        self.assertEqual(gate.production_tag(VERSION), TAG)
        self.assertEqual(gate.rehearsal_tag(RUN_ID, VERSION), REHEARSAL_TAG)
        self.assertEqual(gate.expected_tag(VERSION, RUN_ID, False), TAG)
        self.assertEqual(gate.expected_tag(VERSION, RUN_ID, True), REHEARSAL_TAG)

    def test_validate_tag_rejects_wrong_version_tag(self) -> None:
        with self.assertRaises(gate.GateError) as caught:
            gate.validate_tag(
                f"gate-test-{RUN_ID}-v0.0.0", version=VERSION, run_id=RUN_ID, rehearsal=True
            )
        self.assertIn(f"gate-test-{RUN_ID}-v{VERSION}", str(caught.exception))
        with self.assertRaises(gate.GateError):
            gate.validate_tag("v0.0.0", version=VERSION, run_id=RUN_ID, rehearsal=False)

    def test_is_prerelease(self) -> None:
        self.assertFalse(gate.is_prerelease(VERSION))
        self.assertTrue(gate.is_prerelease(PRE_VERSION))

    def test_validate_sha_and_run_id(self) -> None:
        self.assertEqual(gate.validate_sha(SHA), SHA)
        self.assertEqual(gate.validate_run_id("123"), "123")
        for bad in (SHA.upper(), SHA[:39], SHA + "0", "z" * 40):
            with self.assertRaises(gate.GateError):
                gate.validate_sha(bad)
        for bad in ("12a", "-1", "", None):
            with self.assertRaises(gate.GateError):
                gate.validate_run_id(bad)  # type: ignore[arg-type]

    def test_parse_run_attempt(self) -> None:
        self.assertEqual(gate.parse_run_attempt(None), 1)
        self.assertEqual(gate.parse_run_attempt(""), 1)
        self.assertEqual(gate.parse_run_attempt("2"), 2)
        self.assertEqual(gate.parse_run_attempt(3), 3)
        for bad in ("0", "-1", "x", True):
            with self.assertRaises(gate.GateError):
                gate.parse_run_attempt(bad)  # type: ignore[arg-type]

    def test_parse_rehearsal(self) -> None:
        self.assertTrue(gate.parse_rehearsal("true"))
        self.assertFalse(gate.parse_rehearsal("false"))
        for bad in ("True", "1", ""):
            with self.assertRaises(gate.GateError):
                gate.parse_rehearsal(bad)

    def test_expected_asset_names(self) -> None:
        self.assertEqual(
            gate.expected_asset_names(VERSION),
            (
                f"TJDesktopTimetable-v{VERSION}-setup.exe",
                f"TJDesktopTimetable-v{VERSION}-win-x64.zip",
                f"TJDesktopTimetable-v{VERSION}-linux-x64.tar.gz",
            ),
        )

    def test_context_rejects_fault_outside_rehearsal(self) -> None:
        with self.assertRaises(gate.GateError) as caught:
            make_ctx(rehearsal=False, fault="platform-failure")
        self.assertIn("演练", str(caught.exception))

    def test_context_rejects_unknown_fault(self) -> None:
        with self.assertRaises(gate.GateError):
            make_ctx(rehearsal=True, fault="boom")

    def test_context_accepts_fault_in_rehearsal(self) -> None:
        ctx = make_ctx(rehearsal=True, fault="version-mismatch")
        self.assertEqual(ctx.fault, "version-mismatch")
        self.assertEqual(ctx.rehearsal_flag, "true")

    def test_context_rejects_bad_repo(self) -> None:
        with self.assertRaises(gate.GateError):
            make_ctx(repo="not-a-repo")
        with self.assertRaises(gate.GateError):
            make_ctx(repo="owner/repo/extra")

    def test_context_rejects_bad_attempt(self) -> None:
        with self.assertRaises(gate.GateError):
            make_ctx(run_attempt=0)
        with self.assertRaises(gate.GateError):
            make_ctx(run_attempt="2")  # type: ignore[arg-type]


# --------------------------------------------------------------------------- prepare


class PrepareTests(GateTestCase):
    def _env(self, ref: str = f"refs/tags/{TAG}", output: Path | None = None) -> dict[str, str]:
        env = {"GITHUB_REF": ref, "GITHUB_REPOSITORY": REPO}
        if output is not None:
            env["GITHUB_OUTPUT"] = str(output)
        return env

    def test_prepare_writes_outputs_and_json_values(self) -> None:
        output = self.tmp / "github-output.txt"
        ctx = make_ctx()
        with mock.patch.object(gate, "git_head", return_value=SHA), mock.patch.object(
            gate, "git_tag_commit", return_value=SHA
        ):
            values = gate.prepare(ctx, repo_root=self.repo_root, env=self._env(output=output))
        self.assertEqual(
            values,
            {"version": VERSION, "tag": TAG, "sha": SHA, "rehearsal": "false", "fault": "none"},
        )
        text = output.read_text(encoding="utf-8")
        self.assertIn(f"version={VERSION}", text)
        self.assertIn(f"tag={TAG}", text)
        self.assertIn(f"sha={SHA}", text)
        self.assertIn("rehearsal=false", text)
        self.assertIn("fault=none", text)

    def test_prepare_rehearsal_requires_release_gate_branch(self) -> None:
        ctx = make_ctx(rehearsal=True)
        with mock.patch.object(gate, "git_head", return_value=SHA):
            values = gate.prepare(
                ctx,
                repo_root=self.repo_root,
                env=self._env(ref="refs/heads/release-gate-test/run-9001"),
            )
        self.assertEqual(values["tag"], REHEARSAL_TAG)
        self.assertEqual(values["rehearsal"], "true")
        with mock.patch.object(gate, "git_head", return_value=SHA):
            with self.assertRaises(gate.GateError) as caught:
                gate.prepare(ctx, repo_root=self.repo_root, env=self._env(ref="refs/heads/main"))
        self.assertIn("release-gate-test", str(caught.exception))

    def test_prepare_rejects_head_mismatch(self) -> None:
        ctx = make_ctx()
        with mock.patch.object(gate, "git_head", return_value=OTHER_SHA):
            with self.assertRaises(gate.GateError) as caught:
                gate.prepare(ctx, repo_root=self.repo_root, env=self._env())
        self.assertIn("HEAD", str(caught.exception))

    def test_prepare_production_requires_tag_ref_trigger(self) -> None:
        ctx = make_ctx()
        with mock.patch.object(gate, "git_head", return_value=SHA), mock.patch.object(
            gate, "git_tag_commit", return_value=SHA
        ):
            with self.assertRaises(gate.GateError) as caught:
                gate.prepare(
                    ctx,
                    repo_root=self.repo_root,
                    env=self._env(ref="refs/heads/release-gate-test/x"),
                )
        self.assertIn("refs/tags", str(caught.exception))

    def test_prepare_production_requires_local_tag(self) -> None:
        ctx = make_ctx()
        with mock.patch.object(gate, "git_head", return_value=SHA), mock.patch.object(
            gate, "git_tag_commit", return_value=None
        ):
            with self.assertRaises(gate.GateError) as caught:
                gate.prepare(ctx, repo_root=self.repo_root, env=self._env())
        self.assertIn("没有 tag", str(caught.exception))

    def test_prepare_production_rejects_tag_drift(self) -> None:
        ctx = make_ctx()
        with mock.patch.object(gate, "git_head", return_value=SHA), mock.patch.object(
            gate, "git_tag_commit", return_value=OTHER_SHA
        ):
            with self.assertRaises(gate.GateError) as caught:
                gate.prepare(ctx, repo_root=self.repo_root, env=self._env())
        self.assertIn("指向", str(caught.exception))

    def test_prepare_rehearsal_tag_mismatch_fails_without_extra_injection(self) -> None:
        # 演练注入了错误的 0.0.0 标签，门禁自己就该拒绝，不需要脚本另外造错。
        with self.assertRaises(gate.GateError):
            make_ctx(rehearsal=True, tag=f"gate-test-{RUN_ID}-v0.0.0")

    def test_prepare_rehearsal_does_not_need_local_tag(self) -> None:
        ctx = make_ctx(rehearsal=True)
        with mock.patch.object(gate, "git_head", return_value=SHA), mock.patch.object(
            gate, "git_tag_commit", side_effect=AssertionError("演练不应读本地 tag")
        ):
            gate.prepare(
                ctx,
                repo_root=self.repo_root,
                env=self._env(ref="refs/heads/release-gate-test/run-9001"),
            )

    def test_git_head_uses_real_git_arguments(self) -> None:
        with mock.patch.object(gate.subprocess, "run") as run:
            run.return_value = subprocess.CompletedProcess(["git"], 0, SHA + "\n", "")
            self.assertEqual(gate.git_head(self.repo_root), SHA)
            command = run.call_args.args[0]
            self.assertEqual(command[:3], ["git", "-C", str(self.repo_root)])
            self.assertIn("timeout", run.call_args.kwargs)
            run.return_value = subprocess.CompletedProcess(["git"], 1, "", "fatal")
            self.assertIsNone(gate.git_tag_commit(self.repo_root, TAG))

    def test_validate_trigger_rejects_empty_ref(self) -> None:
        with self.assertRaises(gate.GateError):
            gate.validate_trigger(make_ctx(rehearsal=True), {"GITHUB_REF": "refs/heads/release-gate-test/"})

    def test_context_from_args_reads_run_attempt_from_env(self) -> None:
        args = gate.build_parser().parse_args(
            ["prepare", "--sha", SHA, "--tag", TAG, "--run-id", RUN_ID, "--rehearsal", "false"]
        )
        ctx = gate.context_from_args(
            args, repo_root=self.repo_root, env={"GITHUB_REPOSITORY": REPO, "GITHUB_RUN_ATTEMPT": "2"}
        )
        self.assertEqual(ctx.run_attempt, 2)
        ctx = gate.context_from_args(args, repo_root=self.repo_root, env={"GITHUB_REPOSITORY": REPO})
        self.assertEqual(ctx.run_attempt, 1)


# --------------------------------------------------------------------------- gh 子进程封装


class GhCliTests(unittest.TestCase):
    def setUp(self) -> None:
        self._tmpdir = tempfile.TemporaryDirectory(prefix="gate-ghcli-")
        self.addCleanup(self._tmpdir.cleanup)
        self.tmp = Path(self._tmpdir.name)

    def _proc(self, code: int = 0, stdout: str = "", stderr: str = "") -> subprocess.CompletedProcess[str]:
        return subprocess.CompletedProcess(["gh"], code, stdout, stderr)

    def test_api_get_builds_arg_array_and_uses_gh_token(self) -> None:
        with mock.patch.dict(os.environ, {"GH_TOKEN": "leak", "GITHUB_SHA": "shady"}, clear=True):
            with mock.patch("subprocess.run", return_value=self._proc(0, '{"ok": 1}')) as run:
                self.assertEqual(gate.GhCli(REPO, token="tok").api("GET", "repos/o/r/x"), {"ok": 1})
        command = run.call_args.args[0]
        self.assertEqual(
            command, ["gh", "api", "--hostname", "github.com", "-X", "GET", "repos/o/r/x"]
        )
        kwargs = run.call_args.kwargs
        self.assertIsNone(kwargs["input"])
        self.assertNotIn("shell", kwargs)
        self.assertEqual(kwargs["timeout"], gate.DEFAULT_TIMEOUT_SECONDS)
        self.assertEqual(kwargs["env"]["GH_TOKEN"], "tok")
        self.assertEqual(kwargs["env"]["GH_HOST"], "github.com")
        self.assertNotIn("GITHUB_SHA", kwargs["env"])

    def test_api_sends_payload_on_stdin(self) -> None:
        payload = {"tag_name": TAG, "draft": True}
        with mock.patch("subprocess.run", return_value=self._proc(0, "{}")) as run:
            gate.GhCli(REPO, token="tok").api("POST", "repos/o/r/releases", payload)
        command = run.call_args.args[0]
        self.assertEqual(command[-2:], ["--input", "-"])
        self.assertEqual(json.loads(run.call_args.kwargs["input"]), payload)

    def test_api_404_is_absence_but_other_errors_raise(self) -> None:
        with mock.patch(
            "subprocess.run", return_value=self._proc(1, "", "gh: Not Found (HTTP 404)\n")
        ):
            self.assertIsNone(gate.GhCli(REPO, token="tok").api("GET", "repos/o/r/releases/tags/v1"))
        with mock.patch(
            "subprocess.run", return_value=self._proc(1, "", "gh: Server Error (HTTP 500)\n")
        ):
            with self.assertRaises(gate.GateError) as caught:
                gate.GhCli(REPO, token="tok").api("GET", "repos/o/r/releases/tags/v1")
        self.assertIn("HTTP 500", str(caught.exception))
        with mock.patch("subprocess.run", return_value=self._proc(1, "", "network down")):
            with self.assertRaises(gate.GateError):
                gate.GhCli(REPO, token="tok").api("GET", "repos/o/r/x")

    def test_api_rejects_non_json_success_body(self) -> None:
        with mock.patch("subprocess.run", return_value=self._proc(0, "<html>")):
            with self.assertRaises(gate.GateError):
                gate.GhCli(REPO, token="tok").api("GET", "repos/o/r/x")

    def test_api_timeout_raises_gate_error(self) -> None:
        with mock.patch("subprocess.run", side_effect=subprocess.TimeoutExpired(["gh"], 120)):
            with self.assertRaises(gate.GateError) as caught:
                gate.GhCli(REPO, token="tok", timeout=5).api("GET", "repos/o/r/x")
        self.assertIn("超时", str(caught.exception))

    def test_api_requires_token(self) -> None:
        with mock.patch.dict(os.environ, {}, clear=True):
            with mock.patch("subprocess.run") as run:
                with self.assertRaises(gate.GateError) as caught:
                    gate.GhCli(REPO, token=None).api("GET", "repos/o/r/x")
        self.assertIn("GH_TOKEN", str(caught.exception))
        self.assertFalse(run.called)

    def test_upload_asset_contract_absolute_uploads_url_and_input_file(self) -> None:
        body = {"id": 77, "name": "a.bin", "size": 5, "state": "uploaded"}
        source = self.tmp / "a.bin"
        source.write_bytes(b"hello")
        with mock.patch("subprocess.run", return_value=self._proc(0, json.dumps(body))) as run:
            result = gate.GhCli(REPO, token="tok-secret").upload_asset(7000, "a.bin", source)
        self.assertEqual(result["id"], 77)
        command = run.call_args.args[0]
        self.assertEqual(
            command[:7],
            ["gh", "api", "--hostname", "github.com", "-X", "POST",
             f"https://uploads.github.com/repos/{REPO}/releases/7000/assets?name=a.bin"],
        )
        self.assertIn("Content-Type: application/octet-stream", command)
        self.assertIn("Accept: application/vnd.github+json", command)
        self.assertEqual(command[command.index("--input") + 1], str(source))
        self.assertNotIn("--clobber", command)
        self.assertNotIn("gh", command[1:2] + [])  # 不是 release 子命令
        self.assertNotIn("release", command)
        kwargs = run.call_args.kwargs
        self.assertNotIn("shell", kwargs)
        self.assertEqual(kwargs["timeout"], gate.DEFAULT_TIMEOUT_SECONDS)
        env = kwargs["env"]
        self.assertEqual(env["GH_HOST"], "github.com")
        self.assertEqual(env["GH_ENTERPRISE_TOKEN"], "tok-secret")
        self.assertEqual(env["GITHUB_ENTERPRISE_TOKEN"], "tok-secret")
        self.assertNotIn("tok-secret", " ".join(command))

    def test_upload_asset_quotes_name_in_query(self) -> None:
        body = {"id": 1, "name": "a b.exe", "size": 5, "state": "uploaded"}
        source = self.tmp / "a b.exe"
        source.write_bytes(b"hello")
        with mock.patch("subprocess.run", return_value=self._proc(0, json.dumps(body))) as run:
            gate.GhCli(REPO, token="tok").upload_asset(7000, "a b.exe", source)
        endpoint = run.call_args.args[0][6]
        self.assertIn("name=a%20b.exe", endpoint)

    def test_upload_asset_rejects_mismatched_response(self) -> None:
        source = self.tmp / "a.bin"
        source.write_bytes(b"hello")
        for payload in (
            {"id": 1, "name": "b.bin", "size": 5, "state": "uploaded"},
            {"id": 1, "name": "a.bin", "size": 999, "state": "uploaded"},
            {"name": "a.bin", "size": 5, "state": "uploaded"},
            {"id": 1, "name": "a.bin", "size": 5, "state": "starter"},
        ):
            with self.subTest(payload=payload):
                with mock.patch("subprocess.run", return_value=self._proc(0, json.dumps(payload))):
                    with self.assertRaises(gate.GateError):
                        gate.GhCli(REPO, token="tok").upload_asset(7000, "a.bin", source)

    def test_upload_asset_accepts_valid_response(self) -> None:
        source = self.tmp / "a.bin"
        source.write_bytes(b"hello")
        payload = {"id": 1, "name": "a.bin", "size": 5, "state": "uploaded"}
        with mock.patch("subprocess.run", return_value=self._proc(0, json.dumps(payload))):
            result = gate.GhCli(REPO, token="tok").upload_asset(7000, "a.bin", source)
        self.assertEqual(result["id"], 1)

    def test_upload_asset_fails_closed(self) -> None:
        source = self.tmp / "a.bin"
        source.write_bytes(b"hello")
        with mock.patch("subprocess.run", return_value=self._proc(1, "", "HTTP 422 asset exists")):
            with self.assertRaises(gate.GateError) as caught:
                gate.GhCli(REPO, token="tok").upload_asset(7000, "a.bin", source)
        self.assertIn("422", str(caught.exception))
        with mock.patch("subprocess.run", return_value=self._proc(0, "not json")):
            with self.assertRaises(gate.GateError):
                gate.GhCli(REPO, token="tok").upload_asset(7000, "a.bin", source)
        with mock.patch("subprocess.run", return_value=self._proc(0, "[]")):
            with self.assertRaises(gate.GateError):
                gate.GhCli(REPO, token="tok").upload_asset(7000, "a.bin", source)
        with self.assertRaises(gate.GateError):
            gate.GhCli(REPO, token="tok").upload_asset(0, "a.bin", source)

    def test_download_to_file_streams_stdout_without_output_flag(self) -> None:
        # 契约：gh api 没有 --output（gh api --help 已确认），必须把 stdout 绑到文件句柄，
        # 且不能开 text=True，否则非 UTF-8 的二进制会被改写。
        payload = b"\x00\xff\xfe binary \r\n MZ\x90\x00 end"
        captured: dict[str, Any] = {}

        def fake_run(command: list[str], **kwargs: Any) -> subprocess.CompletedProcess[Any]:
            captured["command"] = command
            captured["kwargs"] = kwargs
            handle = kwargs["stdout"]
            handle.write(payload)
            return subprocess.CompletedProcess(command, 0, None, b"")

        dest = self.tmp / "asset.bin"
        with mock.patch("subprocess.run", side_effect=fake_run):
            gate.GhCli(REPO, token="tok").download_to_file("repos/o/r/releases/assets/5", dest)
        self.assertEqual(dest.read_bytes(), payload)
        command = captured["command"]
        self.assertNotIn("--output", command)
        self.assertNotIn("--output", " ".join(command))
        self.assertIn("Accept: application/octet-stream", command)
        self.assertIn("github.com", command)
        kwargs = captured["kwargs"]
        self.assertTrue(hasattr(kwargs["stdout"], "write"))
        self.assertEqual(kwargs["stderr"], subprocess.PIPE)
        self.assertNotIn("text", kwargs)
        self.assertNotIn("input", kwargs)
        self.assertEqual(kwargs["timeout"], gate.DEFAULT_TIMEOUT_SECONDS)
        self.assertEqual(kwargs["env"]["GH_HOST"], "github.com")

    def test_download_failure_fails_closed_and_removes_partial_file(self) -> None:
        def fake_run(command: list[str], **kwargs: Any) -> subprocess.CompletedProcess[Any]:
            kwargs["stdout"].write(b"partial bytes")
            return subprocess.CompletedProcess(command, 1, None, b"gh: Server Error (HTTP 500)")

        dest = self.tmp / "asset.bin"
        with mock.patch("subprocess.run", side_effect=fake_run):
            with self.assertRaises(gate.GateError) as caught:
                gate.GhCli(REPO, token="tok").download_to_file("repos/o/r/releases/assets/5", dest)
        self.assertIn("HTTP 500", str(caught.exception))
        self.assertFalse(dest.exists())

    def test_download_rejects_empty_body(self) -> None:
        def fake_run(command: list[str], **kwargs: Any) -> subprocess.CompletedProcess[Any]:
            return subprocess.CompletedProcess(command, 0, None, b"")

        dest = self.tmp / "asset.bin"
        with mock.patch("subprocess.run", side_effect=fake_run):
            with self.assertRaises(gate.GateError) as caught:
                gate.GhCli(REPO, token="tok").download_to_file("repos/o/r/releases/assets/5", dest)
        self.assertIn("空文件", str(caught.exception))
        self.assertFalse(dest.exists())

    def test_download_timeout_fails_closed(self) -> None:
        dest = self.tmp / "asset.bin"
        with mock.patch(
            "subprocess.run", side_effect=subprocess.TimeoutExpired(["gh"], 120)
        ):
            with self.assertRaises(gate.GateError) as caught:
                gate.GhCli(REPO, token="tok", timeout=5).download_to_file(
                    "repos/o/r/releases/assets/5", dest
                )
        self.assertIn("超时", str(caught.exception))
        self.assertFalse(dest.exists())

    def test_download_requires_token_before_touching_disk(self) -> None:
        with mock.patch.dict(os.environ, {}, clear=True):
            with mock.patch("subprocess.run") as run:
                with self.assertRaises(gate.GateError):
                    gate.GhCli(REPO, token=None).download_to_file(
                        "repos/o/r/releases/assets/5", self.tmp / "asset.bin"
                    )
        self.assertFalse(run.called)

    def test_decode_stderr_handles_bytes_and_text(self) -> None:
        self.assertEqual(gate._decode_stderr(b"boom \xff"), "boom \ufffd")
        self.assertEqual(gate._decode_stderr("boom"), "boom")
        self.assertEqual(gate._decode_stderr(None), "")


# --------------------------------------------------------------------------- run / jobs / 环境


class RunVerificationTests(GateTestCase):
    def test_verify_run_accepts_matching_run(self) -> None:
        gh = FakeGh()
        run = gate.verify_run(gh, make_ctx())
        self.assertEqual(run["head_sha"], SHA)

    def test_verify_run_rejects_sha_mismatch(self) -> None:
        gh = FakeGh(run=fake_run(sha=OTHER_SHA))
        with self.assertRaises(gate.GateError) as caught:
            gate.verify_run(gh, make_ctx())
        self.assertIn("head_sha", str(caught.exception))

    def test_verify_run_rejects_non_push_event(self) -> None:
        gh = FakeGh(run=fake_run(event="workflow_dispatch"))
        with self.assertRaises(gate.GateError) as caught:
            gate.verify_run(gh, make_ctx())
        self.assertIn("push", str(caught.exception))

    def test_verify_run_rejects_other_workflow(self) -> None:
        gh = FakeGh(run=fake_run(path=".github/workflows/other.yml"))
        with self.assertRaises(gate.GateError) as caught:
            gate.verify_run(gh, make_ctx())
        self.assertIn("release.yml", str(caught.exception))

    def test_verify_run_accepts_reusable_workflow_ref_suffix(self) -> None:
        gh = FakeGh(run=fake_run(path=".github/workflows/release.yml@refs/heads/main"))
        gate.verify_run(gh, make_ctx())

    def test_verify_run_rejects_lookalike_workflow_paths(self) -> None:
        for path in (
            ".github/workflows/release.yml.bak",
            ".github/workflows/release.yml-other",
            ".github/workflows/release.yml/x",
            "x/.github/workflows/release.yml",
            ".github/workflows/release.yaml",
            "",
            None,
        ):
            with self.subTest(path=path):
                gh = FakeGh(run=fake_run(path=path))
                with self.assertRaises(gate.GateError):
                    gate.verify_run(gh, make_ctx())

    def test_verify_run_rejects_missing_run(self) -> None:
        with self.assertRaises(gate.GateError):
            gate.verify_run(FakeGh(), make_ctx(run_id="1234"))

    def test_verify_run_rejects_missing_attempt(self) -> None:
        run = fake_run()
        del run["run_attempt"]
        with self.assertRaises(gate.GateError):
            gate.verify_run(FakeGh(run=run), make_ctx())

    def test_verify_run_rejects_attempt_mismatch(self) -> None:
        gh = FakeGh(run=fake_run(attempt=2), jobs=fake_jobs(attempt=2))
        with self.assertRaises(gate.GateError) as caught:
            gate.verify_run(gh, make_ctx(run_attempt=1))
        self.assertIn("attempt", str(caught.exception))

    def test_verify_run_uses_attempt_scoped_jobs_endpoint(self) -> None:
        gh = FakeGh(run=fake_run(attempt=2), jobs=fake_jobs(attempt=2))
        gate.verify_run(gh, make_ctx(run_attempt=2))
        job_requests = [item for item in gh.requests if "/jobs?" in item[1]]
        self.assertEqual(len(job_requests), 1)
        self.assertIn("/actions/runs/9001/attempts/2/jobs?", job_requests[0][1])

    def test_verify_run_rejects_mixed_attempt_jobs(self) -> None:
        # 只重跑了失败 job 时，新 attempt 里没有完整 8 个 job：拒绝。
        gh = FakeGh(run=fake_run(attempt=2), jobs=fake_jobs(attempt=1))
        with self.assertRaises(gate.GateError) as caught:
            gate.verify_run(gh, make_ctx(run_attempt=2))
        self.assertIn("缺少 job", str(caught.exception))

    def test_verify_jobs_requires_every_expected_job_successful(self) -> None:
        verified = gate.verify_jobs(fake_jobs(), 1)
        self.assertEqual(set(verified), set(gate.EXPECTED_JOB_NAMES))

    def test_verify_jobs_rejects_failed_skipped_cancelled(self) -> None:
        for conclusion in ("failure", "skipped", "cancelled", "timed_out", None):
            with self.subTest(conclusion=conclusion):
                jobs = fake_jobs(overrides={gate.EXPECTED_JOB_NAMES[1]: conclusion})
                with self.assertRaises(gate.GateError) as caught:
                    gate.verify_jobs(jobs, 1)
                self.assertIn(gate.EXPECTED_JOB_NAMES[1], str(caught.exception))

    def test_verify_jobs_rejects_missing_job(self) -> None:
        jobs = [job for job in fake_jobs() if job["name"] != "linux / build"]
        with self.assertRaises(gate.GateError) as caught:
            gate.verify_jobs(jobs, 1)
        self.assertIn("linux / build", str(caught.exception))

    def test_verify_jobs_rejects_platform_failure(self) -> None:
        jobs = fake_jobs(overrides={"windows / build": "failure"})
        with self.assertRaises(gate.GateError) as caught:
            gate.verify_jobs(jobs, 1)
        self.assertIn("windows / build", str(caught.exception))

    def test_verify_jobs_ignores_older_attempt(self) -> None:
        jobs = fake_jobs(attempt=1, overrides={gate.EXPECTED_JOB_NAMES[0]: "failure"})
        jobs += fake_jobs(attempt=2)
        gate.verify_jobs(jobs, 2)

    def test_verify_jobs_rejects_duplicate_names(self) -> None:
        jobs = fake_jobs() + [fake_job(gate.EXPECTED_JOB_NAMES[0])]
        with self.assertRaises(gate.GateError) as caught:
            gate.verify_jobs(jobs, 1)
        self.assertIn("重名", str(caught.exception))

    def test_list_run_jobs_paginates(self) -> None:
        jobs = fake_jobs(attempt=1, extra=130)
        gh = FakeGh(jobs=jobs)
        listed = gate.list_run_jobs(gh, make_ctx())
        self.assertEqual(len(listed), len(jobs))
        pages = [request for request in gh.requests if "/jobs?" in request[1]]
        self.assertEqual(len(pages), 2)
        self.assertIn("page=2", pages[1][1])

    def test_environment_accepts_exactly_the_designated_reviewer(self) -> None:
        gate.verify_environment(FakeGh(), make_ctx())

    def test_environment_requires_exactly_one_required_reviewers_rule(self) -> None:
        with self.assertRaises(gate.GateError) as caught:
            gate.verify_environment(FakeGh(environment=None), make_ctx())
        self.assertIn("不存在", str(caught.exception))
        cases = {
            "没有审批规则": fake_environment(logins=None),
            "两条 required_reviewers": fake_environment_with_rules(
                reviewer_rule(user_reviewer()), reviewer_rule(user_reviewer())
            ),
            "只有别的规则": fake_environment_with_rules(
                {"id": 2, "type": "wait_timer", "wait_timer": 30}
            ),
        }
        for label, environment in cases.items():
            with self.subTest(label=label):
                with self.assertRaises(gate.GateError) as caught:
                    gate.verify_environment(FakeGh(environment=environment), make_ctx())
                self.assertIn("required_reviewers", str(caught.exception))

    def test_environment_rejects_missing_or_extra_reviewers(self) -> None:
        cases = {
            "别的用户": fake_environment(logins=("someone",)),
            "gzy + 另一个 User": fake_environment(logins=("gzy31007", "someone")),
            "gzy + Team": fake_environment_with_rules(
                reviewer_rule(user_reviewer(), team_reviewer())
            ),
            "只有 Team": fake_environment_with_rules(reviewer_rule(team_reviewer())),
            "未知 type": fake_environment_with_rules(reviewer_rule(user_reviewer(type_="Bot"))),
            "缺 type": fake_environment_with_rules(
                reviewer_rule({"reviewer": {"type": "User", "login": "gzy31007"}})
            ),
            "审核器条目不是对象": fake_environment_with_rules(reviewer_rule("gzy31007")),
            "reviewers 为空": fake_environment_with_rules(reviewer_rule()),
            "reviewers 不是数组": fake_environment_with_rules(
                {"id": 1, "type": "required_reviewers", "reviewers": {"login": "gzy31007"}}
            ),
            "User 条目缺 reviewer": fake_environment_with_rules(
                reviewer_rule({"type": "User"})
            ),
            "reviewer 里 login 不符": fake_environment_with_rules(
                reviewer_rule(user_reviewer("not-the-maintainer"))
            ),
            "reviewer 缺 login": fake_environment_with_rules(
                reviewer_rule({"type": "User", "reviewer": {"type": "User"}})
            ),
        }
        for label, environment in cases.items():
            with self.subTest(label=label):
                with self.assertRaises(gate.GateError):
                    gate.verify_environment(FakeGh(environment=environment), make_ctx())

    def test_environment_rejects_malformed_protection_rules(self) -> None:
        for environment in (
            fake_environment_with_rules("not-a-rule"),
            {"name": gate.RELEASE_ENVIRONMENT, "protection_rules": {"type": "required_reviewers"}},
            {"name": gate.RELEASE_ENVIRONMENT},
            ["not-an-object"],
        ):
            with self.subTest(environment=environment):
                with self.assertRaises(gate.GateError):
                    gate.verify_environment(FakeGh(environment=environment), make_ctx())

    def test_environment_ignores_unrelated_rule_types(self) -> None:
        environment = fake_environment_with_rules(
            {"id": 2, "type": "wait_timer", "wait_timer": 30},
            reviewer_rule(user_reviewer()),
        )
        gate.verify_environment(FakeGh(environment=environment), make_ctx())


# --------------------------------------------------------------------------- 远端 tag


class RemoteTagTests(GateTestCase):
    def test_resolve_remote_tag_missing(self) -> None:
        self.assertIsNone(gate.resolve_remote_tag(FakeGh(), make_ctx(), TAG))

    def test_resolve_remote_tag_peels_annotated_tag(self) -> None:
        tag_object = "1" * 40
        gh = FakeGh(tags={TAG: {"type": "tag", "sha": tag_object}})
        gh.tag_objects[tag_object] = {"object": {"type": "commit", "sha": SHA}}
        self.assertEqual(gate.resolve_remote_tag(gh, make_ctx(), TAG), SHA)

    def test_resolve_remote_tag_rejects_non_commit(self) -> None:
        gh = FakeGh(tags={TAG: {"type": "tree", "sha": SHA}})
        with self.assertRaises(gate.GateError):
            gate.resolve_remote_tag(gh, make_ctx(), TAG)

    def test_resolve_remote_tag_rejects_missing_tag_object(self) -> None:
        gh = FakeGh(tags={TAG: {"type": "tag", "sha": "2" * 40}})
        with self.assertRaises(gate.GateError):
            gate.resolve_remote_tag(gh, make_ctx(), TAG)

    def test_ensure_tag_ref_production_accepts_matching_tag(self) -> None:
        gh = FakeGh(tags={TAG: SHA})
        self.assertEqual(gate.ensure_tag_ref(gh, make_ctx()), SHA)

    def test_ensure_tag_ref_production_rejects_drift_and_missing(self) -> None:
        with self.assertRaises(gate.GateError) as caught:
            gate.ensure_tag_ref(FakeGh(tags={TAG: OTHER_SHA}), make_ctx())
        self.assertIn("漂移", str(caught.exception))
        with self.assertRaises(gate.GateError):
            gate.ensure_tag_ref(FakeGh(), make_ctx())

    def test_ensure_tag_ref_rehearsal_creates_dedicated_tag(self) -> None:
        gh = FakeGh()
        ctx = make_ctx(rehearsal=True)
        gate.ensure_tag_ref(gh, ctx)
        self.assertEqual(gh.tags[REHEARSAL_TAG]["sha"], SHA)
        posts = [request for request in gh.requests if request[0] == "POST"]
        self.assertEqual(posts[0][2]["ref"], f"refs/tags/{REHEARSAL_TAG}")

    def test_ensure_tag_ref_rehearsal_existing_tag_must_match(self) -> None:
        gate.ensure_tag_ref(FakeGh(tags={REHEARSAL_TAG: SHA}), make_ctx(rehearsal=True))
        with self.assertRaises(gate.GateError):
            gate.ensure_tag_ref(FakeGh(tags={REHEARSAL_TAG: OTHER_SHA}), make_ctx(rehearsal=True))

    def test_ensure_tag_ref_rehearsal_never_touches_production_tag(self) -> None:
        gh = FakeGh(tags={TAG: SHA})
        ctx = make_ctx(rehearsal=True)
        gate.ensure_tag_ref(gh, ctx)
        self.assertEqual(gh.tags[TAG], SHA)
        self.assertIn(REHEARSAL_TAG, gh.tags)


# --------------------------------------------------------------------------- 本地资产


class AssetCollectionTests(GateTestCase):
    def test_collects_three_assets_with_size_and_digest(self) -> None:
        assets = gate.collect_assets(self.assets_dir(), VERSION)
        self.assertEqual([item.name for item in assets], standard_assets())
        for item in assets:
            self.assertTrue(item.path.is_file())
            self.assertEqual(item.sha256, gate.sha256_file(item.path))
            self.assertEqual(item.size, item.path.stat().st_size)
            self.assertIsNone(item.as_expected().asset_id)

    def test_rejects_missing_asset(self) -> None:
        names = standard_assets()
        names.remove(names[2])
        directory = make_assets(self.tmp / "missing", names)
        with self.assertRaises(gate.GateError) as caught:
            gate.collect_assets(directory, VERSION)
        self.assertIn("缺少资产", str(caught.exception))

    def test_rejects_empty_asset(self) -> None:
        directory = self.assets_dir()
        (directory / standard_assets()[0]).write_bytes(b"")
        with self.assertRaises(gate.GateError) as caught:
            gate.collect_assets(directory, VERSION)
        self.assertIn("空文件", str(caught.exception))

    def test_rejects_symlink_asset(self) -> None:
        directory = self.assets_dir()
        target = directory / standard_assets()[0]
        real = self.tmp / "real.exe"
        real.write_bytes(b"payload")
        target.unlink()
        target.symlink_to(real)
        with self.assertRaises(gate.GateError) as caught:
            gate.collect_assets(directory, VERSION)
        self.assertIn("符号链接", str(caught.exception))

    def test_rejects_extra_file(self) -> None:
        directory = self.assets_dir()
        (directory / "unexpected.txt").write_text("x", encoding="utf-8")
        with self.assertRaises(gate.GateError) as caught:
            gate.collect_assets(directory, VERSION)
        self.assertIn("意外文件", str(caught.exception))

    def test_rejects_missing_directory_and_symlinked_directory(self) -> None:
        with self.assertRaises(gate.GateError):
            gate.collect_assets(self.tmp / "nope", VERSION)
        link = self.tmp / "linked"
        link.symlink_to(self.assets_dir())
        with self.assertRaises(gate.GateError):
            gate.collect_assets(link, VERSION)

    def test_rejects_directory_instead_of_file(self) -> None:
        directory = self.assets_dir()
        target = directory / standard_assets()[1]
        target.unlink()
        target.mkdir()
        with self.assertRaises(gate.GateError) as caught:
            gate.collect_assets(directory, VERSION)
        self.assertIn("普通文件", str(caught.exception))


# --------------------------------------------------------------------------- 远端资产核验


class RemoteAssetTests(GateTestCase):
    def _uploaded(self, **upload_effect: Any) -> FakeGh:
        gh = FakeGh()
        gh.releases[7000] = seeded_release(TAG)
        gh.assets[7000] = []
        assets = gate.collect_assets(self.assets_dir(), VERSION)
        for item in assets:
            gh.upload_asset(7000, item.name, item.path)
        return gh

    def _expected(self) -> list[gate.ExpectedAsset]:
        return [
            gate.ExpectedAsset(name=item.name, size=item.size, sha256=item.sha256)
            for item in gate.collect_assets(self.assets_dir(), VERSION)
        ]

    def test_verify_remote_assets_uses_server_digest(self) -> None:
        gh = self._uploaded()
        gate.verify_remote_assets(gh, make_ctx(), 7000, self._expected())
        self.assertEqual(gh.download_calls, [])

    def test_verify_remote_assets_downloads_when_digest_missing(self) -> None:
        gh = self._uploaded()
        for asset in gh.assets[7000]:
            asset["digest"] = None
        gate.verify_remote_assets(gh, make_ctx(), 7000, self._expected())
        self.assertEqual(len(gh.download_calls), 3)
        self.assertTrue(all("/releases/assets/" in call for call in gh.download_calls))

    def test_verify_remote_assets_rejects_digest_mismatch_from_download(self) -> None:
        gh = self._uploaded()
        assets = self._expected()
        for asset in gh.assets[7000]:
            asset["digest"] = None
        for asset_id in gh.downloads:
            gh.downloads[asset_id] = b"tampered"
        with self.assertRaises(gate.GateError) as caught:
            gate.verify_remote_assets(gh, make_ctx(), 7000, assets)
        self.assertIn("摘要", str(caught.exception))

    def test_verify_remote_assets_rejects_digest_field_mismatch(self) -> None:
        gh = self._uploaded()
        for asset in gh.assets[7000]:
            asset["digest"] = "sha256:" + "0" * 64
        with self.assertRaises(gate.GateError) as caught:
            gate.verify_remote_assets(gh, make_ctx(), 7000, self._expected())
        self.assertIn("摘要不符", str(caught.exception))

    def test_verify_remote_assets_rejects_asset_id_change(self) -> None:
        gh = self._uploaded()
        bound = gate.bind_remote_assets(self._expected(), gh.assets[7000])
        shifted = [
            gate.ExpectedAsset(
                name=item.name, size=item.size, sha256=item.sha256, asset_id=item.asset_id + 1
            )
            for item in bound
        ]
        with self.assertRaises(gate.GateError) as caught:
            gate.verify_remote_assets(gh, make_ctx(), 7000, shifted)
        self.assertIn("资产 id 变了", str(caught.exception))

    def test_verify_remote_assets_accepts_matching_asset_ids(self) -> None:
        gh = self._uploaded()
        bound = gate.bind_remote_assets(
            self._expected(),
            gate.list_release_assets(gh, make_ctx(), 7000),
        )
        self.assertEqual([item.asset_id for item in bound], [a["id"] for a in gh.assets[7000]])
        gate.verify_remote_assets(gh, make_ctx(), 7000, bound)

    def test_verify_remote_assets_rejects_size_and_missing_and_extra(self) -> None:
        gh = self._uploaded()
        gh.assets[7000][0]["size"] += 1
        with self.assertRaises(gate.GateError) as caught:
            gate.verify_remote_assets(gh, make_ctx(), 7000, self._expected())
        self.assertIn("大小不符", str(caught.exception))

        gh = self._uploaded()
        gh.assets[7000].pop()
        with self.assertRaises(gate.GateError) as caught:
            gate.verify_remote_assets(gh, make_ctx(), 7000, self._expected())
        self.assertIn("缺少资产", str(caught.exception))

        gh = self._uploaded()
        gh.assets[7000].append(
            {"id": 1, "name": "extra.txt", "size": 1, "state": "uploaded", "digest": "sha256:" + "0" * 64}
        )
        with self.assertRaises(gate.GateError) as caught:
            gate.verify_remote_assets(gh, make_ctx(), 7000, self._expected())
        self.assertIn("额外资产", str(caught.exception))

    def test_verify_remote_assets_rejects_unuploaded_state(self) -> None:
        gh = self._uploaded()
        gh.assets[7000][0]["state"] = "starter"
        with self.assertRaises(gate.GateError) as caught:
            gate.verify_remote_assets(gh, make_ctx(), 7000, self._expected())
        self.assertIn("uploaded", str(caught.exception))

    def test_asset_sha256_requires_id_when_digest_absent(self) -> None:
        with self.assertRaises(gate.GateError):
            gate.asset_sha256(FakeGh(), make_ctx(), {"name": "x", "size": 1, "digest": None})

    def test_asset_sha256_accepts_valid_digest(self) -> None:
        digest = hashlib.sha256(b"x").hexdigest()
        self.assertEqual(
            gate.asset_sha256(FakeGh(), make_ctx(), {"id": 3, "digest": f"sha256:{digest}"}), digest
        )

    def test_bind_remote_assets_rejects_missing_name(self) -> None:
        with self.assertRaises(gate.GateError):
            gate.bind_remote_assets(self._expected(), [])


# --------------------------------------------------------------------------- Release 内容


class ReleaseContentTests(GateTestCase):
    def test_release_notes_uses_repo_file(self) -> None:
        self.assertIn(f"# v{VERSION} 发布说明", gate.release_notes(self.repo_root, VERSION))

    def test_release_notes_falls_back_to_default(self) -> None:
        root = make_repo_root(self.tmp / "nonotes", notes=False)
        notes = gate.release_notes(root, VERSION)
        self.assertIn(f"v{VERSION}", notes)
        self.assertIn("发布门禁", notes)

    def test_release_notes_ignores_symlink(self) -> None:
        root = make_repo_root(self.tmp / "linknotes")
        notes_path = root / "docs" / "releases" / f"v{VERSION}.md"
        outside = self.tmp / "outside.md"
        outside.write_text("outside", encoding="utf-8")
        notes_path.unlink()
        notes_path.symlink_to(outside)
        self.assertIn("发布门禁", gate.release_notes(root, VERSION))

    def test_release_body_carries_sha_run_and_attempt(self) -> None:
        body = gate.release_body(make_ctx(run_attempt=2), "说明")
        self.assertIn(SHA, body)
        self.assertIn(RUN_ID, body)
        self.assertIn("attempt=2", body)
        self.assertIn("rehearsal=false", body)
        self.assertIn("说明", body)

    def test_release_title_marks_rehearsal(self) -> None:
        self.assertEqual(gate.release_title(make_ctx()), f"TJDesktopTimetable v{VERSION}")
        rehearsal = gate.release_title(make_ctx(rehearsal=True))
        self.assertIn("演练", rehearsal)
        self.assertIn(REHEARSAL_TAG, rehearsal)

    def test_verify_release_metadata_checks_every_field(self) -> None:
        ctx = make_ctx()
        good = seeded_release(TAG)
        gate.verify_release_metadata(good, ctx, require_draft=True)
        for mutate in (
            lambda release: release.update(tag_name="v9.9.9"),
            lambda release: release.update(target_commitish=OTHER_SHA),
            lambda release: release.update(draft=False),
            lambda release: release.update(prerelease=True),
        ):
            release = dict(good)
            mutate(release)
            with self.subTest(release=release):
                with self.assertRaises(gate.GateError):
                    gate.verify_release_metadata(release, ctx, require_draft=True)

    def test_verify_release_metadata_prerelease_follows_version(self) -> None:
        ctx = make_ctx(version=PRE_VERSION)
        release = seeded_release(_TAG_BY_VERSION[PRE_VERSION], prerelease=True)
        gate.verify_release_metadata(release, ctx, require_draft=True)

    def test_release_snapshot_and_verify(self) -> None:
        release = seeded_release(TAG)
        snapshot = gate.release_snapshot(release)
        self.assertEqual(
            snapshot, {"name": release["name"], "body": release["body"], "prerelease": False}
        )
        gate.verify_release_snapshot(release, snapshot)
        for mutate in (
            lambda item: item.update(name="changed"),
            lambda item: item.update(body="changed"),
            lambda item: item.update(prerelease=True),
        ):
            broken = dict(release)
            mutate(broken)
            with self.subTest(broken=broken):
                with self.assertRaises(gate.GateError):
                    gate.verify_release_snapshot(broken, snapshot)

    def test_release_snapshot_rejects_incomplete_release(self) -> None:
        with self.assertRaises(gate.GateError):
            gate.release_snapshot({"id": 1, "name": None, "body": "x", "prerelease": False})
        with self.assertRaises(gate.GateError):
            gate.release_snapshot({"id": 1, "name": "x", "body": "x", "prerelease": None})

    def test_create_draft_release_posts_draft_payload(self) -> None:
        gh = FakeGh()
        release = gate.create_draft_release(gh, make_ctx(), "body")
        self.assertTrue(release["draft"])
        request = gh.release_posts()[0]
        self.assertEqual(request[2]["tag_name"], TAG)
        self.assertEqual(request[2]["target_commitish"], SHA)
        self.assertIs(request[2]["draft"], True)
        self.assertIs(request[2]["prerelease"], False)

    def test_create_draft_release_fails_closed_on_non_draft_response(self) -> None:
        class NonDraftGh(FakeGh):
            def api(self, method: str, path: str, payload: Mapping[str, Any] | None = None) -> Any:
                return {"id": 1, "draft": False}

        with self.assertRaises(gate.GateError):
            gate.create_draft_release(NonDraftGh(), make_ctx(), "body")

        class NoIdGh(FakeGh):
            def api(self, method: str, path: str, payload: Mapping[str, Any] | None = None) -> Any:
                return {"draft": True}

        with self.assertRaises(gate.GateError):
            gate.create_draft_release(NoIdGh(), make_ctx(), "body")

    def test_upload_assets_binds_release_id_and_passes_three_paths(self) -> None:
        gh = FakeGh()
        gh.releases[7000] = seeded_release(TAG)
        gh.assets[7000] = []
        assets = gate.collect_assets(self.assets_dir(), VERSION)
        uploaded = gate.upload_assets(gh, make_ctx(), 7000, assets)
        self.assertEqual([item["name"] for item in uploaded], [a.name for a in assets])
        self.assertEqual([item[0] for item in gh.uploads], [7000, 7000, 7000])
        self.assertEqual(len(gh.assets[7000]), 3)


# --------------------------------------------------------------------------- manifest


class ManifestTests(GateTestCase):
    def _payload(self) -> dict[str, Any]:
        ctx = make_ctx()
        assets = gate.collect_assets(self.assets_dir(), VERSION)
        expected = [
            gate.ExpectedAsset(name=item.name, size=item.size, sha256=item.sha256, asset_id=100 + index)
            for index, item in enumerate(assets)
        ]
        release = seeded_release(TAG)
        return gate.manifest_payload(ctx, release=release, expected=expected)

    def test_manifest_roundtrip(self) -> None:
        payload = self._payload()
        path = self.manifest_path()
        gate.write_manifest(path, payload)
        self.assertEqual(gate.read_manifest(path), payload)

    def test_manifest_binds_repo_attempt_release_and_asset_ids(self) -> None:
        payload = self._payload()
        self.assertEqual(payload["repo"], REPO)
        self.assertEqual(payload["run_attempt"], 1)
        self.assertEqual(set(payload["release"]), {"name", "body", "prerelease"})
        self.assertEqual([item["asset_id"] for item in payload["expected"]], [100, 101, 102])
        for item in payload["expected"]:
            self.assertEqual(set(item), {"name", "size", "sha256", "asset_id"})

    def test_manifest_payload_rejects_unbound_asset(self) -> None:
        ctx = make_ctx()
        assets = [
            gate.ExpectedAsset(name=item.name, size=item.size, sha256=item.sha256)
            for item in gate.collect_assets(self.assets_dir(), VERSION)
        ]
        with self.assertRaises(gate.GateError) as caught:
            gate.manifest_payload(ctx, release=seeded_release(TAG), expected=assets)
        self.assertIn("服务端 id", str(caught.exception))

    def test_manifest_expected_projection(self) -> None:
        payload = self._payload()
        expected = gate.manifest_expected(payload)
        self.assertEqual([item.name for item in expected], standard_assets())
        self.assertEqual(expected[0].sha256, payload["expected"][0]["sha256"])
        self.assertEqual(expected[0].asset_id, 100)

    def test_verify_manifest_mismatches(self) -> None:
        payload = self._payload()
        gate.verify_manifest(payload, make_ctx(), version=VERSION)
        for field, value in (
            ("repo", "someone/else"),
            ("tag", "v9.9.9"),
            ("sha", OTHER_SHA),
            ("version", PRE_VERSION),
            ("run_id", "1"),
            ("run_attempt", 2),
            ("rehearsal", True),
        ):
            broken = dict(payload)
            broken[field] = value
            with self.subTest(field=field):
                with self.assertRaises(gate.GateError):
                    gate.verify_manifest(broken, make_ctx(), version=VERSION)

    def test_read_manifest_rejects_broken_files(self) -> None:
        path = self.manifest_path()
        with self.assertRaises(gate.GateError):
            gate.read_manifest(self.tmp / "absent.json")

        path.write_text("not json", encoding="utf-8")
        with self.assertRaises(gate.GateError):
            gate.read_manifest(path)

        path.write_text("[]", encoding="utf-8")
        with self.assertRaises(gate.GateError):
            gate.read_manifest(path)

        good = self._payload()
        for mutate in (
            lambda data: data.pop("sha"),
            lambda data: data.pop("repo"),
            lambda data: data.update(repo="bad repo"),
            lambda data: data.update(release_id=0),
            lambda data: data.update(run_attempt="2"),
            lambda data: data.update(run_attempt=0),
            lambda data: data.update(rehearsal="no"),
            lambda data: data["release"].pop("prerelease"),
            lambda data: data["release"].update(prerelease="no"),
            lambda data: data["expected"][0].update(sha256="short"),
            lambda data: data["expected"][0].update(size=0),
            lambda data: data["expected"][0].update(asset_id=0),
            lambda data: data["expected"][0].pop("asset_id"),
            lambda data: data["expected"].pop(),
        ):
            data = copy.deepcopy(good)
            mutate(data)
            path.write_text(json.dumps(data), encoding="utf-8")
            with self.subTest(data=data):
                with self.assertRaises(gate.GateError):
                    gate.read_manifest(path)

    def test_read_manifest_rejects_wrong_asset_set(self) -> None:
        data = self._payload()
        data["expected"][0]["name"] = f"TJDesktopTimetable-v{PRE_VERSION}-setup.exe"
        path = self.manifest_path()
        path.write_text(json.dumps(data), encoding="utf-8")
        with self.assertRaises(gate.GateError) as caught:
            gate.read_manifest(path)
        self.assertIn("三件资产", str(caught.exception))


# --------------------------------------------------------------------------- Release 清单


class ReleaseLookupTests(GateTestCase):
    def test_list_releases_paginates_to_the_end(self) -> None:
        gh = FakeGh()
        for index in range(205):
            release_id = 10000 + index
            gh.releases[release_id] = seeded_release(f"v0.0.{index}", release_id=release_id)
            gh.assets[release_id] = []
        releases = gate.list_releases(gh, make_ctx())
        self.assertEqual(len(releases), 205)
        pages = [path for method, path, _ in gh.requests if "page=" in path]
        self.assertEqual(len(pages), 3)

    def test_find_release_by_tag_matches_exact_tag_only(self) -> None:
        gh = FakeGh()
        gh.releases[1] = seeded_release(f"{TAG}-other", release_id=1)
        gh.assets[1] = []
        gh.releases[2] = seeded_release(TAG, release_id=2)
        gh.assets[2] = []
        found = gate.find_release_by_tag(gh, make_ctx())
        self.assertEqual(found["id"], 2)
        self.assertIsNone(gate.find_release_by_tag(FakeGh(), make_ctx()))

    def test_find_release_by_tag_rejects_duplicates(self) -> None:
        gh = FakeGh()
        gh.releases[1] = seeded_release(TAG, release_id=1)
        gh.assets[1] = []
        gh.releases[2] = seeded_release(TAG, release_id=2)
        gh.assets[2] = []
        with self.assertRaises(gate.GateError) as caught:
            gate.find_release_by_tag(gh, make_ctx())
        self.assertIn("多个 Release", str(caught.exception))

    def test_list_releases_rejects_non_list_payload(self) -> None:
        class BadGh(FakeGh):
            def api(self, method: str, path: str, payload: Any = None) -> Any:
                return {"message": "nope"}

        with self.assertRaises(gate.GateError):
            gate.list_releases(BadGh(), make_ctx())

    def test_list_releases_rejects_non_object_entries(self) -> None:
        class BadGh(FakeGh):
            def api(self, method: str, path: str, payload: Any = None) -> Any:
                return ["oops"]

        with self.assertRaises(gate.GateError):
            gate.list_releases(BadGh(), make_ctx())


# --------------------------------------------------------------------------- draft


class DraftTests(GateTestCase):
    def test_draft_production_creates_private_release_and_manifest(self) -> None:
        ctx = make_ctx()
        gh = FakeGh(tags={TAG: SHA})
        payload = self.draft(ctx, gh)
        self.assertEqual(payload["tag"], TAG)
        self.assertFalse(payload["rehearsal"])
        release = gh.releases_by_tag[TAG]
        self.assertTrue(release["draft"])
        self.assertEqual(release["target_commitish"], SHA)
        self.assertFalse(release["prerelease"])
        self.assertEqual(len(gh.uploads), 3)
        self.assertEqual({item[0] for item in gh.uploads}, {release["id"]})
        self.assertEqual(
            sorted(Path(item[1][0]).name for item in gh.uploads), sorted(standard_assets())
        )
        self.assertEqual(len(gh.release_posts()), 1)
        self.assertIs(gh.release_posts()[0][2]["draft"], True)
        manifest = gate.read_manifest(self.manifest_path())
        self.assertEqual(manifest["release_id"], release["id"])
        self.assertEqual(manifest["repo"], REPO)
        self.assertEqual(manifest["run_attempt"], 1)
        self.assertEqual(
            [item["asset_id"] for item in manifest["expected"]],
            [asset["id"] for asset in gh.assets[release["id"]]],
        )
        self.assertEqual(manifest["release"]["body"], release["body"])

    def test_draft_rehearsal_creates_dedicated_tag_and_release(self) -> None:
        ctx = make_ctx(rehearsal=True)
        gh = FakeGh()
        self.draft(ctx, gh)
        self.assertIn(REHEARSAL_TAG, gh.tags)
        self.assertIn(REHEARSAL_TAG, gh.releases_by_tag)
        self.assertNotIn(TAG, gh.tags)

    def test_draft_rejects_existing_draft_release(self) -> None:
        gh = FakeGh(tags={TAG: SHA}, releases_by_tag={TAG: seeded_release(TAG, draft=True)})
        with self.assertRaises(gate.GateError) as caught:
            self.draft(make_ctx(), gh)
        self.assertIn("draft", str(caught.exception))
        self.assertEqual(gh.release_posts(), [])
        self.assertFalse(self.manifest_path().exists())

    def test_draft_rejects_existing_draft_invisible_to_tag_endpoint(self) -> None:
        # 实机证据：draft Release 在 REST 的 /releases/tags/<tag> 上是 404，
        # 只有分页清单能看到。既存判定绝不能只信 tag 端点。
        gh = FakeGh(tags={TAG: SHA})
        gh.releases[7000] = seeded_release(TAG, draft=True)
        gh.assets[7000] = []
        self.assertIsNone(gh.api("GET", f"repos/{REPO}/releases/tags/{TAG}"))
        gh.requests.clear()  # 上面的探针请求不算 gate 的行为
        with self.assertRaises(gate.GateError) as caught:
            self.draft(make_ctx(), gh)
        self.assertIn("拒绝覆写", str(caught.exception))
        self.assertEqual(gh.release_posts(), [])
        self.assertFalse(self.manifest_path().exists())
        self.assertFalse(
            any("/releases/tags/" in path for method, path, _ in gh.requests if method == "GET")
        )

    def test_draft_rejects_existing_public_release(self) -> None:
        gh = FakeGh(tags={TAG: SHA}, releases_by_tag={TAG: seeded_release(TAG, draft=False)})
        with self.assertRaises(gate.GateError) as caught:
            self.draft(make_ctx(), gh)
        self.assertIn("已公开", str(caught.exception))
        self.assertFalse(self.manifest_path().exists())

    def test_draft_rejects_run_sha_mismatch_before_any_write(self) -> None:
        gh = FakeGh(run=fake_run(sha=OTHER_SHA), tags={TAG: SHA})
        with self.assertRaises(gate.GateError):
            self.draft(make_ctx(), gh)
        self.assertEqual(gh.release_count(), 0)
        self.assertEqual(gh.uploads, [])
        self.assertFalse(self.manifest_path().exists())

    def test_draft_rejects_failed_or_skipped_jobs(self) -> None:
        for conclusion in ("failure", "skipped", "cancelled"):
            with self.subTest(conclusion=conclusion):
                gh = FakeGh(
                    jobs=fake_jobs(overrides={"windows / build": conclusion}),
                    tags={TAG: SHA},
                )
                with self.assertRaises(gate.GateError):
                    self.draft(make_ctx(), gh)
                self.assertEqual(gh.release_count(), 0)

    def test_draft_rejects_missing_platform_job(self) -> None:
        jobs = [job for job in fake_jobs() if job["name"] != "linux / build"]
        gh = FakeGh(jobs=jobs, tags={TAG: SHA})
        with self.assertRaises(gate.GateError) as caught:
            self.draft(make_ctx(), gh)
        self.assertIn("linux / build", str(caught.exception))

    def test_draft_rejects_missing_reviewer(self) -> None:
        gh = FakeGh(tags={TAG: SHA}, environment=fake_environment(logins=("other",)))
        with self.assertRaises(gate.GateError) as caught:
            self.draft(make_ctx(), gh)
        self.assertIn("审核器", str(caught.exception))
        self.assertEqual(gh.release_count(), 0)

    def test_draft_rejects_extra_approver(self) -> None:
        for environment in (
            fake_environment(logins=("gzy31007", "someone")),
            fake_environment_with_rules(reviewer_rule(user_reviewer(), team_reviewer())),
        ):
            with self.subTest(environment=environment):
                gh = FakeGh(tags={TAG: SHA}, environment=environment)
                with self.assertRaises(gate.GateError):
                    self.draft(make_ctx(), gh)
                self.assertEqual(gh.release_count(), 0)
                self.assertEqual(gh.uploads, [])
                self.assertFalse(self.manifest_path().exists())

    def test_draft_rejects_tag_drift(self) -> None:
        gh = FakeGh(tags={TAG: OTHER_SHA})
        with self.assertRaises(gate.GateError):
            self.draft(make_ctx(), gh)
        self.assertEqual(gh.release_count(), 0)

    def test_draft_rejects_missing_asset_without_api_writes(self) -> None:
        directory = make_assets(self.tmp / "partial", standard_assets()[:2])
        gh = FakeGh(tags={TAG: SHA})
        with self.assertRaises(gate.GateError) as caught:
            self.draft(make_ctx(), gh, assets_dir=directory)
        self.assertIn("缺少资产", str(caught.exception))
        self.assertEqual(gh.requests, [])
        self.assertFalse(self.manifest_path().exists())

    def test_draft_fails_when_server_asset_incomplete(self) -> None:
        gh = FakeGh(tags={TAG: SHA}, on_upload=lambda release: gh.assets[release["id"]].pop())
        with self.assertRaises(gate.GateError) as caught:
            self.draft(make_ctx(), gh)
        self.assertIn("缺少资产", str(caught.exception))
        self.assertFalse(self.manifest_path().exists())

    def test_draft_fails_when_server_digest_mismatch(self) -> None:
        def on_upload(release: dict[str, Any]) -> None:
            gh.assets[release["id"]][0]["digest"] = "sha256:" + "0" * 64

        gh = FakeGh(tags={TAG: SHA}, on_upload=on_upload)
        with self.assertRaises(gate.GateError) as caught:
            self.draft(make_ctx(), gh)
        self.assertIn("摘要不符", str(caught.exception))
        self.assertFalse(self.manifest_path().exists())

    def test_draft_fails_when_digest_missing_and_download_differs(self) -> None:
        def on_upload(release: dict[str, Any]) -> None:
            for asset in gh.assets[release["id"]]:
                asset["digest"] = None
            for asset_id in list(gh.downloads):
                gh.downloads[asset_id] = b"other bytes"

        gh = FakeGh(tags={TAG: SHA}, on_upload=on_upload)
        with self.assertRaises(gate.GateError):
            self.draft(make_ctx(), gh)
        self.assertFalse(self.manifest_path().exists())

    def test_draft_accepts_missing_digest_when_download_matches(self) -> None:
        def on_upload(release: dict[str, Any]) -> None:
            for asset in gh.assets[release["id"]]:
                asset["digest"] = None

        gh = FakeGh(tags={TAG: SHA}, on_upload=on_upload)
        payload = self.draft(make_ctx(), gh)
        self.assertEqual(len(payload["expected"]), 3)
        self.assertEqual(len(gh.download_calls), 3)

    def test_draft_fails_when_release_readback_not_draft(self) -> None:
        gh = FakeGh(tags={TAG: SHA})
        original_api = gh.api

        def api(method: str, path: str, payload: Mapping[str, Any] | None = None) -> Any:
            result = original_api(method, path, payload)
            if method == "GET" and re.fullmatch(rf"repos/{re.escape(REPO)}/releases/\d+", path):
                result["draft"] = False
            return result

        gh.api = api  # type: ignore[method-assign]
        with self.assertRaises(gate.GateError):
            self.draft(make_ctx(), gh)
        self.assertFalse(self.manifest_path().exists())


# --------------------------------------------------------------------------- publish


class PublishTests(GateTestCase):
    def _prepare_draft(self, ctx: gate.GateContext | None = None) -> tuple[gate.GateContext, FakeGh, int]:
        ctx = ctx or make_ctx()
        gh = FakeGh(tags={TAG: SHA}, run=fake_run(attempt=ctx.run_attempt))
        payload = self.draft(ctx, gh)
        return ctx, gh, int(payload["release_id"])

    def test_publish_production_flips_draft_after_all_checks(self) -> None:
        ctx, gh, release_id = self._prepare_draft()
        result = self.publish(ctx, gh)
        self.assertEqual(result["release_id"], release_id)
        self.assertEqual(result["tag"], TAG)
        patches = gh.publish_patches()
        self.assertEqual(len(patches), 1)
        self.assertEqual(patches[0][1], f"repos/{REPO}/releases/{release_id}")
        self.assertEqual(patches[0][2], {"draft": False})
        self.assertFalse(gh.releases[release_id]["draft"])
        self.assertIsNotNone(gh.releases[release_id]["published_at"])

    def test_publish_rehearsal_is_always_refused(self) -> None:
        ctx, gh, _ = self._prepare_draft()
        rehearsal_ctx = make_ctx(rehearsal=True)
        with self.assertRaises(gate.GateError) as caught:
            self.publish(rehearsal_ctx, gh)
        self.assertIn("演练", str(caught.exception))
        self.assertEqual(gh.publish_patches(), [])

    def test_publish_rehearsal_refused_before_touching_api(self) -> None:
        ctx = make_ctx(rehearsal=True)
        gh = FakeGh(tags={REHEARSAL_TAG: SHA})
        with self.assertRaises(gate.GateError):
            self.publish(ctx, gh, env={"GITHUB_REF": f"refs/heads/release-gate-test/x"})
        self.assertEqual(gh.requests, [])

    def test_publish_revalidates_trigger_ref(self) -> None:
        ctx, gh, _ = self._prepare_draft()
        for env in (
            {"GITHUB_REF": "refs/heads/main"},
            {"GITHUB_REF": "refs/tags/v9.9.9"},
            {},
        ):
            with self.subTest(env=env):
                with self.assertRaises(gate.GateError):
                    self.publish(ctx, gh, env=env)
                self.assertEqual(gh.publish_patches(), [])

    def test_publish_rejects_manifest_parameter_mismatch(self) -> None:
        ctx, gh, _ = self._prepare_draft()
        for field, value in (
            ("tag", REHEARSAL_TAG),
            ("sha", OTHER_SHA),
            ("run_id", "1"),
            ("run_attempt", 2),
            ("repo", "someone/else"),
        ):
            data = json.loads(self.manifest_path().read_text(encoding="utf-8"))
            data[field] = value
            self.manifest_path().write_text(json.dumps(data), encoding="utf-8")
            with self.subTest(field=field):
                with self.assertRaises(gate.GateError):
                    self.publish(ctx, gh)
                self.assertEqual(gh.publish_patches(), [])

    def test_publish_rejects_manifest_version_change(self) -> None:
        ctx, gh, _ = self._prepare_draft()
        props = self.repo_root / "dotnet" / "Directory.Build.props"
        props.write_text(
            "<Project><PropertyGroup><Version>1.7.0</Version></PropertyGroup></Project>",
            encoding="utf-8",
        )
        with self.assertRaises(gate.GateError) as caught:
            self.publish(ctx, gh)
        self.assertIn("version", str(caught.exception))
        self.assertEqual(gh.publish_patches(), [])

    def test_publish_rejects_tag_drift_after_approval(self) -> None:
        ctx, gh, _ = self._prepare_draft()
        gh.tags[TAG] = OTHER_SHA
        with self.assertRaises(gate.GateError) as caught:
            self.publish(ctx, gh)
        self.assertIn("漂移", str(caught.exception))
        self.assertEqual(gh.publish_patches(), [])

    def test_publish_rejects_vanished_tag(self) -> None:
        ctx, gh, _ = self._prepare_draft()
        gh.tags.clear()
        with self.assertRaises(gate.GateError):
            self.publish(ctx, gh)
        self.assertEqual(gh.publish_patches(), [])

    def test_publish_rechecks_run_and_jobs(self) -> None:
        ctx, gh, _ = self._prepare_draft()
        gh.jobs = fake_jobs(overrides={"ci / linux-shell": "failure"})
        with self.assertRaises(gate.GateError):
            self.publish(ctx, gh)
        self.assertEqual(gh.publish_patches(), [])

        ctx, gh, _ = self._prepare_draft()
        gh.run = fake_run(sha=OTHER_SHA)
        with self.assertRaises(gate.GateError):
            self.publish(ctx, gh)
        self.assertEqual(gh.publish_patches(), [])

    def test_publish_rechecks_environment(self) -> None:
        for environment in (
            fake_environment(logins=("someone-else",)),
            fake_environment(logins=("gzy31007", "someone-else")),
            fake_environment_with_rules(reviewer_rule(user_reviewer(), team_reviewer())),
            fake_environment_with_rules(reviewer_rule()),
            None,
        ):
            with self.subTest(environment=environment):
                ctx, gh, release_id = self._prepare_draft()
                gh.environment = environment
                with self.assertRaises(gate.GateError):
                    self.publish(ctx, gh)
                self.assertEqual(gh.publish_patches(), [])
                self.assertTrue(gh.releases[release_id]["draft"])

    def test_publish_rejects_release_already_public(self) -> None:
        ctx, gh, release_id = self._prepare_draft()
        gh.releases[release_id]["draft"] = False
        with self.assertRaises(gate.GateError) as caught:
            self.publish(ctx, gh)
        self.assertIn("不是 draft", str(caught.exception))
        self.assertEqual(gh.publish_patches(), [])

    def test_publish_rejects_metadata_changed_after_approval(self) -> None:
        ctx, gh, release_id = self._prepare_draft()
        gh.releases[release_id]["target_commitish"] = OTHER_SHA
        with self.assertRaises(gate.GateError) as caught:
            self.publish(ctx, gh)
        self.assertIn("target_commitish", str(caught.exception))
        self.assertEqual(gh.publish_patches(), [])

    def test_publish_rejects_release_snapshot_changed_after_approval(self) -> None:
        for field, value in (("name", "改过的标题"), ("body", "改过的正文"), ("prerelease", True)):
            ctx, gh, release_id = self._prepare_draft()
            gh.releases[release_id][field] = value
            with self.subTest(field=field):
                with self.assertRaises(gate.GateError):
                    self.publish(ctx, gh)
                self.assertEqual(gh.publish_patches(), [])

    def test_publish_rejects_server_assets_changed_after_approval(self) -> None:
        ctx, gh, release_id = self._prepare_draft()
        gh.assets[release_id][0]["digest"] = "sha256:" + "1" * 64
        with self.assertRaises(gate.GateError):
            self.publish(ctx, gh)
        self.assertEqual(gh.publish_patches(), [])

        ctx, gh, release_id = self._prepare_draft()
        gh.assets[release_id].append(
            {"id": 5, "name": "extra.bin", "size": 3, "state": "uploaded", "digest": "sha256:" + "0" * 64}
        )
        with self.assertRaises(gate.GateError) as caught:
            self.publish(ctx, gh)
        self.assertIn("额外资产", str(caught.exception))
        self.assertEqual(gh.publish_patches(), [])

    def test_publish_rejects_reuploaded_asset_with_new_id(self) -> None:
        ctx, gh, release_id = self._prepare_draft()
        gh.assets[release_id][0]["id"] += 500
        with self.assertRaises(gate.GateError) as caught:
            self.publish(ctx, gh)
        self.assertIn("资产 id 变了", str(caught.exception))
        self.assertEqual(gh.publish_patches(), [])

    def test_publish_rejects_missing_server_asset(self) -> None:
        ctx, gh, release_id = self._prepare_draft()
        gh.assets[release_id].pop()
        with self.assertRaises(gate.GateError):
            self.publish(ctx, gh)
        self.assertEqual(gh.publish_patches(), [])

    def test_publish_fails_closed_when_patch_does_not_stick(self) -> None:
        def patch_effect(release: dict[str, Any], payload: Mapping[str, Any]) -> None:
            release["draft"] = True  # 服务端没接受 draft=false

        ctx, gh, _ = self._prepare_draft()
        gh.patch_effect = patch_effect
        with self.assertRaises(gate.GateError) as caught:
            self.publish(ctx, gh)
        self.assertIn("仍不是公开状态", str(caught.exception))

    def test_publish_fails_closed_when_missing_manifest(self) -> None:
        ctx = make_ctx()
        gh = FakeGh(tags={TAG: SHA})
        with self.assertRaises(gate.GateError) as caught:
            self.publish(ctx, gh)
        self.assertIn("manifest", str(caught.exception))
        self.assertEqual(gh.publish_patches(), [])

    def test_publish_rejects_unknown_release(self) -> None:
        ctx, gh, release_id = self._prepare_draft()
        del gh.releases[release_id]
        with self.assertRaises(gate.GateError):
            self.publish(ctx, gh)
        self.assertEqual(gh.publish_patches(), [])

    def test_publish_works_with_full_rerun_attempt(self) -> None:
        ctx, gh, release_id = self._prepare_draft(make_ctx(run_attempt=2))
        result = self.publish(ctx, gh, env={**trigger_env(), "GITHUB_RUN_ATTEMPT": "2"})
        self.assertEqual(result["run_attempt"], 2)
        self.assertFalse(gh.releases[release_id]["draft"])

    def test_publish_rejects_mixed_attempt_after_approval(self) -> None:
        ctx, gh, _ = self._prepare_draft(make_ctx(run_attempt=2))
        gh.jobs = fake_jobs(attempt=1)
        with self.assertRaises(gate.GateError):
            self.publish(ctx, gh, env={**trigger_env(), "GITHUB_RUN_ATTEMPT": "2"})
        self.assertEqual(gh.publish_patches(), [])


# --------------------------------------------------------------------------- 入口


class MainEntryTests(GateTestCase):
    def test_main_prepare_success_prints_json_and_writes_outputs(self) -> None:
        output = self.tmp / "github-output.txt"
        env = {
            "GITHUB_REF": f"refs/tags/{TAG}",
            "GITHUB_REPOSITORY": REPO,
            "GITHUB_OUTPUT": str(output),
        }
        with mock.patch.object(gate, "git_head", return_value=SHA), mock.patch.object(
            gate, "git_tag_commit", return_value=SHA
        ), mock.patch("sys.stdout") as fake_stdout:
            code = gate.main(
                ["prepare", "--sha", SHA, "--tag", TAG, "--run-id", RUN_ID, "--rehearsal", "false"],
                env=env,
                repo_root=self.repo_root,
            )
        self.assertEqual(code, 0)
        printed = json.loads(fake_stdout.write.call_args_list[0].args[0])
        self.assertEqual(printed["tag"], TAG)
        self.assertIn("rehearsal=false", output.read_text(encoding="utf-8"))

    def test_main_prepare_failure_returns_one(self) -> None:
        env = {"GITHUB_REF": f"refs/tags/{TAG}", "GITHUB_REPOSITORY": REPO}
        with mock.patch.object(gate, "git_head", return_value=OTHER_SHA), mock.patch(
            "sys.stderr"
        ) as fake_stderr:
            code = gate.main(
                ["prepare", "--sha", SHA, "--tag", TAG, "--run-id", RUN_ID, "--rehearsal", "false"],
                env=env,
                repo_root=self.repo_root,
            )
        self.assertEqual(code, 1)
        self.assertIn("拒绝", fake_stderr.write.call_args_list[0].args[0])

    def test_main_draft_uses_injected_gh_factory(self) -> None:
        env = {
            "GITHUB_REF": f"refs/tags/{TAG}",
            "GITHUB_REPOSITORY": REPO,
            "GH_TOKEN": "tok",
            "GITHUB_RUN_ATTEMPT": "1",
        }
        gh = FakeGh(tags={TAG: SHA})
        argv = [
            "draft",
            "--sha",
            SHA,
            "--tag",
            TAG,
            "--run-id",
            RUN_ID,
            "--rehearsal",
            "false",
            "--assets",
            str(self.assets_dir()),
            "--manifest",
            str(self.manifest_path()),
        ]
        with mock.patch("sys.stdout") as fake_stdout:
            code = gate.main(argv, env=env, repo_root=self.repo_root, gh_factory=lambda ctx, e: gh)
        self.assertEqual(code, 0)
        self.assertTrue(gh.releases_by_tag[TAG]["draft"])
        self.assertTrue(self.manifest_path().is_file())
        self.assertEqual(json.loads(fake_stdout.write.call_args_list[0].args[0])["tag"], TAG)

    def test_main_draft_missing_asset_returns_one(self) -> None:
        env = {"GITHUB_REF": f"refs/tags/{TAG}", "GITHUB_REPOSITORY": REPO, "GH_TOKEN": "tok"}
        gh = FakeGh(tags={TAG: SHA})
        empty = self.tmp / "empty-assets"
        empty.mkdir()
        argv = [
            "draft",
            "--sha",
            SHA,
            "--tag",
            TAG,
            "--run-id",
            RUN_ID,
            "--rehearsal",
            "false",
            "--assets",
            str(empty),
            "--manifest",
            str(self.manifest_path()),
        ]
        with mock.patch("sys.stderr"):
            code = gate.main(argv, env=env, repo_root=self.repo_root, gh_factory=lambda ctx, e: gh)
        self.assertEqual(code, 1)
        self.assertEqual(gh.requests, [])

    def test_main_publish_uses_injected_gh_factory(self) -> None:
        env = {"GITHUB_REF": f"refs/tags/{TAG}", "GITHUB_REPOSITORY": REPO, "GH_TOKEN": "tok"}
        gh = FakeGh(tags={TAG: SHA})
        self.draft(make_ctx(), gh)
        argv = [
            "publish",
            "--sha",
            SHA,
            "--tag",
            TAG,
            "--run-id",
            RUN_ID,
            "--rehearsal",
            "false",
            "--manifest",
            str(self.manifest_path()),
        ]
        with mock.patch("sys.stdout") as fake_stdout:
            code = gate.main(argv, env=env, repo_root=self.repo_root, gh_factory=lambda ctx, e: gh)
        self.assertEqual(code, 0)
        release_id = next(iter(gh.releases))
        self.assertFalse(gh.releases[release_id]["draft"])
        self.assertEqual(json.loads(fake_stdout.write.call_args_list[0].args[0])["tag"], TAG)

    def test_default_gh_factory_requires_repo(self) -> None:
        with self.assertRaises(gate.GateError):
            gate._default_gh_factory(make_ctx(repo=""), {"GH_TOKEN": "tok"})

    def test_build_parser_has_three_commands(self) -> None:
        parser = gate.build_parser()
        with self.assertRaises(SystemExit):
            parser.parse_args([])
        parsed = parser.parse_args(
            [
                "publish",
                "--sha",
                SHA,
                "--tag",
                TAG,
                "--run-id",
                RUN_ID,
                "--rehearsal",
                "true",
                "--manifest",
                "m.json",
            ]
        )
        self.assertEqual(parsed.command, "publish")
        self.assertEqual(parsed.rehearsal, "true")


if __name__ == "__main__":
    unittest.main(verbosity=2)
