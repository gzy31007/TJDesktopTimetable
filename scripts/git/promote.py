#!/usr/bin/env python3
"""#41 stable 晋级：把固定 release SHA 的完整 tree 变成经人工批准的独立候选提交。

计划模式只读：不写仓库 refs、不动 index / 工作树（目标 SHA 在临时对象目录生成后丢弃）。
--apply 用 update-ref 的 CAS 推进本地 stable；--push 只在同一批准下把该精确目标用 lease
推给 origin。--verify-target 只凭目标提交的消息、parent、tree 与来源对象校验，不采信
调用方手里的旧回执。

边界说明：
- 批准是维护者的声明，不是加密认证；工具只按字段把候选来源、版本与说明绑定起来。
- source、release 与 stable 分散在多个 refs，本工具只在写前、推前、推后分别读回，
  做不到把异地 refs 的并发变化纳入一个跨本地事务的原子判定。
- 推送结果不明（超时、远端拒绝、读回不符）一律保留已完成的本地晋级，不回滚、不强推；
  同一条批准可以重跑，远端已是别的候选时拒绝。
- 本票只产生“受信本地 git 推送”这一事实：推送 event 只写进 stdout 回执供后续接入，
  不使用 GITHUB_TOKEN 链式触发，也不 dispatch Actions；本工具不声称 Actions 已接通。
"""
import argparse
import datetime
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import tempfile
import xml.etree.ElementTree as ET

ORIGIN = "https://github.com/gzy31007/TJDesktopTimetable.git"
STABLE_REF = "refs/heads/stable"
PROPS_PATH = "dotnet/Directory.Build.props"
ZERO_SHA = "0" * 40
SHA_RE = re.compile(r"^[0-9a-f]{40}$")
RELEASE_REF_RE = re.compile(r"^refs/heads/release/(0|[1-9]\d*)\.(0|[1-9]\d*)$")
SEMVER_RE = re.compile(
    r"^(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)"
    r"(?:-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$")

APPROVAL_KEYS = frozenset({
    "schema", "origin", "release_ref", "source_sha", "stable_base_sha", "version",
    "release_notes_path", "evidence", "approved_by", "approved_by_email",
    "approved_at", "approval_reference",
})
ACTIONS = {
    "trigger": "trusted local git push to refs/heads/stable",
    "github_token_chain": False,
    "workflow_dispatch": False,
    "wired": False,
}


class PromoteError(Exception):
  """晋级被拒绝。所有拒绝路径都抛这一个异常，调用方 fail closed。"""


def git(repo, *args, input=None, env=None):
  # 始终禁用 replace refs：source/target 的 tree、父链与消息必须是真实对象，而不是可被 git replace 换掉的内容。
  environment = {**os.environ, **(env or {}), "GIT_NO_REPLACE_OBJECTS": "1"}
  result = subprocess.run(["git", "-C", str(repo), *args], input=input, text=True,
                          capture_output=True, env=environment)
  if result.returncode:
    raise PromoteError(f"git {' '.join(str(arg) for arg in args)} failed: "
                       f"{result.stderr.strip()}")

  return result.stdout.strip()


def read_commit(repo, sha):
  """读原始 commit 对象头，取 tree/parent/message。

  grafts 会改写 rev-list 一类遍历视图，replace refs 还会换掉 cat-file 看到的内容，
  所以父链只能从关闭 replace 后的 cat-file -p 原始头部解析，不能走 rev-list。
  """
  raw = git(repo, "cat-file", "-p", sha)
  headers, _, message = raw.partition("\n\n")
  tree = None
  parents = []
  for line in headers.splitlines():
    key, _, value = line.partition(" ")
    if key == "tree":
      tree = value
    elif key == "parent":
      parents.append(value)
  if tree is None or not SHA_RE.fullmatch(tree):
    raise PromoteError(f"{sha} does not expose a regular commit header")
  return {"tree": tree, "parents": parents, "message": message}


