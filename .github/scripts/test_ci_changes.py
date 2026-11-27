"""Tests for .github/scripts/ci_changes.py, a copy of the factory's factory/tests/test_ci_changes.py with RoomKit's paths.

Run: python3 -m unittest discover -s .github/scripts -p test_ci_changes.py
"""
import git_identity  # noqa: F401
import contextlib
import io
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parent))
import ci_changes  # noqa: E402

CLAUDE_MD = """# RoomKit — conventions

- **Stack:** Python 3.

## Lessons
- First lesson.
- Second lesson.
"""
SKIP = "only backlog or lessons changed"


def run_git(repo, *args):
    """Run git in repo and return its stdout; fail the test when git fails."""
    return subprocess.run(["git", "-C", str(repo), *args], check=True, capture_output=True, text=True).stdout


class Repo:
    """A temporary git repo with a base commit of backlog, CLAUDE.md and code files."""

    def __init__(self, root):
        self.path = Path(root) / "repo"
        self.path.mkdir()
        run_git(self.path, "init", "-q")
        run_git(self.path, "checkout", "-q", "-b", "develop")
        self.write("backlog.md", "- [ ] [#1 X](backlog/001-x.md)\n")
        self.write("backlog/001-x.md", "# X\n")
        self.write("CLAUDE.md", CLAUDE_MD)
        self.write("src/RoomKit/Code.cs", "class Code {}\n")
        self.base = self.commit("base")

    def write(self, rel, text):
        """Write text to rel, creating folders."""
        path = self.path / rel
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(text, encoding="utf-8")

    def read(self, rel):
        """The text of rel."""
        return (self.path / rel).read_text(encoding="utf-8")

    def commit(self, message):
        """Commit everything and return the new commit's SHA."""
        run_git(self.path, "add", "-A")
        run_git(self.path, "commit", "-q", "--allow-empty", "-m", message)
        return run_git(self.path, "rev-parse", "HEAD").strip()

    def decide(self, head):
        """ci_changes.decide from the base commit to head."""
        return ci_changes.decide(self.path, self.base, head)


