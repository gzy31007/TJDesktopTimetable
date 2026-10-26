"""#42 公共门禁 CLI：最小合成 Git 候选 + 既有 FakeGh；不访问真实 GitHub。"""
import contextlib
import hashlib
import io
import json
import os
from pathlib import Path
import subprocess
import tempfile
import unittest

import gate
from test_gate import FakeGh, REPO, RUN_ID, VERSION, fake_run, make_repo_root, standard_assets


class CandidateCliTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="candidate-cli-")
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.repo = make_repo_root(self.root)
        self.git("init", "-b", "stable")
        self.git("config", "user.name", "Fixture")
        self.git("config", "user.email", "fixture@example.invalid")
        self.origin = f"https://github.com/{REPO}.git"
        self.git("remote", "add", "origin", self.origin)
        self.git("add", ".")
        self.git("commit", "-m", "Published fixture")
        self.base = self.git("rev-parse", "HEAD")
        (self.repo / "candidate").write_text("source", encoding="utf-8")
        self.git("add", ".")
        self.git("commit", "-m", "Approved source fixture")
        self.source = self.git("rev-parse", "HEAD")
        self.tree = self.git("rev-parse", "HEAD^{tree}")
        self.record = {
            "schema": 1, "origin": self.origin, "release_ref": "refs/heads/release/1.6",
            "source_sha": self.source, "stable_base_sha": self.base,
            "version": VERSION, "release_notes_path": f"docs/releases/v{VERSION}.md",
            "evidence": ["https://github.com/gzy31007/TJDesktopTimetable/issues/41"],
            "approved_by": "Fixture", "approved_by_email": "fixture@example.invalid",
            "approved_at": "2026-10-26T21:00:00+08:00", "approval_reference": "fixture-approval",
            "tree": self.tree,
        }
        self.sha = self.target(self.record)
        self.git("checkout", "--detach", self.sha)
        self.env = {
            "GITHUB_REF": "refs/heads/stable", "GITHUB_EVENT_NAME": "push",
            "GITHUB_SHA": self.sha, "GITHUB_REPOSITORY": REPO,
            "GITHUB_RUN_ID": RUN_ID, "GITHUB_RUN_ATTEMPT": "1",
        }
        self.tag = f"v{VERSION}"
        self.assets = self.root / "downloaded"
        self.manifest = self.root / "manifest.json"
        self.gh = FakeGh(run={**fake_run(sha=self.sha), "head_branch": "stable",
                              "repository": {"full_name": REPO}, "head_repository": {"full_name": REPO}})

    def git(self, *args, input=None):
        result = subprocess.run(["git", "-C", str(self.repo), *args], input=input,
                                text=True, capture_output=True, check=True,
                                env={**os.environ, "GIT_NO_REPLACE_OBJECTS": "1"})
        return result.stdout.strip()

    def target(self, record):
        return self.git("commit-tree", self.tree, "-p", self.base,
                        input="Promoted fixture\n\n" + json.dumps(record) + "\n")

    def cli(self, command="prepare", *, success=True, extra=()):
        args = [command, "--sha", self.sha, "--tag", self.tag, "--run-id", RUN_ID,
                "--rehearsal", getattr(self, "rehearsal", "false"), *extra]
        if command == "draft":
            args += ["--assets", str(self.assets)]
        if command in ("draft", "publish"):
            args += ["--manifest", str(self.manifest)]
        out, err = io.StringIO(), io.StringIO()
        with contextlib.redirect_stdout(out), contextlib.redirect_stderr(err):
            code = gate.main(args, env=self.env, repo_root=self.repo,
                             gh_factory=lambda ctx, env: self.gh)
        self.assertEqual(code, 0 if success else 1, out.getvalue() + err.getvalue())
        return json.loads(out.getvalue()) if success else err.getvalue()

    def build_outputs(self):
        for platform, names in (("windows", standard_assets()[:2]), ("linux", standard_assets()[2:])):
            directory = self.assets / platform
            directory.mkdir(parents=True, exist_ok=True)
            entries = []
            for name in names:
                data = ("fixture-" + name).encode()
                (directory / name).write_bytes(data)
                entries.append({"name": name, "size": len(data), "sha256": hashlib.sha256(data).hexdigest()})
            proof = {"schema": 1, "repo": REPO, "sha": self.sha, "run_id": RUN_ID,
                     "run_attempt": 1, "platform": platform, "assets": entries}
            (directory / "build-proof.json").write_text(json.dumps(proof), encoding="utf-8")

    def test_stable_draft_binds_source_run_and_two_platform_proofs_without_publication(self):
        self.build_outputs()
        result = self.cli("draft")
        self.assertEqual(result["source_sha"], self.source)
        self.assertEqual(result["stable_sha"], self.sha)
        self.assertEqual(result["ref"], "refs/heads/stable")
        self.assertEqual(result["event"], "push")
        self.assertEqual(result["builds"]["windows"]["run_attempt"], 1)
        self.assertEqual(result["repo"], REPO)
        self.assertEqual(result["run_id"], "9001")
        self.assertEqual(len(result["expected"]), 3)
        self.assertTrue(all(asset["asset_id"] > 0 for asset in result["expected"]))
        self.assertEqual(self.gh.tags[self.tag], {"type": "commit", "sha": self.sha})
        self.assertTrue(self.gh.releases[result["release_id"]]["draft"])
        self.assertEqual(self.gh.publish_patches(), [])
        self.assertEqual(json.loads(self.manifest.read_text()), result)

    def test_public_cli_cannot_publish_even_with_old_tag_trigger_and_valid_manifest(self):
        self.build_outputs()
        result = self.cli("draft")
        self.manifest.write_text(json.dumps({k: result[k] for k in gate.MANIFEST_KEYS}))
        self.env["GITHUB_REF"] = "refs/tags/" + self.tag
        error = self.cli("publish", success=False)
        self.assertIn("#43", error)
        self.assertEqual(self.gh.publish_patches(), [])

    def test_rehearsal_uses_isolated_source_not_a_claimed_stable_promotion(self):
        self.sha = self.source
        self.git("checkout", "--detach", self.sha)
        self.env.update(GITHUB_SHA=self.sha, GITHUB_REF="refs/heads/release-gate-test/success")
        self.tag = f"gate-test-{RUN_ID}-v{VERSION}"
        self.rehearsal = "true"
        self.gh.run.update(head_sha=self.sha, head_branch="release-gate-test/success")
        self.build_outputs()
        result = self.cli("draft")
        self.assertEqual(result["source_sha"], self.source)
        self.assertIsNone(result["stable_sha"])
        self.assertEqual(result["provenance_kind"], "rehearsal")
        self.assertIsNone(result["release_ref"])
        self.assertEqual(self.gh.publish_patches(), [])

    def test_missing_asset_rehearsal_hits_the_same_real_package_validator(self):
        self.sha = self.source
        self.git("checkout", "--detach", self.sha)
        self.env.update(GITHUB_SHA=self.sha, GITHUB_REF="refs/heads/release-gate-test/missing-asset")
        self.tag, self.rehearsal = f"gate-test-{RUN_ID}-v{VERSION}", "true"
        self.gh.run.update(head_sha=self.sha, head_branch="release-gate-test/missing-asset")
        self.build_outputs()
        linux = self.assets / "linux"
        held = self.root / "withheld-linux"
        self.assertEqual(linux.resolve().parent, self.assets.resolve())
        self.assertEqual(held.resolve().parent, self.root.resolve())
        linux.rename(held)
        error = self.cli("draft", success=False, extra=("--fault", "missing-asset"))
        self.assertIn("windows/linux", error)
        self.assertEqual(self.gh.requests, [])
        self.assertFalse(self.manifest.exists())

    def test_corrupt_build_proof_fails_closed_before_any_github_request(self):
        self.build_outputs()
        (self.assets / "linux" / "build-proof.json").write_text("{")
        self.cli("draft", success=False)
        self.assertEqual(self.gh.requests, [])
        self.assertFalse(self.manifest.exists())

    def test_tag_drift_during_upload_never_produces_verified_manifest(self):
        self.build_outputs()
        self.gh.on_upload = lambda asset: self.gh.tags.update({self.tag: "f" * 40})
        self.cli("draft", success=False)
        self.assertFalse(self.manifest.exists())
        self.assertEqual(self.gh.publish_patches(), [])

    def test_source_record_tampering_is_rejected_before_github(self):
        for field, value in (("tree", "f" * 40), ("source_sha", self.base),
                             ("stable_base_sha", self.source), ("version", "1.6.3"),
                             ("origin", "https://github.com/other/repo.git"),
                             ("release_notes_path", "docs/releases/absent.md")):
            with self.subTest(field=field):
                self.sha = self.target({**self.record, field: value})
                self.git("checkout", "--detach", self.sha)
                self.env["GITHUB_SHA"] = self.sha
                self.cli(success=False)
                self.assertEqual(self.gh.requests, [])

    def test_initialization_without_promotion_record_cannot_enter_stable_path(self):
        self.sha = self.base
        self.env["GITHUB_SHA"] = self.sha
        self.git("checkout", "--detach", self.sha)
        self.cli(success=False)
        self.assertEqual(self.gh.requests, [])

    def test_exact_event_identity_required_even_when_sha_has_valid_record(self):
        for field, value in (("GITHUB_REF", "refs/heads/stable/evil"),
                             ("GITHUB_REF", "refs/tags/" + self.tag),
                             ("GITHUB_REF", "refs/heads/main"),
                             ("GITHUB_EVENT_NAME", "workflow_dispatch"),
                             ("GITHUB_SHA", self.source), ("GITHUB_RUN_ID", "1"),
                             ("GITHUB_RUN_ATTEMPT", ""), ("GITHUB_REPOSITORY", "other/repo")):
            previous = self.env[field]
            with self.subTest(field=field, value=value):
                self.env[field] = value
                self.cli(success=False)
                self.assertEqual(self.gh.requests, [])
            self.env[field] = previous

    def test_dirty_version_or_notes_cannot_replace_fixed_commit_content(self):
        for relative in ("dotnet/Directory.Build.props", self.record["release_notes_path"]):
            path = self.repo / relative
            before = path.read_bytes()
            with self.subTest(path=relative):
                path.write_bytes(before + b"modified")
                self.cli(success=False)
                self.assertEqual(self.gh.requests, [])
            path.write_bytes(before)

    def test_old_build_proof_run_attempt_source_or_repo_is_rejected(self):
        self.build_outputs()
        path = self.assets / "windows" / "build-proof.json"
        before = path.read_text()
        for field, value in (("repo", "other/repo"), ("sha", self.source), ("run_id", "8999"),
                             ("run_attempt", 2), ("run_attempt", True), ("platform", "linux")):
            with self.subTest(field=field):
                proof = json.loads(before)
                proof[field] = value
                path.write_text(json.dumps(proof))
                self.cli("draft", success=False)
                self.assertEqual(self.gh.requests, [])
        path.write_text(before)

    def test_asset_tamper_missing_extra_empty_or_symlink_rejected_before_api(self):
        self.build_outputs()
        path = self.assets / "windows" / standard_assets()[0]
        before = path.read_bytes()
        for fault in ("tamper", "missing", "extra", "empty", "symlink"):
            with self.subTest(fault=fault):
                if fault == "tamper":
                    path.write_bytes(b"wrong")
                elif fault == "missing":
                    path.unlink()
                elif fault == "extra":
                    (self.assets / "windows" / "unexpected").write_bytes(b"extra")
                elif fault == "empty":
                    path.write_bytes(b"")
                else:
                    path.unlink()
                    path.symlink_to(self.repo / "candidate")
                self.cli("draft", success=False)
                self.assertEqual(self.gh.requests, [])
                if path.is_symlink():
                    path.unlink()
                path.write_bytes(before)
                extra = self.assets / "windows" / "unexpected"
                if extra.exists():
                    extra.unlink()

    def test_same_run_repository_branch_id_sha_attempt_are_required(self):
        self.build_outputs()
        for field, value in (("id", 8999), ("head_branch", "v1.6.2"),
                             ("head_sha", self.source), ("run_attempt", 2),
                             ("repository", {"full_name": "other/repo"}),
                             ("head_repository", {"full_name": "other/repo"})):
            previous = self.gh.run[field]
            with self.subTest(field=field):
                self.gh.run[field] = value
                self.cli("draft", success=False)
                self.assertEqual(self.gh.release_posts(), [])
                self.assertEqual(self.gh.tags, {})
            self.gh.run[field] = previous

    def test_each_platform_failure_cancellation_skip_or_unknown_blocks_tag_and_draft(self):
        self.build_outputs()
        for name in ("windows / build", "linux / build", "ci / test (ubuntu-latest)"):
            job = next(job for job in self.gh.jobs if job["name"] == name)
            for conclusion in ("failure", "cancelled", "skipped", None, "neutral"):
                with self.subTest(job=name, conclusion=conclusion):
                    job["conclusion"] = conclusion
                    self.cli("draft", success=False)
                    self.assertEqual(self.gh.tags, {})
                    self.assertEqual(self.gh.release_posts(), [])
            job["conclusion"] = "success"

    def test_existing_draft_is_not_taken_over_and_does_not_create_an_orphan_tag(self):
        self.build_outputs()
        self.cli("draft")
        count, uploaded = len(self.gh.releases), len(self.gh.uploads)
        self.gh.tags.clear()
        self.manifest.unlink()
        self.cli("draft", success=False)
        self.assertEqual(len(self.gh.releases), count)
        self.assertEqual(len(self.gh.uploads), uploaded)
        self.assertEqual(self.gh.tags, {})
        self.assertFalse(self.manifest.exists())

    def test_missing_server_digest_requires_download_verification(self):
        self.build_outputs()
        def remove_digests(release):
            for asset in self.gh.assets[release["id"]]:
                asset.pop("digest", None)
        self.gh.on_upload = remove_digests
        self.cli("draft")
        self.assertEqual(len(self.gh.download_calls), 3)

    def test_release_notes_changed_during_upload_never_become_trusted_snapshot(self):
        self.build_outputs()
        self.gh.on_upload = lambda release: release.update(body="wrong notes")
        self.cli("draft", success=False)
        self.assertFalse(self.manifest.exists())

    def test_fetch_source_rejects_unapproved_origin_before_network(self):
        self.git("config", "remote.origin.url", "https://github.com/other/repo.git")
        self.cli(success=False, extra=("--fetch-source",))
        self.assertEqual(self.gh.requests, [])

    def test_checkout_https_origin_without_dot_git_keeps_canonical_record_identity(self):
        # actions/checkout 的 HTTPS origin 没有 .git；晋级记录仍是经批准的 canonical URL。
        self.git("remote", "set-url", "origin", self.origin.removesuffix(".git"))
        result = self.cli()
        self.assertEqual(result["source_sha"], self.source)
        self.assertEqual(self.gh.requests, [])

    def test_extra_push_url_is_rejected_before_source_fetch(self):
        self.git("config", "--add", "remote.origin.pushurl", self.origin)
        self.git("config", "--add", "remote.origin.pushurl", "https://github.com/other/repo.git")
        self.cli(success=False)
        self.assertEqual(self.gh.requests, [])

    def test_prepare_accepts_verified_stable_candidate_without_preexisting_tag(self):
        result = self.cli()
        self.assertEqual(result["sha"], self.sha)
        self.assertEqual(result["source_sha"], self.source)
        self.assertEqual(result["version"], "1.6.2")
        self.assertEqual(result["tag"], "v1.6.2")
        self.assertEqual(self.gh.requests, [])


if __name__ == "__main__":
    unittest.main()
