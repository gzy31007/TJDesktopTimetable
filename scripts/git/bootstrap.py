#!/usr/bin/env python3
"""#40 首次迁移：经人工审计工作流后，仅创建 release/1.6 和 stable。"""
import argparse
import json
from pathlib import Path
import re
import subprocess
import sys

ORIGIN = "https://github.com/gzy31007/TJDesktopTimetable.git"


def git(repo, *args, input=None):
  result = subprocess.run(["git", "-C", str(repo), *args], input=input,
              text=True, capture_output=True)
  if result.returncode:
    raise ValueError(f"git {args[0]} failed: {result.stderr.strip()}")
  return result.stdout.strip()


def remote_refs(repo):
  return {ref: sha for sha, ref in
      (line.split("\t") for line in git(repo, "ls-remote", "origin").splitlines())}


def initialize(args):
  repo = Path(args.repo).resolve()
  targets = {"release/1.6": args.main_sha, "stable": args.published_sha}
  for options in (("--all",), ("--push", "--all")):
    urls = git(repo, "remote", "get-url", *options, "origin").splitlines()
    if urls != [args.expected_origin]:
      raise ValueError("origin fetch/push URL differs from the explicitly audited origin")
  remote = remote_refs(repo)
  for sha in targets.values():
    if not re.fullmatch(r"[0-9a-f]{40}", sha):
      raise ValueError("Use full immutable commit SHAs")
  if (git(repo, "rev-parse", "refs/heads/main^{commit}") != args.main_sha
      or remote.get("refs/heads/main") != args.main_sha):
    raise ValueError("main baseline changed; review before initializing")
  published = remote.get("refs/tags/v1.6.1^{}", remote.get("refs/tags/v1.6.1"))
  if (git(repo, "rev-parse", "refs/tags/v1.6.1^{commit}") != args.published_sha
      or published != args.published_sha):
    raise ValueError("v1.6.1 release commit differs; verify the formal Release and peeled tag")
  for sha, tree in ((args.main_sha, args.main_workflows_tree),
           (args.published_sha, args.published_workflows_tree)):
    if (not re.fullmatch(r"[0-9a-f]{40}", tree)
        or git(repo, "rev-parse", f"{sha}:.github/workflows") != tree):
      raise ValueError("Committed workflow tree differs from the manual non-publication audit")
  local = {}
  for branch, sha in targets.items():
    ref = f"refs/heads/{branch}"
    local[branch] = git(repo, "for-each-ref", "--format=%(objectname)", ref)
    if local[branch] and local[branch] != sha:
      raise ValueError(f"Local {branch} exists at another commit; stop without overwriting")
    if ref in remote and remote[ref] != sha:
      raise ValueError(f"Remote {branch} exists at another commit; stop without overwriting")
  result = {"mode": "apply" if args.apply else "plan", "origin": args.expected_origin,
       "sources": {"main": args.main_sha, "v1.6.1^{}": args.published_sha},
       "targets": targets,
       "publication": "not requested; audited branch initialization only"}
  if args.apply:
    commands = ["start"]
    for branch, sha in targets.items():
      ref = f"refs/heads/{branch}"
      if local[branch]:
        commands.append(f"verify {ref} {sha}")
      else:
        commands.append(f"create {ref} {sha}")
    commands.extend(["prepare", "commit", ""])
    git(repo, "update-ref", "--stdin", input="\n".join(commands))
    if args.push:
      missing = [branch for branch in targets if f"refs/heads/{branch}" not in remote]
      if missing:
        # 空 lease 要求远端 ref 尚不存在。即便出现并发创建，也不能快进覆盖。
        git(repo, "-c", "push.followTags=false", "push", "--atomic", "--no-follow-tags",
          *(f"--force-with-lease=refs/heads/{branch}:" for branch in missing),
          "origin", *(f"{targets[branch]}:refs/heads/{branch}" for branch in missing))
      observed = remote_refs(repo)
      result["remote"] = {branch: observed.get(f"refs/heads/{branch}") for branch in targets}
      if result["remote"] != targets:
        raise ValueError("Remote readback differs; stop and inspect, do not roll back")
    result["local"] = {branch: git(repo, "rev-parse", f"refs/heads/{branch}") for branch in targets}
  return result


def main():
  parser = argparse.ArgumentParser(description=__doc__)
  parser.add_argument("--repo", default=".")
  parser.add_argument("--expected-origin", default=ORIGIN,
            help="Exact audited fetch AND push URL; local bare URL is for fixtures only")
  parser.add_argument("--main-sha", required=True)
  parser.add_argument("--published-sha", required=True,
            help="Commit of the independently verified formal GitHub Release v1.6.1")
  parser.add_argument("--main-workflows-tree", required=True,
            help="Git workflow tree manually audited to not publish on branch creation")
  parser.add_argument("--published-workflows-tree", required=True,
            help="Same audit for the published baseline, not the dirty working tree")
  parser.add_argument("--apply", action="store_true")
  parser.add_argument("--push", action="store_true")
  args = parser.parse_args()
  try:
    if args.push and not args.apply:
      raise ValueError("--push requires --apply")
    print(json.dumps(initialize(args), ensure_ascii=False, indent=2))
  except ValueError as error:
    print(str(error), file=sys.stderr)
    return 1
  return 0


if __name__ == "__main__":
  sys.exit(main())