def remote_refs(repo):
  return {ref: sha for sha, ref in
          (line.split("\t") for line in git(repo, "ls-remote", "origin").splitlines())}


def require_sha(value, label):
  if not isinstance(value, str) or not SHA_RE.fullmatch(value):
    raise PromoteError(f"{label} must be a full immutable 40-char lowercase commit SHA, "
                       "not a branch, tag or short SHA")
  return value


def require_text(value, label):
  if not isinstance(value, str) or not value.strip():
    raise PromoteError(f"{label} must be a non-empty string")
  return value


def validate_fields(data, label, extra=frozenset()):
  if not isinstance(data, dict):
    raise PromoteError(f"{label} must be a JSON object")
  unknown = sorted(set(data) - APPROVAL_KEYS - set(extra))
  if unknown:
    raise PromoteError(f"{label} has unknown keys: {unknown}")
  missing = sorted(APPROVAL_KEYS - set(data))
  if missing:
    raise PromoteError(f"{label} missing keys: {missing}")
  if type(data["schema"]) is not int or data["schema"] != 1:
    raise PromoteError(f"{label} schema must be the integer 1")
  if not isinstance(data["origin"], str) or not data["origin"]:
    raise PromoteError(f"{label} origin must be a non-empty string")
  source = require_sha(data["source_sha"], f"{label} source_sha")
  base = require_sha(data["stable_base_sha"], f"{label} stable_base_sha")
  version = data["version"]
  if not isinstance(version, str) or "+" in version or not SEMVER_RE.fullmatch(version):
    raise PromoteError(f"{label} version is not an explicit semver: {version!r}")
  match = RELEASE_REF_RE.fullmatch(str(data["release_ref"]))
  if not match:
    raise PromoteError(f"{label} release_ref must be the full ref "
                       "refs/heads/release/<major.minor>")
  release_line = f"{match.group(1)}.{match.group(2)}"
  if version.split(".")[:2] != release_line.split("."):
    raise PromoteError(f"{label} version major.minor differs from release_ref")
  notes = require_text(data["release_notes_path"], f"{label} release_notes_path")
  if notes.startswith("/") or ".." in Path(notes).parts:
    raise PromoteError(f"{label} release_notes_path must be a repository-relative path")
  evidence = data["evidence"]
  if (not isinstance(evidence, list) or not evidence
      or any(not isinstance(item, str) or not item.strip() for item in evidence)):
    raise PromoteError(f"{label} evidence must be a non-empty list of non-empty references")
  require_text(data["approved_by"], f"{label} approved_by")
  email = require_text(data["approved_by_email"], f"{label} approved_by_email")
  if "@" not in email:
    raise PromoteError(f"{label} approved_by_email must be an email address")
  stamp = require_text(data["approved_at"], f"{label} approved_at")
  try:
    parsed = datetime.datetime.fromisoformat(stamp)
  except ValueError as error:
    raise PromoteError(f"{label} approved_at is not ISO 8601: {stamp!r}") from error
  if parsed.tzinfo is None:
    raise PromoteError(f"{label} approved_at must carry an explicit UTC offset")
  require_text(data["approval_reference"], f"{label} approval_reference")
  return {**data, "release_ref": "refs/heads/" + data["release_ref"].split("refs/heads/")[-1],
          "source_sha": source, "stable_base_sha": base}


def read_approval(path):
  try:
    data = json.loads(Path(path).read_text(encoding="utf-8"))
  except (OSError, UnicodeDecodeError, json.JSONDecodeError) as error:
    raise PromoteError(f"approval JSON unreadable: {error}") from error
  return validate_fields(data, "approval")


def checked_out_refs(repo):
  refs = set()
  for line in git(repo, "worktree", "list", "--porcelain").splitlines():
    if line.startswith("branch "):
      refs.add(line[len("branch "):].strip())
  return refs