class DecideTests(unittest.TestCase):
    def setUp(self):
        tmp = tempfile.TemporaryDirectory()
        self.addCleanup(tmp.cleanup)
        self.repo = Repo(tmp.name)

    def lessons(self, old, new):
        """Replace old by new in CLAUDE.md."""
        text = self.repo.read("CLAUDE.md")
        self.assertIn(old, text)
        self.repo.write("CLAUDE.md", text.replace(old, new))

    def test_backlog_only_changes_skip(self):
        """AC-1: changed backlog.md and an added story skip the tests."""
        self.repo.write("backlog.md", "- [x] [#1 X](backlog/001-x.md)\n- [ ] [#2 Y](backlog/002-y.md)\n")
        self.repo.write("backlog/002-y.md", "# Y\n")
        self.assertEqual(self.repo.decide(self.repo.commit("backlog")), (False, SKIP))

    def test_backlog_rename_and_delete_skip(self):
        """AC-1: a renamed and a deleted story skip the tests."""
        self.repo.write("backlog/002-y.md", "# Y\n")
        self.repo.base = self.repo.commit("second story")
        run_git(self.repo.path, "mv", "backlog/001-x.md", "backlog/003-x.md")
        (self.repo.path / "backlog/002-y.md").unlink()
        self.assertEqual(self.repo.decide(self.repo.commit("rename")), (False, SKIP))

    def test_rename_out_of_backlog_runs(self):
        """AC-1: a rename counts both paths, so moving a story out of the backlog runs the tests."""
        run_git(self.repo.path, "mv", "backlog/001-x.md", "notes-x.md")
        tests, reason = self.repo.decide(self.repo.commit("move out"))
        self.assertTrue(tests)
        self.assertIn("notes-x.md", reason)

    def test_factory_backlog_paths_run(self):
        """AC-6: only the root-level backlog counts, so factory/backlog.md and factory/backlog/x.md run the tests."""
        for rel in ("factory/backlog.md", "factory/backlog/x.md"):
            self.repo.write(rel, "- [ ] " + rel + "\n")
            tests, reason = self.repo.decide(self.repo.commit(rel))
            self.assertTrue(tests, rel)
            self.assertIn(rel, reason)
            self.repo.base = self.repo.commit("same")

    def test_lesson_added_skips(self):
        """AC-2: a new lesson line skips the tests."""
        self.lessons("- Second lesson.\n", "- Second lesson.\n- Third lesson.\n")
        self.assertEqual(self.repo.decide(self.repo.commit("lesson")), (False, SKIP))

    def test_lesson_replaced_and_removed_skip(self):
        """AC-2: a replaced and a removed lesson skip the tests."""
        self.lessons("- First lesson.\n- Second lesson.\n", "- Better first lesson.\n")
        self.assertEqual(self.repo.decide(self.repo.commit("lesson")), (False, SKIP))

    def test_lessons_section_appended_skips(self):
        """AC-2: a Lessons section appended to a CLAUDE.md that had none skips the tests."""
        self.repo.write("CLAUDE.md", "# Workspace\n\nText.\n")
        self.repo.base = self.repo.commit("claude without lessons")
        self.repo.write("CLAUDE.md", "# Workspace\n\nText.\n\n## Lessons\n- A lesson.\n")
        self.assertEqual(self.repo.decide(self.repo.commit("lesson")), (False, SKIP))

    def test_lessons_section_in_the_middle(self):
        """AC-2: the section ends at the next `## ` heading; a lesson before it skips, a line after it runs."""
        self.repo.write("CLAUDE.md", CLAUDE_MD + "\n## After\nText.\n")
        self.repo.base = self.repo.commit("after section")
        self.lessons("- Second lesson.\n", "- Second lesson.\n- Third.\n")
        self.assertEqual(self.repo.decide(self.repo.commit("lesson")), (False, SKIP))
        self.repo.base = self.repo.commit("same")
        self.lessons("Text.\n", "Other text.\n")
        self.assertTrue(self.repo.decide(self.repo.commit("after"))[0])

    def test_backlog_and_lessons_skip(self):
        """AC-3: backlog and lessons changes together skip the tests."""
        self.repo.write("backlog.md", "- [x] [#1 X](backlog/001-x.md)\n")
        self.lessons("- First lesson.\n", "- First lesson, reworded.\n")
        self.assertEqual(self.repo.decide(self.repo.commit("both")), (False, SKIP))

    def test_lessons_and_other_line_run(self):
        """AC-4: a lesson plus a line outside the section runs the tests."""
        self.lessons("- First lesson.\n", "- New first.\n")
        self.lessons("Python 3.", "Python 3.9.")
        tests, reason = self.repo.decide(self.repo.commit("mixed"))
        self.assertTrue(tests)
        self.assertIn("CLAUDE.md", reason)

    def test_changed_lessons_heading_runs(self):
        """AC-4: renaming the `## Lessons` heading itself runs the tests."""
        self.lessons("## Lessons\n", "## Lessons learned\n")
        self.assertTrue(self.repo.decide(self.repo.commit("heading"))[0])

    def test_code_file_runs(self):
        """AC-5: a code file alone runs the tests."""
        self.repo.write("src/RoomKit/Code.cs", "class Other {}\n")
        tests, reason = self.repo.decide(self.repo.commit("code"))
        self.assertTrue(tests)
        self.assertIn("src/RoomKit/Code.cs", reason)

    def test_code_file_with_backlog_runs(self):
        """AC-5: a code file alongside backlog and lessons changes runs the tests."""
        self.repo.write("backlog.md", "- [x] [#1 X](backlog/001-x.md)\n")
        self.lessons("- First lesson.\n", "- New first.\n")
        self.repo.write("src/RoomKit/Code.cs", "class Other {}\n")
        self.assertTrue(self.repo.decide(self.repo.commit("code"))[0])

    def test_new_claude_md_runs(self):
        """AC-6: an added CLAUDE.md runs the tests, even with only a Lessons section."""
        self.repo.write("Times/CLAUDE.md", "## Lessons\n- A lesson.\n")
        tests, reason = self.repo.decide(self.repo.commit("new"))
        self.assertTrue(tests)
        self.assertIn("Times/CLAUDE.md", reason)

    def test_deleted_claude_md_runs(self):
        """AC-6: a deleted CLAUDE.md runs the tests."""
        (self.repo.path / "CLAUDE.md").unlink()
        self.assertTrue(self.repo.decide(self.repo.commit("gone"))[0])

    def test_zeros_base_runs(self):
        """AC-7: a base of all zeros runs the tests."""
        head = self.repo.commit("empty")
        tests, reason = ci_changes.decide(self.repo.path, "0" * 40, head)
        self.assertTrue(tests)
        self.assertIn("zeros", reason)

    def test_unknown_base_runs(self):
        """AC-7: a base commit that is not in the clone runs the tests."""
        head = self.repo.commit("empty")
        tests, reason = ci_changes.decide(self.repo.path, "1" * 40, head)
        self.assertTrue(tests)
        self.assertIn("not in the clone", reason)

    def test_bad_sha_runs(self):
        """AC-7: a SHA that is not 40 lowercase hex digits never reaches git and runs the tests."""
        head = self.repo.commit("empty")
        for bad in ("--output=x", "HEAD", self.repo.base.upper(), "", None):
            tests, reason = ci_changes.decide(self.repo.path, bad, head)
            self.assertTrue(tests)
            self.assertIn("base SHA", reason)
        self.assertTrue(ci_changes.decide(self.repo.path, self.repo.base, "--help")[0])

    def test_empty_diff_runs(self):
        """AC-7: an empty change list runs the tests."""
        tests, reason = self.repo.decide(self.repo.commit("empty"))
        self.assertTrue(tests)
        self.assertIn("empty", reason)

    def test_git_error_runs(self):
        """AC-7: a git failure (an unknown head) runs the tests."""
        tests, reason = self.repo.decide("2" * 40)
        self.assertTrue(tests)
        self.assertIn("git failed", reason)


