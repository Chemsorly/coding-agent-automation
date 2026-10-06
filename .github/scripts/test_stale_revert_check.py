"""Tests for stale_revert_check.py.

Each test builds a throwaway git repository with explicit author and committer dates, so the
"landed on main after the PR's work began" ordering is under the test's control, and runs the
script against it as CI would: as a subprocess with --base and --head.

Run: python3 -m unittest discover -s .github/scripts -p 'test_*.py' -v
"""

import contextlib
import importlib.util
import io
import os
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path
from unittest import mock

SCRIPT = Path(__file__).resolve().parent / "stale_revert_check.py"

# Commit times, in seconds since the epoch. T1 is when the PR's work begins.
T0, T1, T2, T3 = 1_750_000_000, 1_750_010_000, 1_750_020_000, 1_750_030_000

BASE = [f"    var setting{i} = Load({i});" for i in range(12)]
MAIN_LINES = [
    "    if (retries > MaxRetries)",
    "        logger.LogWarning(\"Giving up after {Retries}\", retries);",
    "    metrics.RecordFailure(jobId);",
    "    await notifier.NotifyAsync(jobId);",
]
PR_LINE = "    var fromPr = ComputePrValue();"


class Repo:
    """A throwaway git repository whose commits carry explicit timestamps."""

    def __init__(self, path):
        self.path = Path(path)
        self.path.mkdir()
        # Keep the developer's global config (signing, hooks, default branch) out of the tests.
        self.env = dict(os.environ, GIT_CONFIG_GLOBAL=os.devnull, GIT_CONFIG_NOSYSTEM="1")
        self.env.pop("GITHUB_STEP_SUMMARY", None)
        self.git("init", "-q", "-b", "main")
        self.git("config", "user.name", "Test")
        self.git("config", "user.email", "test@example.com")
        self.git("config", "commit.gpgsign", "false")

    def git(self, *args, author=None, committer=None):
        env = dict(self.env)
        if author is not None:
            env["GIT_AUTHOR_DATE"] = f"@{author} +0000"
        if committer or author:
            env["GIT_COMMITTER_DATE"] = f"@{committer or author} +0000"
        result = subprocess.run(["git", *args], cwd=self.path, env=env, check=True,
                                capture_output=True, text=True)
        return result.stdout.strip()

    def write(self, name, lines):
        file = self.path / name
        file.parent.mkdir(parents=True, exist_ok=True)
        file.write_text("\n".join(lines) + "\n")

    def commit(self, message, author, committer=None):
        self.git("add", "-A")
        self.git("commit", "-q", "-m", message, author=author, committer=committer)
        return self.git("rev-parse", "HEAD")

    def run_check(self, base="main", head="pr", body=None, fail=True):
        """Run the script (with --fail unless fail=False); return (exit code, output, step summary)."""
        args = [sys.executable, str(SCRIPT), "--base", self.git("rev-parse", base),
                "--head", self.git("rev-parse", head)]
        if fail:
            args.append("--fail")
        summary = self.path.parent / "summary.md"
        args += ["--summary-file", str(summary)]
        if body is not None:
            body_file = self.path.parent / "body.txt"
            body_file.write_text(body)
            args += ["--pr-body-file", str(body_file)]
        result = subprocess.run(args, cwd=self.path, env=self.env, capture_output=True, text=True)
        text = summary.read_text() if summary.exists() else ""
        return result.returncode, result.stdout + result.stderr, text


class StaleRevertCheckTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.repo = Repo(Path(self.tmp.name) / "repo")
        self.repo.write("src/a.cs", BASE)
        self.repo.commit("init", T0)

    def start_pr(self):
        """The PR's first commit, authored at T1 on a branch off main."""
        self.repo.git("checkout", "-q", "-b", "pr", "main")
        self.repo.write("src/a.cs", BASE + [PR_LINE])
        sha = self.repo.commit("feat: PR work", T1)
        self.repo.git("checkout", "-q", "main")
        return sha

    def merge_on_main(self, lines, subject, when):
        """Main commit X that inserts `lines` near the top of src/a.cs."""
        self.repo.git("checkout", "-q", "main")
        self.repo.write("src/a.cs", BASE[:2] + lines + BASE[2:])
        return self.repo.commit(subject, when)

    def rebase_and_overwrite(self, message="feat: PR work, continued", extra_files=None):
        """Rebase the PR onto main, then re-apply the PR's stale src/a.cs (drops X's lines)."""
        self.repo.git("checkout", "-q", "pr")
        self.repo.git("rebase", "-q", "main", committer=T3)  # keeps the T1 author date
        self.repo.write("src/a.cs", BASE + [PR_LINE])
        for name, lines in (extra_files or {}).items():
            self.repo.write(name, lines)
        return self.repo.commit(message, T3)

    def test_a_stale_overwrite_after_rebase_is_flagged(self):
        self.start_pr()
        x = self.merge_on_main(MAIN_LINES, "feat: Retry failed jobs (#101)", T2)
        self.rebase_and_overwrite()

        code, out, summary = self.repo.run_check()

        self.assertEqual(code, 1, out)
        self.assertIn("#101", out)
        self.assertIn(x[:7], out)
        self.assertIn("::error file=src/a.cs,line=3::", out)
        self.assertIn("mention #101 in the PR description", out)
        self.assertIn("#101", summary)
        self.assertIn("Retry failed jobs", summary)
        self.assertIn("MaxRetries", summary)

    def test_a2_without_fail_the_stale_overwrite_is_reported_as_a_warning(self):
        # The pipeline treats any failed workflow run on its branch as CI to fix, so by default
        # the check reports (warning annotations, step summary) and passes.
        self.start_pr()
        self.merge_on_main(MAIN_LINES, "feat: Retry failed jobs (#101)", T2)
        self.rebase_and_overwrite()

        code, out, summary = self.repo.run_check(fail=False)

        self.assertEqual(code, 0, out)
        self.assertIn("::warning file=src/a.cs,line=3::", out)
        self.assertNotIn("::error", out)
        self.assertIn("#101", summary)

    def test_b_pr_body_mentioning_the_pr_acknowledges_the_deletion(self):
        self.start_pr()
        self.merge_on_main(MAIN_LINES, "feat: Retry failed jobs (#101)", T2)
        self.rebase_and_overwrite()

        code, out, _ = self.repo.run_check(body="Removes the retry added by #101 on purpose.")

        self.assertEqual(code, 0, out)

    def test_b2_commit_message_naming_the_sha_acknowledges_the_deletion(self):
        self.start_pr()
        x = self.merge_on_main(MAIN_LINES, "feat: Retry failed jobs (#101)", T2)
        self.rebase_and_overwrite(message=f"refactor: Drop the retry from {x[:9]}")

        code, out, _ = self.repo.run_check(body="Unrelated description, mentions #1010.")

        self.assertEqual(code, 0, out)

    def test_c_lines_that_landed_before_the_pr_began_are_not_flagged(self):
        self.merge_on_main(MAIN_LINES, "feat: Retry failed jobs (#100)", T0 + 100)
        self.repo.git("checkout", "-q", "-b", "pr")
        self.repo.write("src/a.cs", BASE + [PR_LINE])
        self.repo.commit("feat: Remove the retry", T1)

        code, out, summary = self.repo.run_check()

        self.assertEqual(code, 0, out)
        self.assertIn("no stale reverts found", summary)

    def test_d_lines_moved_to_another_file_are_not_flagged(self):
        self.start_pr()
        self.merge_on_main(MAIN_LINES, "feat: Retry failed jobs (#101)", T2)
        self.rebase_and_overwrite(extra_files={"src/b.cs": ["void Retry()", "{"] + MAIN_LINES + ["}"]})

        code, out, _ = self.repo.run_check()

        self.assertEqual(code, 0, out)

    def test_e_fewer_than_three_non_trivial_lines_are_not_flagged(self):
        self.start_pr()
        lines = ["    if (retries > MaxRetries)", "    {", "", "        return;", "    }"]
        self.merge_on_main(lines, "feat: Stop early (#101)", T2)
        self.rebase_and_overwrite()

        code, out, _ = self.repo.run_check()

        self.assertEqual(code, 0, out)

    def test_f_commit_from_a_merged_branch_maps_to_the_merge_commit(self):
        # Y is committed before the PR begins but only reaches main via a merge commit after it.
        self.repo.git("checkout", "-q", "-b", "feature")
        self.repo.write("src/a.cs", BASE[:2] + MAIN_LINES + BASE[2:])
        y = self.repo.commit("Add retry", T0 + 100)
        self.start_pr()
        self.repo.git("merge", "-q", "--no-ff", "-m", "Merge pull request #202 from org/feature",
                      "feature", author=T2)
        merge = self.repo.git("rev-parse", "HEAD")
        self.rebase_and_overwrite()

        code, out, summary = self.repo.run_check()

        self.assertEqual(code, 1, out)
        self.assertIn(merge[:7], out)
        self.assertNotIn(y[:7], out)
        self.assertIn("#202", summary)

    def test_g_a_path_git_quotes_is_blamed_unquoted(self):
        name = 'src/we"ird.cs'
        self.repo.write(name, BASE)
        self.repo.commit("add weird", T0 + 10)
        self.repo.git("checkout", "-q", "-b", "pr", "main")
        self.repo.write(name, BASE + [PR_LINE])
        self.repo.commit("feat: PR work", T1)
        self.repo.git("checkout", "-q", "main")
        self.repo.write(name, BASE[:2] + MAIN_LINES + BASE[2:])
        self.repo.commit("feat: Retry failed jobs (#101)", T2)
        self.repo.git("checkout", "-q", "pr")
        self.repo.git("rebase", "-q", "main", committer=T3)
        self.repo.write(name, BASE + [PR_LINE])
        self.repo.commit("feat: PR work, continued", T3)

        code, out, summary = self.repo.run_check()

        self.assertEqual(code, 1, out)
        self.assertIn("::error file=src/we\"ird.cs,line=3::", out)
        self.assertIn("#101", summary)

    def test_generated_paths_are_ignored(self):
        self.repo.write("src/Migrations/Snapshot.cs", BASE)
        self.repo.commit("add migration", T0 + 10)
        self.start_pr()
        self.repo.write("src/Migrations/Snapshot.cs", BASE[:2] + MAIN_LINES + BASE[2:])
        self.repo.commit("feat: New migration (#101)", T2)
        self.repo.git("checkout", "-q", "pr")
        self.repo.git("rebase", "-q", "main", committer=T3)
        self.repo.write("src/Migrations/Snapshot.cs", BASE)
        self.repo.commit("feat: Regenerate migration", T3)

        code, out, _ = self.repo.run_check()

        self.assertEqual(code, 0, out)

    def test_no_pr_commits_passes(self):
        self.repo.git("branch", "pr")

        code, out, _ = self.repo.run_check()

        self.assertEqual(code, 0, out)

    def test_shallow_history_warns_and_passes(self):
        self.start_pr()
        self.merge_on_main(MAIN_LINES, "feat: Retry failed jobs (#101)", T2)
        self.rebase_and_overwrite()
        shallow = Path(self.tmp.name) / "shallow"
        subprocess.run(["git", "clone", "-q", "--depth=1", "--no-single-branch",
                        self.repo.path.as_uri(), str(shallow)],
                       check=True, env=self.repo.env, capture_output=True)
        # Depth 1 cuts the PR tip off from main, so there is no merge base to compute.
        base, head = self.repo.git("rev-parse", "main"), self.repo.git("rev-parse", "pr")
        result = subprocess.run([sys.executable, str(SCRIPT), "--base", base, "--head", head],
                                cwd=shallow, env=self.repo.env, capture_output=True, text=True)

        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertIn("::warning", result.stdout)


class InternalErrorTests(unittest.TestCase):
    """A bug or git failure in the check must not fail the run unless --fail is set."""

    def setUp(self):
        spec = importlib.util.spec_from_file_location("stale_revert_check", SCRIPT)
        self.check = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(self.check)

    def run_main(self, *extra):
        out = io.StringIO()
        with mock.patch.object(self.check, "find_stale_reverts", side_effect=RuntimeError("boom")), \
                contextlib.redirect_stdout(out):
            code = self.check.main(["--base", "a", "--head", "b", "--summary-file", os.devnull, *extra])
        return code, out.getvalue()

    def test_an_internal_error_is_a_warning_and_passes(self):
        code, out = self.run_main()

        self.assertEqual(code, 0, out)
        self.assertIn("::warning title=Stale revert check failed::", out)
        self.assertIn("boom", out)

    def test_with_fail_an_internal_error_fails(self):
        code, out = self.run_main("--fail")

        self.assertEqual(code, 1, out)


if __name__ == "__main__":
    unittest.main()