def read_version(repo, sha):
  raw = git(repo, "show", f"{sha}:{PROPS_PATH}")
  try:
    root = ET.fromstring(raw)
  except ET.ParseError as error:
    raise PromoteError(f"{PROPS_PATH} is not valid XML: {error}") from error
  found = []
  for element in root.iter():
    tag = element.tag.split("}")[-1] if isinstance(element.tag, str) else ""
    if tag == "Version":
      found.append((element.text or "").strip())
  if len(found) != 1:
    raise PromoteError(f"{PROPS_PATH} must declare exactly one Version, found {len(found)}")
  version = found[0]
  if "+" in version or not SEMVER_RE.fullmatch(version):
    raise PromoteError(f"source Version is not an explicit semver: {version!r}")
  return version


def read_notes(repo, sha, path):
  """只接受普通文件（100644/100755）。cat-file -t 会把 symlink 也报成 blob，必须看 tree mode。"""
  try:
    listing = git(repo, "ls-tree", sha, "--", path)
  except PromoteError as error:
    raise PromoteError(f"release notes are missing from the source commit: {path}") from error
  entries = [line for line in listing.splitlines() if line.strip()]
  if not entries:
    raise PromoteError(f"release notes are missing from the source commit: {path}")
  if len(entries) > 1:
    raise PromoteError(f"release notes path is ambiguous in the source commit: {path}")
  fields = entries[0].split(None, 2)
  mode, kind = fields[0], fields[1]
  if kind != "blob" or mode not in ("100644", "100755"):
    raise PromoteError(f"release notes must be a regular file (mode 100644/100755), "
                       f"got mode {mode}: {path}")
  size = int(git(repo, "cat-file", "-s", f"{sha}:{path}"))
  if size <= 0:
    raise PromoteError(f"release notes must be non-empty: {path}")
  return size


def promotion_record(approval, tree):
  return {
      "schema": 1,
      "origin": approval["origin"],
      "approval_reference": approval["approval_reference"],
      "approved_by": approval["approved_by"],
      "approved_by_email": approval["approved_by_email"],
      "approved_at": approval["approved_at"],
      "evidence": list(approval["evidence"]),
      "release_ref": approval["release_ref"],
      "source_sha": approval["source_sha"],
      "stable_base_sha": approval["stable_base_sha"],
      "version": approval["version"],
      "release_notes_path": approval["release_notes_path"],
      "tree": tree,
  }


def commit_message(record):
  return ("release: 晋级 v" + record["version"] + " 到 stable 候选\n\n"
          + json.dumps(record, ensure_ascii=False, sort_keys=True,
                       separators=(",", ":")) + "\n")


def identity_env(approval):
  identity = {
      "GIT_AUTHOR_NAME": approval["approved_by"],
      "GIT_AUTHOR_EMAIL": approval["approved_by_email"],
      "GIT_AUTHOR_DATE": approval["approved_at"],
      "GIT_COMMITTER_NAME": approval["approved_by"],
      "GIT_COMMITTER_EMAIL": approval["approved_by_email"],
      "GIT_COMMITTER_DATE": approval["approved_at"],
  }
  return {**os.environ, **identity}


def candidate_sha(repo, tree, base, message, approval, write):
  """用 git commit-tree 得到目标提交；计划模式写到临时对象目录，仓库对象零写入。"""
  handle = tempfile.NamedTemporaryFile("w", encoding="utf-8", suffix=".txt", delete=False)
  with handle:
    handle.write(message)
  env = identity_env(approval)
  try:
    if write:
      return git(repo, "commit-tree", tree, "-p", base, "-F", handle.name, env=env)
    objects = tempfile.mkdtemp(prefix="promote-objects-")
    try:
      scratch = {**env, "GIT_OBJECT_DIRECTORY": objects,
                 "GIT_ALTERNATE_OBJECT_DIRECTORIES":
                     git(repo, "rev-parse", "--absolute-git-dir") + "/objects"}
      return git(repo, "commit-tree", tree, "-p", base, "-F", handle.name, env=scratch)
    finally:
      shutil.rmtree(objects, ignore_errors=True)
  finally:
    os.unlink(handle.name)


