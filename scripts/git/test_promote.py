"""稳定线晋级的公开 CLI 合约。只用最小临时合成 Git 仓库，不访问 GitHub。"""
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest

SCRIPT = Path(__file__).with_name("promote.py")


class PromoteTests(unittest.TestCase):
  def setUp(self):
    self.temp = tempfile.TemporaryDirectory()
    self.addCleanup(self.temp.cleanup)
    root = Path(self.temp.name)
    self.repo = root / "source"
    self.remote = root / "origin.git"
    self.approval_file = root / "approval.json"
    self.repo.mkdir()
    self.env = {**os.environ, "GIT_CONFIG_NOSYSTEM": "1", "GIT_CONFIG_GLOBAL": os.devnull}
    self.git("init", "-b", "main")
    self.git("config", "user.name", "Fixture")
    self.git("config", "user.email", "fixture@example.invalid")
    self.write_props("1.6.2")
    self.notes = self.repo / "docs/releases/v1.6.2.md"
    self.notes.parent.mkdir(parents=True)
    self.notes.write_text("# 1.6.2\n\n- 合成候选说明\n", encoding="utf-8")
    (self.repo / "docs/releases/empty.md").write_text("", encoding="utf-8")
    (self.repo / "docs/releases/notes-link.md").symlink_to("v1.6.2.md")
    (self.repo / "tracked").write_text("published\n", encoding="utf-8")
    self.git("add", ".")
    self.git("commit", "-m", "Published 1.6.1 fixture")
    self.base = self.git("rev-parse", "HEAD")
    self.git("tag", "-a", "v1.6.1", "-m", "Published 1.6.1")
    self.git("checkout", "-b", "release/1.6")
    (self.repo / "tracked").write_text("development\n", encoding="utf-8")
    (self.repo / "dev-only").write_text("dev\n", encoding="utf-8")
    self.git("add", ".")
    self.git("commit", "-m", "Develop 1.6.2 fixture")
    self.source = self.git("rev-parse", "HEAD")
    self.tree = self.git("rev-parse", "HEAD^{tree}")
    subprocess.run(["git", "init", "--bare", str(self.remote)], check=True,
                   capture_output=True, env=self.env)
    self.git("remote", "add", "origin", str(self.remote))
    self.git("push", "origin", "main", "release/1.6", "refs/tags/v1.6.1")
    self.git("branch", "stable", self.base)
    self.git("push", "origin", "stable")

  def git(self, *args, check=True):
    result = subprocess.run(["git", "-C", str(self.repo), *args], env=self.env,
                            capture_output=True, text=True)
    if check and result.returncode:
      raise AssertionError(result.stderr)
    return result.stdout.strip()

  def write_props(self, version):
    props = self.repo / "dotnet/Directory.Build.props"
    props.parent.mkdir(parents=True, exist_ok=True)
    props.write_text(
        "<Project>\n  <PropertyGroup>\n    <Version>" + version +
        "</Version>\n  </PropertyGroup>\n</Project>\n", encoding="utf-8")

  def approval(self, **overrides):
    data = {
        "schema": 1,
        "origin": str(self.remote),
        "release_ref": "refs/heads/release/1.6",
        "source_sha": self.source,
        "stable_base_sha": self.base,
        "version": "1.6.2",
        "release_notes_path": "docs/releases/v1.6.2.md",
        "evidence": ["https://github.com/gzy31007/TJDesktopTimetable/issues/41"],
        "approved_by": "Maintainer",
        "approved_by_email": "maintainer@example.invalid",
        "approved_at": "2026-10-26T14:00:00+08:00",
        "approval_reference": "issue41-plan",
    }
    data.update(overrides)
    self.approval_file.write_text(json.dumps(data, ensure_ascii=False), encoding="utf-8")
    return data

  def run_cli(self, *args, success=True):
    command = [sys.executable, str(SCRIPT), "--repo", str(self.repo),
               "--expected-origin", str(self.remote), *args]
    result = subprocess.run(command, capture_output=True, text=True, env=self.env)
    self.assertEqual(result.returncode == 0, success, result.stdout + result.stderr)
    return json.loads(result.stdout) if success else result

  def remote_refs(self):
    return self.git("ls-remote", "origin")

  def remote_map(self):
    return {line.split("\t")[1]: line.split("\t")[0]
            for line in self.remote_refs().splitlines()}

  def install_git_shim(self, marker, nth, action):
    """PATH 前置一个 git 包装：第 nth 次匹配 marker 时先执行 action，再 exec 真 git。

    用来确定性地模拟「初始校验之后、本地写入之前」出现的并发写入，只通过 CLI 观察结果。
    """
    shim = Path(self.temp.name) / "shim"
    shim.mkdir(exist_ok=True)
    real = shutil.which("git")
    state = shim / ("count-" + marker)
    (shim / "git").write_text(
        "#!/usr/bin/env python3\n"
        "import os\n"
        "import subprocess\n"
        "import sys\n"
        f"real = {real!r}\n"
        f"state = {str(state)!r}\n"
        f"action = {list(action)!r}\n"
        "args = sys.argv[1:]\n"
        f"if {marker!r} in args and not os.environ.get('SHIM_GUARD'):\n"
        "  count = int(open(state).read()) if os.path.exists(state) else 0\n"
        "  count += 1\n"
        "  open(state, 'w').write(str(count))\n"
        f"  if count == {nth}:\n"
        "    action[0] = real\n"
        "    subprocess.run(action, check=True, env={**os.environ, 'SHIM_GUARD': '1'},\n"
        f"                   cwd={str(self.repo)!r})\n"
        "os.execv(real, [real] + args)\n", encoding="utf-8")
    (shim / "git").chmod(0o755)
    self.env["PATH"] = str(shim) + os.pathsep + self.env["PATH"]
    self.addCleanup(self.env.pop, "PATH", None)

  def object_exists(self, sha):
    result = subprocess.run(["git", "-C", str(self.repo), "cat-file", "-e", sha],
                            env=self.env, capture_output=True)
    return result.returncode == 0

  # ---- 切片 A：计划模式 ----

  def test_plan_reports_deterministic_candidate_without_any_write(self):
    self.approval()
    head = self.git("branch", "--show-current")
    index = (self.repo / ".git/index").read_bytes()
    local = self.git("for-each-ref")
    remote = self.remote_refs()

    plan = self.run_cli("--approval", str(self.approval_file))

    self.assertEqual(plan["mode"], "plan")
    self.assertEqual(plan["version"], "1.6.2")
    self.assertEqual(plan["approval"]["approval_reference"], "issue41-plan")
    self.assertEqual(plan["approval"]["approved_by"], "Maintainer")
    self.assertEqual(plan["release"],
                     {"ref": "refs/heads/release/1.6", "source_sha": self.source,
                      "tree": self.tree})
    self.assertEqual(plan["release_notes"],
                     {"path": "docs/releases/v1.6.2.md",
                      "size": len(self.notes.read_bytes())})
    target = plan["stable"]["target_sha"]
    self.assertRegex(target, r"^[0-9a-f]{40}$")
    self.assertNotEqual(target, self.source)
    self.assertNotEqual(target, self.base)
    self.assertEqual(plan["stable"]["base_sha"], self.base)
    self.assertEqual(plan["local"], {"stable": self.base})
    self.assertEqual(plan["remote"], {"stable": self.base})
    self.assertFalse(plan["actions"]["wired"])
    self.assertIn("stable", plan["actions"]["trigger"])

    self.assertEqual(self.git("for-each-ref"), local)
    self.assertEqual(self.remote_refs(), remote)
    self.assertEqual(self.git("branch", "--show-current"), head)
    self.assertEqual((self.repo / ".git/index").read_bytes(), index)
    self.assertFalse(self.object_exists(target))
    self.assertEqual(self.run_cli("--approval", str(self.approval_file))["stable"]["target_sha"],
                     target)


  # ---- 切片 B：本地晋级 ----

  def test_apply_promotes_full_source_tree_as_single_parent_candidate(self):
    self.approval()
    (self.repo / "tracked").write_text("staged\n", encoding="utf-8")
    self.git("add", "tracked")
    (self.repo / "tracked").write_text("unstaged\n", encoding="utf-8")
    (self.repo / "untracked").write_text("keep\n", encoding="utf-8")
    index = (self.repo / ".git/index").read_bytes()
    head = self.git("branch", "--show-current")
    plan = self.run_cli("--approval", str(self.approval_file))
    target = plan["stable"]["target_sha"]

    result = self.run_cli("--approval", str(self.approval_file), "--apply")

    self.assertEqual(result["mode"], "apply")
    self.assertEqual(result["stable"]["target_sha"], target)
    self.assertEqual(result["local"], {"stable": target})
    self.assertEqual(result["remote"], {"stable": self.base})
    self.assertNotIn("event", result)
    self.assertEqual(self.git("rev-parse", "refs/heads/stable"), target)
    self.assertEqual(self.git("rev-parse", target + "^{tree}"), self.tree)
    self.assertEqual(self.git("rev-list", "--parents", "-n", "1", target).split(),
                     [target, self.base])
    self.assertNotEqual(target, self.source)
    self.assertEqual(self.git("show", target + ":dev-only"), "dev")
    self.assertEqual(self.git("show", target + ":tracked"), "development")
    self.assertEqual(self.git("show", target + ":docs/releases/v1.6.2.md"),
                     self.notes.read_text(encoding="utf-8").strip())

    record = json.loads(self.git("log", "-1", "--format=%B", target).splitlines()[-1])
    self.assertEqual(record["schema"], 1)
    self.assertEqual(record["source_sha"], self.source)
    self.assertEqual(record["stable_base_sha"], self.base)
    self.assertEqual(record["tree"], self.tree)
    self.assertEqual(record["version"], "1.6.2")
    self.assertEqual(record["release_ref"], "refs/heads/release/1.6")
    self.assertEqual(record["approval_reference"], "issue41-plan")
    self.assertEqual(record["evidence"], ["https://github.com/gzy31007/TJDesktopTimetable/issues/41"])
    self.assertNotIn("target_sha", record)

    self.assertEqual(self.git("branch", "--show-current"), head)
    self.assertEqual((self.repo / ".git/index").read_bytes(), index)
    self.assertEqual((self.repo / "tracked").read_text(encoding="utf-8"), "unstaged\n")
    self.assertEqual(self.git("show", ":tracked"), "staged")
    self.assertEqual((self.repo / "untracked").read_text(encoding="utf-8"), "keep\n")
    self.assertIn("refs/tags/v1.6.1", self.remote_refs())

    refs = self.git("for-each-ref")
    again = self.run_cli("--approval", str(self.approval_file), "--apply")
    self.assertEqual(again["stable"]["target_sha"], target)
    self.assertEqual(again["local"], {"stable": target})
    self.assertEqual(self.git("for-each-ref"), refs)
    self.assertEqual((self.repo / ".git/index").read_bytes(), index)


  # ---- 切片 C：推送与失败保留 ----

  def test_push_publishes_exact_target_with_lease_event_and_no_tags(self):
    self.approval()
    tags = [line for line in self.remote_refs().splitlines() if "refs/tags/" in line]
    result = self.run_cli("--approval", str(self.approval_file), "--apply", "--push")
    target = result["stable"]["target_sha"]
    self.assertEqual(result["remote"], {"stable": target})
    self.assertEqual(result["local"], {"stable": target})
    self.assertEqual(result["event"],
                     {"type": "push", "ref": "refs/heads/stable", "sha": target,
                      "previous_sha": self.base, "source_sha": self.source,
                      "version": "1.6.2"})
    self.assertEqual(self.git("rev-parse", "refs/heads/stable"), target)
    remote = {line.split("\t")[1]: line.split("\t")[0]
              for line in self.remote_refs().splitlines()}
    self.assertEqual(remote["refs/heads/stable"], target)
    self.assertEqual([line for line in self.remote_refs().splitlines()
                      if "refs/tags/" in line], tags)
    self.assertEqual(self.git("rev-list", "--parents", "-n", "1", target).split(),
                     [target, self.base])
    before = self.remote_refs()
    again = self.run_cli("--approval", str(self.approval_file), "--apply", "--push")
    self.assertEqual(again["stable"]["target_sha"], target)
    self.assertNotIn("event", again)
    self.assertEqual(self.remote_refs(), before)

  def test_push_requires_apply(self):
    self.approval()
    error = self.run_cli("--approval", str(self.approval_file), "--push", success=False)
    self.assertIn("apply", error.stderr)
    self.assertEqual(self.git("for-each-ref", "--format=%(objectname)", "refs/heads/stable"),
                     self.base)

  def test_rejected_push_retains_local_success_and_same_approval_recovers(self):
    self.approval()
    hook = Path(self.remote) / "hooks/pre-receive"
    hook.write_text("#!/bin/sh\necho fixture rejection >&2\nexit 1\n", encoding="utf-8")
    hook.chmod(0o755)
    error = self.run_cli("--approval", str(self.approval_file), "--apply", "--push",
                         success=False)
    self.assertIn("push", error.stderr.lower())
    target = self.git("rev-parse", "refs/heads/stable")
    self.assertNotEqual(target, self.base)
    self.assertIn("re-run the same approval", error.stderr)
    remote = {line.split("\t")[1]: line.split("\t")[0]
              for line in self.remote_refs().splitlines()}
    self.assertEqual(remote["refs/heads/stable"], self.base)

    hook.unlink()
    result = self.run_cli("--approval", str(self.approval_file), "--apply", "--push")
    self.assertEqual(result["stable"]["target_sha"], target)
    self.assertEqual(result["local"], {"stable": target})
    self.assertEqual(result["event"]["sha"], target)
    remote = {line.split("\t")[1]: line.split("\t")[0]
              for line in self.remote_refs().splitlines()}
    self.assertEqual(remote["refs/heads/stable"], target)

  def test_concurrent_remote_advance_is_not_rewritten_and_local_state_is_retained(self):
    self.approval()
    competitor = self.git("commit-tree", self.tree, "-p", self.base, "-m", "competing promotion")
    hook = Path(self.repo) / ".git/hooks/pre-push"
    hook.write_text(
        "#!/bin/sh\n"
        "if [ -z \"$PROMOTE_FIXTURE_GUARD\" ]; then\n"
        "  PROMOTE_FIXTURE_GUARD=1 git -C \"" + str(self.repo) + "\" push "
        "--no-follow-tags origin " + competitor + ":refs/heads/stable || exit 1\n"
        "fi\nexit 0\n", encoding="utf-8")
    hook.chmod(0o755)
    error = self.run_cli("--approval", str(self.approval_file), "--apply", "--push",
                         success=False)
    self.assertIn("push", error.stderr.lower())
    self.assertIn("do not roll back", error.stderr)
    self.assertIn("re-run the same approval", error.stderr)
    hook.unlink()
    remote = {line.split("\t")[1]: line.split("\t")[0]
              for line in self.remote_refs().splitlines()}
    self.assertEqual(remote["refs/heads/stable"], competitor)
    target = self.git("rev-parse", "refs/heads/stable")
    self.assertNotEqual(target, competitor)
    self.assertEqual(self.git("rev-list", "--parents", "-n", "1", target).split(),
                     [target, self.base])
    retry = self.run_cli("--approval", str(self.approval_file), "--apply", "--push",
                         success=False)
    self.assertIn("Remote stable", retry.stderr)
    remote = {line.split("\t")[1]: line.split("\t")[0]
              for line in self.remote_refs().splitlines()}
    self.assertEqual(remote["refs/heads/stable"], competitor)


  # ---- 切片 D：拒绝路径 ----

  def test_unapproved_or_floating_approval_is_rejected_before_writes(self):
    cases = {
        "missing keys": ({}, "approval"),
        "unknown key": ({"unexpected": 1}, "unknown keys"),
        "schema": ({"schema": 2}, "schema"),
        "origin": ({"origin": "https://example.invalid/repo.git"}, "origin"),
        "shorthand release ref": ({"release_ref": "release/1.6"}, "release_ref"),
        "wrong version line": ({"release_ref": "refs/heads/release/1.7"}, "major.minor"),
        "version not semver": ({"version": "1.6"}, "version"),
        "short source sha": ({"source_sha": self.source[:12]}, "source_sha"),
        "floating source ref": ({"source_sha": "release/1.6"}, "source_sha"),
        "short base sha": ({"stable_base_sha": self.base[:8]}, "stable_base_sha"),
        "empty evidence": ({"evidence": []}, "evidence"),
        "empty evidence item": ({"evidence": [""]}, "evidence"),
        "blank approver": ({"approved_by": " "}, "approved_by"),
        "approver email": ({"approved_by_email": "maintainer"}, "approved_by_email"),
        "naive timestamp": ({"approved_at": "2026-10-26T14:00:00"}, "approved_at"),
        "blank reference": ({"approval_reference": ""}, "approval_reference"),
        "absolute notes path": ({"release_notes_path": "/etc/passwd"}, "release_notes_path"),
        "traversal notes path": ({"release_notes_path": "../notes.md"}, "release_notes_path"),
    }
    local = self.git("for-each-ref")
    remote = self.remote_refs()
    for name, (overrides, token) in cases.items():
      with self.subTest(case=name):
        if overrides:
          self.approval(**overrides)
        else:
          self.approval_file.write_text("{}", encoding="utf-8")
        error = self.run_cli("--approval", str(self.approval_file), "--apply", "--push",
                             success=False)
        self.assertIn(token, error.stderr)
        self.assertEqual(self.git("for-each-ref"), local)
        self.assertEqual(self.remote_refs(), remote)
    error = self.run_cli(success=False)
    self.assertIn("--approval", error.stderr)

  def test_version_and_release_notes_are_bound_to_the_source_commit(self):
    before = self.git("for-each-ref")
    self.approval(version="1.6.3")
    error = self.run_cli("--approval", str(self.approval_file), "--apply", success=False)
    self.assertIn("Version", error.stderr)
    self.approval(release_notes_path="docs/releases/v9.9.9.md")
    error = self.run_cli("--approval", str(self.approval_file), "--apply", success=False)
    self.assertIn("release notes", error.stderr)
    self.approval(release_notes_path="docs/releases/empty.md")
    error = self.run_cli("--approval", str(self.approval_file), "--apply", success=False)
    self.assertIn("non-empty", error.stderr)
    self.assertEqual(self.git("for-each-ref"), before)
    self.assertEqual(self.git("rev-parse", "refs/heads/stable"), self.base)

  def test_moved_source_or_release_refs_are_rejected_before_writes(self):
    self.approval()
    self.git("checkout", "main")
    self.git("update-ref", "refs/heads/release/1.6", self.base)
    local = self.git("for-each-ref")
    error = self.run_cli("--approval", str(self.approval_file), "--apply", success=False)
    self.assertIn("Local refs/heads/release/1.6", error.stderr)
    self.assertEqual(self.git("for-each-ref"), local)

    self.git("update-ref", "refs/heads/release/1.6", self.source)
    moved = self.git("commit-tree", self.tree, "-p", self.source, "-m", "moved release tip")
    self.git("push", "origin", f"{moved}:refs/heads/release/1.6")
    error = self.run_cli("--approval", str(self.approval_file), "--apply", success=False)
    self.assertIn("Remote refs/heads/release/1.6", error.stderr)
    self.assertEqual(self.git("rev-parse", "refs/heads/stable"), self.base)

  def test_advanced_target_is_rejected_without_rewriting(self):
    self.approval()
    first = self.run_cli("--approval", str(self.approval_file), "--apply", "--push")
    first_target = first["stable"]["target_sha"]
    local = self.git("for-each-ref")
    remote = self.remote_refs()
    self.approval(approval_reference="issue41-second", approved_at="2026-10-26T15:30:00+08:00")
    error = self.run_cli("--approval", str(self.approval_file), "--apply", "--push",
                         success=False)
    self.assertIn("Remote stable", error.stderr)
    self.assertEqual(self.git("for-each-ref"), local)
    self.assertEqual(self.remote_refs(), remote)
    self.assertEqual(self.git("rev-parse", "refs/heads/stable"), first_target)

  def test_locally_advanced_target_is_rejected_without_rewriting(self):
    self.approval()
    self.run_cli("--approval", str(self.approval_file), "--apply")
    local = self.git("for-each-ref")
    self.approval(approval_reference="issue41-second", approved_at="2026-10-26T15:30:00+08:00")
    error = self.run_cli("--approval", str(self.approval_file), "--apply", "--push",
                         success=False)
    self.assertIn("Local stable", error.stderr)
    self.assertEqual(self.git("for-each-ref"), local)
    promoted = self.git("rev-parse", "refs/heads/stable")
    self.assertNotEqual(promoted, self.base)
    remote = {line.split("\t")[1]: line.split("\t")[0]
              for line in self.remote_refs().splitlines()}
    self.assertEqual(remote["refs/heads/stable"], self.base)

  def test_checked_out_stable_blocks_promotion_without_writing(self):
    self.approval()
    self.git("checkout", "stable")
    head = self.git("rev-parse", "HEAD")
    index = (self.repo / ".git/index").read_bytes()
    for flags in ((), ("--apply",), ("--apply", "--push")):
      with self.subTest(flags=flags):
        error = self.run_cli("--approval", str(self.approval_file), *flags, success=False)
        self.assertIn("checked out", error.stderr)
        self.assertEqual(self.git("branch", "--show-current"), "stable")
        self.assertEqual(self.git("rev-parse", "HEAD"), head)
        self.assertEqual((self.repo / ".git/index").read_bytes(), index)
        self.assertEqual(self.git("rev-parse", "refs/heads/stable"), self.base)

  def test_wrong_fetch_or_push_origin_is_rejected_before_writes(self):
    self.approval()
    local = self.git("for-each-ref")
    for kind in ("fetch", "push"):
      with self.subTest(kind=kind):
        self.git("remote", "set-url", *(["--push"] if kind == "push" else []),
                 "origin", str(self.remote) + ".wrong")
        error = self.run_cli("--approval", str(self.approval_file), "--apply", "--push",
                             success=False)
        self.assertIn("origin", error.stderr)
        self.assertEqual(self.git("for-each-ref"), local)
        self.git("remote", "set-url", *(["--push"] if kind == "push" else []),
                 "origin", str(self.remote))
    self.git("remote", "set-url", "--add", "--push", "origin", str(self.remote) + ".second")
    error = self.run_cli("--approval", str(self.approval_file), "--apply", success=False)
    self.assertIn("origin", error.stderr)
    self.assertEqual(self.git("for-each-ref"), local)


  # ---- 切片 E：目标提交自校验 ----

  def test_verify_target_rechecks_record_parent_tree_and_source(self):
    self.approval()
    target = self.run_cli("--approval", str(self.approval_file), "--apply")["stable"]["target_sha"]
    verified = self.run_cli("--verify-target", target)
    self.assertEqual(verified["mode"], "verify")
    self.assertTrue(verified["verified"])
    self.assertEqual(verified["version"], "1.6.2")
    self.assertEqual(verified["release"],
                     {"ref": "refs/heads/release/1.6", "source_sha": self.source,
                      "tree": self.tree})
    self.assertEqual(verified["stable"],
                     {"ref": "refs/heads/stable", "base_sha": self.base,
                      "target_sha": target})
    self.assertEqual(verified["approval"]["approval_reference"], "issue41-plan")

    record = json.loads(self.git("log", "-1", "--format=%B", target).splitlines()[-1])

    def forged(record_data, *parents):
      args = ["commit-tree", self.tree]
      for parent in parents:
        args += ["-p", parent]
      return self.git(*args, "-m", "release: forged\n\n" +
                      json.dumps(record_data, sort_keys=True, separators=(",", ":")))

    lying_tree = forged(dict(record, tree=self.git("rev-parse", self.base + "^{tree}")),
                        self.base)
    error = self.run_cli("--verify-target", lying_tree, success=False)
    self.assertIn("tree", error.stderr)

    wrong_source = forged(dict(record, source_sha=self.base), self.base)
    error = self.run_cli("--verify-target", wrong_source, success=False)
    self.assertIn("source", error.stderr)

    orphan = self.git("commit-tree", self.tree, "-m", "release: forged\n\n" +
                     json.dumps(record, sort_keys=True, separators=(",", ":")))
    error = self.run_cli("--verify-target", orphan, success=False)
    self.assertIn("parent", error.stderr)

    merge = forged(record, self.base, self.source)
    error = self.run_cli("--verify-target", merge, success=False)
    self.assertIn("parent", error.stderr)

    plain = self.git("commit-tree", self.tree, "-p", self.base, "-m", "no record here")
    error = self.run_cli("--verify-target", plain, success=False)
    self.assertIn("record", error.stderr)

    error = self.run_cli("--verify-target", target, "--approval", str(self.approval_file),
                         success=False)
    self.assertIn("standalone", error.stderr)


  # ---- 切片 F：写前并发复核与 update-ref 原子事务 ----

  def test_remote_source_move_after_validation_is_caught_before_local_write(self):
    self.approval()
    moved = self.git("commit-tree", self.tree, "-p", self.source, "-m", "moved release tip")
    self.install_git_shim("ls-remote", 2, ["git", "-C", str(self.repo), "push", "origin",
                                            f"{moved}:refs/heads/release/1.6"])
    error = self.run_cli("--approval", str(self.approval_file), "--apply", success=False)
    self.assertIn("release", error.stderr)
    self.assertIn("moved before promotion", error.stderr)
    self.assertEqual(self.git("rev-parse", "refs/heads/stable"), self.base)
    remote = self.remote_map()
    self.assertEqual(remote["refs/heads/release/1.6"], moved)
    self.assertEqual(remote["refs/heads/stable"], self.base)

  def test_remote_target_move_after_validation_is_caught_before_local_write(self):
    self.approval()
    competitor = self.git("commit-tree", self.tree, "-p", self.base, "-m", "competing promotion")
    self.install_git_shim("ls-remote", 2, ["git", "-C", str(self.repo), "push", "origin",
                                            f"{competitor}:refs/heads/stable"])
    error = self.run_cli("--approval", str(self.approval_file), "--apply", success=False)
    self.assertIn("Remote stable moved before promotion", error.stderr)
    self.assertEqual(self.git("rev-parse", "refs/heads/stable"), self.base)
    self.assertEqual(self.remote_map()["refs/heads/stable"], competitor)

  def test_release_ref_verify_commits_atomically_with_stable_cas(self):
    self.approval()
    moved = self.git("commit-tree", self.tree, "-p", self.source, "-m", "moved locally")
    self.install_git_shim("update-ref", 1, ["git", "-C", str(self.repo), "update-ref",
                                            "refs/heads/release/1.6", moved])
    error = self.run_cli("--approval", str(self.approval_file), "--apply", success=False)
    self.assertIn("release", error.stderr)
    self.assertEqual(self.git("rev-parse", "refs/heads/stable"), self.base)
    self.assertEqual(self.git("rev-parse", "refs/heads/release/1.6"), moved)

    (Path(self.temp.name) / "shim/git").unlink()
    stale = self.run_cli("--approval", str(self.approval_file), "--apply", success=False)
    self.assertIn("Local refs/heads/release/1.6", stale.stderr)
    self.assertEqual(self.git("rev-parse", "refs/heads/stable"), self.base)

    self.git("update-ref", "refs/heads/release/1.6", self.source)
    result = self.run_cli("--approval", str(self.approval_file), "--apply")
    self.assertEqual(result["local"], {"stable": result["stable"]["target_sha"]})
    self.assertEqual(self.git("rev-parse", "refs/heads/stable"),
                     result["stable"]["target_sha"])

  # ---- 切片 G：说明文件必须是普通文件 ----

  def test_release_notes_must_be_a_regular_file_not_a_symlink(self):
    self.approval(release_notes_path="docs/releases/notes-link.md")
    error = self.run_cli("--approval", str(self.approval_file), "--apply", success=False)
    self.assertIn("regular file", error.stderr)
    self.assertEqual(self.git("rev-parse", "refs/heads/stable"), self.base)
    self.approval()
    self.assertEqual(self.run_cli("--approval", str(self.approval_file), "--apply")["mode"],
                     "apply")


if __name__ == "__main__":
  unittest.main()