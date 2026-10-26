"""首次迁移的公开 CLI 合约。只创建最小合成 Git 仓库，不调用 GitHub。"""
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest

SCRIPT = Path(__file__).with_name("bootstrap.py")


class BootstrapTests(unittest.TestCase):
  def setUp(self):
    self.temp = tempfile.TemporaryDirectory()
    self.addCleanup(self.temp.cleanup)
    root = Path(self.temp.name)
    self.repo = root / "source"
    self.remote = root / "origin.git"
    self.repo.mkdir()
    self.env = {**os.environ, "GIT_CONFIG_NOSYSTEM": "1", "GIT_CONFIG_GLOBAL": os.devnull}
    self.git("init", "-b", "main")
    self.git("config", "user.name", "Fixture")
    self.git("config", "user.email", "fixture@example.invalid")
    workflow = self.repo / ".github/workflows/release.yml"
    workflow.parent.mkdir(parents=True)
    workflow.write_text("on:\n  push:\n    tags: ['v*']\njobs: {}\n")
    (self.repo / "tracked").write_text("published\n")
    self.git("add", ".")
    self.git("commit", "-m", "Published fixture")
    self.published = self.git("rev-parse", "HEAD")
    self.git("tag", "-a", "v1.6.1", "-m", "Published")
    (self.repo / "tracked").write_text("development\n")
    self.git("commit", "-am", "Development fixture")
    self.main = self.git("rev-parse", "HEAD")
    self.tree = self.git("rev-parse", "HEAD:.github/workflows")
    subprocess.run(["git", "init", "--bare", str(self.remote)], check=True, capture_output=True, env=self.env)
    self.git("remote", "add", "origin", str(self.remote))
    self.git("push", "origin", "main", "refs/tags/v1.6.1")

  def git(self, *args):
    return subprocess.check_output(["git", "-C", str(self.repo), *args], env=self.env, stderr=subprocess.PIPE).decode().strip()

  def run_cli(self, *args, success=True):
    command = [sys.executable, str(SCRIPT), "--repo", str(self.repo),
         "--expected-origin", str(self.remote), "--main-sha", self.main,
         "--published-sha", self.published,
         "--main-workflows-tree", self.tree, "--published-workflows-tree", self.tree, *args]
    result = subprocess.run(command, capture_output=True, text=True, env=self.env)
    self.assertEqual(result.returncode == 0, success, result.stdout + result.stderr)
    return json.loads(result.stdout) if success else result

  def refs(self):
    return self.git("ls-remote", "origin")

  def test_initialization_preserves_dirty_worktree_and_creates_exact_branch_refs_only(self):
    (self.repo / "tracked").write_text("staged\n")
    self.git("add", "tracked")
    (self.repo / "tracked").write_text("unstaged\n")
    (self.repo / "untracked").write_text("keep\n")
    index = (self.repo / ".git/index").read_bytes()
    before = self.refs()
    plan = self.run_cli()
    self.assertEqual(plan["mode"], "plan")
    self.assertEqual(self.refs(), before)
    result = self.run_cli("--apply", "--push")
    self.assertEqual(result["remote"], {"release/1.6": self.main, "stable": self.published})
    self.assertEqual(self.git("rev-parse", "refs/heads/release/1.6"), self.main)
    self.assertEqual(self.git("rev-parse", "refs/heads/stable"), self.published)
    self.assertEqual(self.git("branch", "--show-current"), "main")
    self.assertEqual((self.repo / ".git/index").read_bytes(), index)
    self.assertEqual((self.repo / "tracked").read_text(), "unstaged\n")
    self.assertEqual((self.repo / "untracked").read_text(), "keep\n")
    self.assertEqual(self.git("show", ":tracked"), "staged")
    tags = [line for line in self.refs().splitlines() if "refs/tags/" in line]
    self.assertEqual(tags, [line for line in before.splitlines() if "refs/tags/" in line])
    self.assertNotEqual(self.git("rev-parse", "v1.6.1"), self.published)
    self.assertEqual(result["publication"], "not requested; audited branch initialization only")
    refs = self.git("for-each-ref")
    self.assertEqual(self.run_cli("--apply", "--push")["remote"], result["remote"])
    self.assertEqual(self.git("for-each-ref"), refs)
    self.assertEqual((self.repo / ".git/index").read_bytes(), index)
    self.assertEqual((self.repo / "tracked").read_text(), "unstaged\n")

  def test_wrong_fetch_or_push_origin_is_rejected_before_any_branch_write(self):
    for kind in ("fetch", "push"):
      with self.subTest(kind=kind):
        before = self.git("for-each-ref")
        self.git("remote", "set-url", *( ["--push"] if kind == "push" else []), "origin", str(self.remote) + ".wrong")
        error = self.run_cli("--apply", "--push", success=False)
        self.assertIn("origin", error.stderr)
        self.assertEqual(self.git("for-each-ref"), before)
        self.git("remote", "set-url", *( ["--push"] if kind == "push" else []), "origin", str(self.remote))

  def test_changed_main_or_release_tag_is_rejected_before_writes(self):
    before = self.refs()
    error = self.run_cli("--main-sha", self.published, "--apply", success=False)
    self.assertIn("main", error.stderr)
    error = self.run_cli("--published-sha", self.main, "--apply", success=False)
    self.assertIn("v1.6.1", error.stderr)
    self.assertEqual(self.refs(), before)
    self.assertEqual(self.git("for-each-ref", "--format=%(refname)", "refs/heads/"), "refs/heads/main")

  def test_existing_conflicting_local_branch_is_preserved_and_nothing_else_is_created(self):
    self.git("branch", "release/1.6", self.published)
    before = self.git("for-each-ref")
    error = self.run_cli("--apply", "--push", success=False)
    self.assertIn("release/1.6", error.stderr)
    self.assertEqual(self.git("for-each-ref"), before)

  def test_existing_remote_conflict_is_preserved_before_local_writes(self):
    self.git("push", "origin", f"{self.published}:refs/heads/release/1.6")
    local = self.git("for-each-ref")
    remote = self.refs()
    error = self.run_cli("--apply", "--push", success=False)
    self.assertIn("Remote release/1.6", error.stderr)
    self.assertEqual(self.git("for-each-ref"), local)
    self.assertEqual(self.refs(), remote)

  def test_matching_remote_refs_are_accepted_without_rewriting(self):
    self.git("push", "origin", f"{self.main}:refs/heads/release/1.6", f"{self.published}:refs/heads/stable")
    remote = self.refs()
    self.assertEqual(self.run_cli("--apply", "--push")["local"],
            {"release/1.6": self.main, "stable": self.published})
    self.assertEqual(self.refs(), remote)

  def test_unreviewed_workflow_tree_is_rejected_before_writes(self):
    before = self.git("for-each-ref")
    error = self.run_cli("--main-workflows-tree", "0" * 40, "--apply", success=False)
    self.assertIn("workflow", error.stderr)
    self.assertEqual(self.git("for-each-ref"), before)


if __name__ == "__main__":
  unittest.main()