def audited_origin(repo, expected):
  fetch = git(repo, "remote", "get-url", "--all", "origin").splitlines()
  push = git(repo, "remote", "get-url", "--push", "--all", "origin").splitlines()
  if fetch != [expected]:
    raise PromoteError("origin fetch URL differs from the explicitly audited origin")
  if push != [expected]:
    raise PromoteError("origin push URL differs from the explicitly audited origin")


def parse_record(message):
  lines = [line for line in message.splitlines() if line.strip()]
  if not lines:
    raise PromoteError("target commit has no machine-readable promotion record")
  try:
    data = json.loads(lines[-1])
  except json.JSONDecodeError as error:
    raise PromoteError("target commit message has no parsable promotion record: "
                       f"{error}") from error
  if not isinstance(data, dict) or "tree" not in data:
    raise PromoteError("target commit promotion record is incomplete")
  record = validate_fields(data, "record", extra={"tree"})
  record["tree"] = require_sha(data["tree"], "record tree")
  return record


def verify_target(args):
  """只信目标提交自身的消息、parent、tree 与来源对象，不采信调用方手里的旧回执。"""
  repo = Path(args.repo).resolve()
  target = args.verify_target
  if not SHA_RE.fullmatch(target):
    raise PromoteError("--verify-target must be a full immutable 40-char commit SHA")
  if git(repo, "cat-file", "-t", target) != "commit":
    raise PromoteError("--verify-target is not a commit object in this repository")
  commit = read_commit(repo, target)
  if len(commit["parents"]) != 1:
    raise PromoteError("target commit must have exactly one parent: the approved baseline")
  record = parse_record(commit["message"])
  if record["origin"] != args.expected_origin:
    raise PromoteError("record origin differs from the explicitly audited origin")
  if record["tree"] != commit["tree"]:
    raise PromoteError("target tree differs from the tree recorded in the commit message")
  source = record["source_sha"]
  if git(repo, "cat-file", "-t", source) != "commit":
    raise PromoteError("recorded source_sha is not a commit object in this repository")
  if read_commit(repo, source)["tree"] != record["tree"]:
    raise PromoteError("recorded source tree differs from the target tree; "
                       "the commit does not carry the complete source tree")
  version = read_version(repo, source)
  if version != record["version"]:
    raise PromoteError("recorded version differs from the source Version")
  notes_size = read_notes(repo, source, record["release_notes_path"])
  if record["stable_base_sha"] != commit["parents"][0]:
    raise PromoteError("target parent is not the recorded stable baseline")
  return {
      "mode": "verify",
      "verified": True,
      "origin": args.expected_origin,
      "version": version,
      "approval": {
          "schema": record["schema"],
          "approval_reference": record["approval_reference"],
          "approved_by": record["approved_by"],
          "approved_by_email": record["approved_by_email"],
          "approved_at": record["approved_at"],
          "evidence": list(record["evidence"]),
      },
      "release": {"ref": record["release_ref"], "source_sha": source, "tree": record["tree"]},
      "release_notes": {"path": record["release_notes_path"], "size": notes_size},
      "stable": {"ref": STABLE_REF, "base_sha": record["stable_base_sha"],
                 "target_sha": target},
  }


