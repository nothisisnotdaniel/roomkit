#!/usr/bin/env python3
"""Decide whether a CI run needs its test job.

A copy of the factory's factory/tools/ci_changes.py (factory#85); only the backlog paths, this
docstring and the usage line differ.

Usage (as the step `Check what changed` of .github/workflows/ci.yml): python3 .github/scripts/ci_changes.py

Reads from the environment:
- `GITHUB_EVENT_NAME`: `push` or `pull_request`; any other event runs the tests.
- `GITHUB_EVENT_PATH`: the event payload (JSON). A push compares `before..GITHUB_SHA` (the
  payload's `after` when `GITHUB_SHA` is unset); a pull request compares
  `pull_request.base.sha...pull_request.head.sha` (from their merge base, three dots).
- `GITHUB_OUTPUT`: the file the step outputs go to; one line `tests=true` or `tests=false` is
  appended.

The tests are skipped (`tests=false`) only when every changed file is backlog
(`backlog.md`, `backlog/**` at the repo root) or a `CLAUDE.md` whose only changed lines lie inside
its `## Lessons` section (from that line to the next line starting with `# ` or `## `). Renames
count both paths. Any doubt runs the tests: no usable base (push `before` all zeros or missing,
base commit not in the clone, other event, unreadable payload), an empty change list, a SHA that
is not 40 lowercase hex digits, or any git error. The script prints one reason line and always
exits 0, so it never fails the build; the one exception is a `GITHUB_OUTPUT` it cannot write
(exit 1), since a missing output would skip the tests. Git is called with a fixed argument list, never a shell.
"""
import json
import os
import re
import subprocess

SHA = re.compile(r"^[0-9a-f]{40}$")
ZEROS = "0" * 40
LESSONS = "## Lessons"
SKIP_REASON = "only backlog or lessons changed"


class GitError(Exception):
    """A git command failed."""


def git(repo, *args):
    """Run git in repo and return its stdout; raise GitError when it fails."""
    try:
        r = subprocess.run(["git", "-C", str(repo), *args], capture_output=True, text=True,
                           encoding="utf-8", errors="replace")
    except OSError as e:
        raise GitError(str(e))
    if r.returncode != 0:
        raise GitError(r.stderr.strip() or f"git {args[0]} failed")
    return r.stdout


def has_commit(repo, sha):
    """True when sha names a commit in repo."""
    try:
        git(repo, "cat-file", "-e", sha + "^{commit}")
    except GitError:
        return False
    return True


def is_backlog(path):
    """True for backlog.md and anything under backlog/, at the repo root only."""
    return path == "backlog.md" or path.startswith("backlog/")


def is_claude_md(path):
    """True for the root CLAUDE.md and any path ending in /CLAUDE.md."""
    return path == "CLAUDE.md" or path.endswith("/CLAUDE.md")


def without_lessons(text):
    """The file's lines with the `## Lessons` section removed and trailing blank lines trimmed."""
    lines = text.splitlines()
    start = next((i for i, line in enumerate(lines) if line.rstrip() == LESSONS), None)
    if start is not None:
        end = next((i for i in range(start + 1, len(lines)) if re.match(r"#{1,2} ", lines[i])), len(lines))
        lines = lines[:start] + lines[end:]
    while lines and not lines[-1].strip():
        lines.pop()
    return lines


def changed_files(repo, base, head):
    """(status, path) of every file changed from base to head, renames split into delete and add."""
    out = git(repo, "diff", "--name-status", "--no-renames", "-z", base, head, "--")
    parts = out.split("\0")
    if parts and parts[-1] == "":
        parts.pop()
    return [(parts[i][:1], parts[i + 1]) for i in range(0, len(parts) - 1, 2)]


def lessons_only(repo, base, head, path):
    """True when path differs between base and head only inside its Lessons section."""
    old = git(repo, "show", f"{base}:{path}")
    new = git(repo, "show", f"{head}:{path}")
    return without_lessons(old) == without_lessons(new)


def decide(repo, base, head, three_dot=False):
    """(tests, reason): whether the change from base to head needs the test steps."""
    for name, sha in (("base", base), ("head", head)):
        if not isinstance(sha, str) or not SHA.match(sha):
            return True, f"no usable {name} SHA"
    if base == ZEROS:
        return True, "no base to compare with (before is all zeros)"
    if not has_commit(repo, base):
        return True, "base commit is not in the clone"
    try:
        if three_dot:
            base = git(repo, "merge-base", base, head).strip()
        files = changed_files(repo, base, head)
        if not files:
            return True, "the change list is empty"
        for status, path in files:
            if is_backlog(path):
                continue
            if not is_claude_md(path):
                return True, f"{path} changed"
            if status != "M":
                return True, f"{path} was added, deleted or changed type"
            if not lessons_only(repo, base, head, path):
                return True, f"{path} changed outside its Lessons section"
    except GitError as e:
        return True, f"git failed: {e}"
    return False, SKIP_REASON


def range_from_event(name, path, sha):
    """(base, head, three_dot, reason) for the event; reason is set when there is nothing to compare."""
    if name not in ("push", "pull_request"):
        return None, None, False, f"event {name or '(none)'} is neither push nor pull_request"
    try:
        with open(path, encoding="utf-8") as f:
            event = json.load(f)
        if name == "push":
            before = event.get("before")
            if not before or before == ZEROS:
                return None, None, False, "no base to compare with (push before is all zeros or missing)"
            return before, sha or event.get("after"), False, None
        pr = event["pull_request"]
        return pr["base"]["sha"], pr["head"]["sha"], True, None
    except (OSError, TypeError, ValueError, KeyError, AttributeError) as e:
        return None, None, False, f"the event payload is unreadable ({type(e).__name__})"


def main():
    """Write tests=true|false to $GITHUB_OUTPUT and print the reason; always exit 0."""
    base, head, three_dot, reason = range_from_event(
        os.environ.get("GITHUB_EVENT_NAME", ""), os.environ.get("GITHUB_EVENT_PATH", ""),
        os.environ.get("GITHUB_SHA", ""))
    if reason:
        tests = True
    else:
        tests, reason = decide(os.getcwd(), base, head, three_dot)
    line = "tests=" + ("true" if tests else "false")
    output = os.environ.get("GITHUB_OUTPUT")
    if output:
        try:
            with open(output, "a", encoding="utf-8") as f:
                f.write(line + "\n")
        except OSError as e:
            # Without the output the test steps would be skipped, so fail the step instead.
            print(f"cannot write GITHUB_OUTPUT: {e}")
            return 1
    print(f"{line}: {reason}" if tests else f"{line}: skipping the tests, {reason}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