class MainTests(unittest.TestCase):
    def setUp(self):
        tmp = tempfile.TemporaryDirectory()
        self.addCleanup(tmp.cleanup)
        self.tmp = Path(tmp.name)
        self.repo = Repo(tmp.name)
        self.output = self.tmp / "github_output"
        self.output.write_text("", encoding="utf-8")
        self.event = self.tmp / "event.json"

    def run_main(self, name, payload, sha=""):
        """Run main() in the repo with the event; return (exit code, output file lines, stdout)."""
        if isinstance(payload, dict):
            self.event.write_text(json.dumps(payload), encoding="utf-8")
        elif payload is not None:
            self.event.write_text(payload, encoding="utf-8")
        env = {"GITHUB_EVENT_NAME": name, "GITHUB_EVENT_PATH": str(self.event),
               "GITHUB_SHA": sha, "GITHUB_OUTPUT": str(self.output)}
        out = io.StringIO()
        cwd = os.getcwd()
        os.chdir(self.repo.path)
        try:
            with mock.patch.dict(os.environ, env), contextlib.redirect_stdout(out):
                code = ci_changes.main()
        finally:
            os.chdir(cwd)
        if self.output.is_dir():
            return code, None, out.getvalue()
        return code, self.output.read_text(encoding="utf-8").splitlines(), out.getvalue()

    def test_push_backlog_only_skips(self):
        """AC-8, AC-9: a push compares before..GITHUB_SHA and writes tests=false with the reason."""
        self.repo.write("backlog.md", "- [x] [#1 X](backlog/001-x.md)\n")
        head = self.repo.commit("backlog")
        code, lines, out = self.run_main("push", {"before": self.repo.base, "after": head}, head)
        self.assertEqual((code, lines), (0, ["tests=false"]))
        self.assertIn(SKIP, out)
        self.assertEqual(len(out.splitlines()), 1)

    def test_push_compares_with_before(self):
        """AC-8: the push range starts at before, so a code change before it does not count."""
        self.repo.write("src/RoomKit/Code.cs", "class Other {}\n")
        middle = self.repo.commit("code")
        self.repo.write("backlog.md", "- [x] [#1 X](backlog/001-x.md)\n")
        head = self.repo.commit("backlog")
        self.assertEqual(self.run_main("push", {"before": middle, "after": head}, head)[1], ["tests=false"])
        self.output.write_text("", encoding="utf-8")
        self.assertEqual(self.run_main("push", {"before": self.repo.base, "after": head}, head)[1], ["tests=true"])

    def test_push_zero_before_runs(self):
        """AC-7: a push whose before is all zeros or missing runs the tests."""
        head = self.repo.commit("empty")
        for payload in ({"before": "0" * 40, "after": head}, {"after": head}):
            self.output.write_text("", encoding="utf-8")
            code, lines, out = self.run_main("push", payload, head)
            self.assertEqual((code, lines), (0, ["tests=true"]))
            self.assertIn("no base", out)

    def test_pull_request_uses_merge_base(self):
        """AC-8: a pull request compares from the merge base, so code on the base branch does not count."""
        fork = self.repo.base
        self.repo.write("src/RoomKit/Code.cs", "class DevelopMovedOn {}\n")
        develop = self.repo.commit("code on develop")
        run_git(self.repo.path, "checkout", "-q", "-b", "side", fork)
        self.repo.write("backlog.md", "- [x] [#1 X](backlog/001-x.md)\n")
        side = self.repo.commit("backlog on side")
        payload = {"pull_request": {"base": {"sha": develop}, "head": {"sha": side}}}
        code, lines, out = self.run_main("pull_request", payload, "3" * 40)
        self.assertEqual((code, lines), (0, ["tests=false"]))
        self.assertIn(SKIP, out)

    def test_unknown_event_runs(self):
        """AC-7: an event that is neither push nor pull_request runs the tests."""
        code, lines, out = self.run_main("workflow_dispatch", {})
        self.assertEqual((code, lines), (0, ["tests=true"]))
        self.assertIn("workflow_dispatch", out)

    def test_unreadable_payload_runs(self):
        """AC-7: a payload that is not JSON, lacks its fields or is missing runs the tests."""
        for payload in ("not json", {"pull_request": {}}, None):
            self.output.write_text("", encoding="utf-8")
            if payload is None and self.event.exists():
                self.event.unlink()
            name = "push" if payload is None else "pull_request"
            code, lines, out = self.run_main(name, payload)
            self.assertEqual((code, lines), (0, ["tests=true"]))
            self.assertIn("unreadable", out)

    def test_unwritable_output_fails_the_step(self):
        """A GITHUB_OUTPUT it cannot write fails the step instead of skipping the tests silently."""
        self.output.unlink()
        self.output.mkdir()
        code, _lines, out = self.run_main("other", {})
        self.assertEqual(code, 1)
        self.assertIn("cannot write GITHUB_OUTPUT", out)


if __name__ == "__main__":
    unittest.main()