def promote(args):
  approval = read_approval(args.approval)
  if approval["origin"] != args.expected_origin:
    raise PromoteError("approval origin differs from the explicitly audited origin")
  repo = Path(args.repo).resolve()
  audited_origin(repo, args.expected_origin)
  remote = remote_refs(repo)
  source, base = approval["source_sha"], approval["stable_base_sha"]
  if git(repo, "cat-file", "-t", source) != "commit":
    raise PromoteError("approval source_sha is not a commit object in this repository")
  tree = read_commit(repo, source)["tree"]
  version = read_version(repo, source)
  if version != approval["version"]:
    raise PromoteError(f"source {PROPS_PATH} Version {version} "
                       "differs from the approved version")
  notes_size = read_notes(repo, source, approval["release_notes_path"])
  if git(repo, "rev-parse", approval["release_ref"]) != source:
    raise PromoteError(f"Local {approval['release_ref']} is not the approved source SHA")
  if remote.get(approval["release_ref"]) != source:
    raise PromoteError(f"Remote {approval['release_ref']} is not the approved source SHA")
  if git(repo, "cat-file", "-t", base) != "commit":
    raise PromoteError("approved stable baseline is not a local commit object")
  record = promotion_record(approval, tree)
  message = commit_message(record)
  target = candidate_sha(repo, tree, base, message, approval, write=False)
  local = git(repo, "for-each-ref", "--format=%(objectname)", STABLE_REF) or None
  remote_stable = remote.get(STABLE_REF) or None
  if remote_stable not in (base, target):
    raise PromoteError("Remote stable is neither the approved baseline nor this candidate; "
                       "another promotion may have moved it")
  if local not in (None, base, target):
    raise PromoteError("Local stable is neither the approved baseline nor this candidate; "
                       "review before promoting")
  if local is None and remote_stable != base:
    raise PromoteError("Local stable is missing while the remote already moved; fetch and reconcile")
  if local == base and remote_stable == target:
    raise PromoteError("Remote stable already has this candidate but local stable is behind; "
                       "reconcile before promoting")
  if STABLE_REF in checked_out_refs(repo) and local != target:
    raise PromoteError("local stable is checked out in a worktree; "
                       "refusing to move an active branch")
  mode = "plan"
  if args.apply:
    # 写前再次读回远端：初始校验之后、创建候选/推进本地之前，源或目标变了就停。
    fresh = remote_refs(repo)
    if fresh.get(approval["release_ref"]) != source:
      raise PromoteError(f"Remote {approval['release_ref']} moved before promotion; "
                         "nothing was written")
    fresh_stable = fresh.get(STABLE_REF) or None
    if fresh_stable not in (base, target) or (local != target and fresh_stable != base):
      raise PromoteError("Remote stable moved before promotion; nothing was written")
    # 计划模式刚算出的目标提交与本地已有对象必须同一；创建走 commit-tree，不碰 index / 工作树。
    if local != target:
      created = candidate_sha(repo, tree, base, message, approval, write=True)
      if created != target:
        raise PromoteError("created candidate commit differs from the planned target")
      # 固定 release ref 的 verify 与 stable 的 CAS 在同一个 update-ref 事务里：
      # 任一前提不成立，整个事务不提交，stable 不会被移动半个状态。
      try:
        git(repo, "update-ref", "--stdin", input="\n".join([
            "start",
            f"verify {approval['release_ref']} {source}",
            f"update {STABLE_REF} {target} {local or ZERO_SHA}",
            "prepare",
            "commit",
        ]) + "\n")
      except PromoteError as error:
        raise PromoteError("Local release ref or stable changed during the atomic update; "
                           f"the stable ref was not moved: {error}") from error
      local = git(repo, "for-each-ref", "--format=%(objectname)", STABLE_REF)
      if local != target:
        raise PromoteError("Local stable changed concurrently; stop and inspect")
    elif git(repo, "cat-file", "-t", f"{target}^{{commit}}") != "commit":
      raise PromoteError("Local stable points at a missing object; stop and inspect")
    created = read_commit(repo, target)
    if created["parents"] != [base] or created["tree"] != tree:
      raise PromoteError("Created candidate must carry the source tree with exactly one parent: "
                         "the approved baseline")
    mode = "apply"
  receipt = {
      "mode": mode,
      "origin": args.expected_origin,
      "version": version,
      "approval": {
          "schema": approval["schema"],
          "approval_reference": approval["approval_reference"],
          "approved_by": approval["approved_by"],
          "approved_by_email": approval["approved_by_email"],
          "approved_at": approval["approved_at"],
          "evidence": list(approval["evidence"]),
      },
      "release": {"ref": approval["release_ref"], "source_sha": source, "tree": tree},
      "release_notes": {"path": approval["release_notes_path"], "size": notes_size},
      "stable": {"ref": STABLE_REF, "base_sha": base, "target_sha": target},
      "local": {"stable": local or None},
      "remote": {"stable": remote_stable or None},
      "actions": dict(ACTIONS),
  }
  if args.push:
    publish(repo, approval, base, target, receipt)
  return receipt


def publish(repo, approval, base, target, receipt):
  """推前/推后各读回一次远端；lease 只允许从批准基线快进到本候选。"""
  observed = remote_refs(repo)
  if observed.get(approval["release_ref"]) != approval["source_sha"]:
    raise PromoteError("Remote release ref moved before push; local stable retained at "
                       f"{target}: keep state and inspect, do not roll back")
  remote_stable = observed.get(STABLE_REF) or None
  if remote_stable == target:
    # 另一同名批准已推进到同一目标：no-op，但回执必须反映刚刚读回的远端，而不是初读基线。
    receipt["remote"] = {"stable": target}
    return
  if remote_stable != base:
    raise PromoteError("Remote stable moved before push; local stable retained at "
                       f"{target}: keep state and inspect, do not roll back")
  try:
    git(repo, "-c", "push.followTags=false", "push", "--atomic", "--no-follow-tags",
        f"--force-with-lease={STABLE_REF}:{base}", "origin", f"{target}:{STABLE_REF}")
  except PromoteError as error:
    raise PromoteError(f"{error}; push result unknown, local stable retained at {target}: "
                       "inspect the remote, do not roll back, then re-run the same approval") from error
  after = remote_refs(repo)
  if (after.get(STABLE_REF) != target
      or after.get(approval["release_ref"]) != approval["source_sha"]):
    raise PromoteError("Remote readback after push differs; local stable retained at "
                       f"{target}: keep state and inspect, do not roll back")
  receipt["remote"] = {"stable": target}
  receipt["event"] = {"type": "push", "ref": STABLE_REF, "sha": target,
                      "previous_sha": base, "source_sha": approval["source_sha"],
                      "version": approval["version"]}


def main(argv=None):
  parser = argparse.ArgumentParser(description=__doc__)
  parser.add_argument("--repo", default=".")
  parser.add_argument("--expected-origin", default=ORIGIN,
                      help="Exact audited fetch AND push URL; local bare URL is for fixtures only")
  parser.add_argument("--approval", help="Per-candidate approval JSON file")
  parser.add_argument("--apply", action="store_true",
                      help="Advance local stable with compare-and-swap; default is a read-only plan")
  parser.add_argument("--push", action="store_true",
                      help="Push the exact candidate to origin after --apply, with a lease")
  parser.add_argument("--verify-target", metavar="SHA",
                      help="Read-only: re-check an existing candidate commit from its own record")
  args = parser.parse_args(argv)
  try:
    if args.verify_target:
      if args.apply or args.push or args.approval:
        raise PromoteError("--verify-target is a standalone read-only mode; "
                           "do not combine it with --approval, --apply or --push")
      print(json.dumps(verify_target(args), ensure_ascii=False, indent=2))
      return 0
    if args.push and not args.apply:
      raise PromoteError("--push requires --apply")
    if not args.approval:
      raise PromoteError("--approval FILE is required")
    print(json.dumps(promote(args), ensure_ascii=False, indent=2))
  except PromoteError as error:
    print(str(error), file=sys.stderr)
    return 1
  return 0


if __name__ == "__main__":
  sys.exit(main())
