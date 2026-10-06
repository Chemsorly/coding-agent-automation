#!/usr/bin/env python3
"""Flag a pull request that deletes code the base branch merged after the PR's work began.

Until #3325 the pipeline's rework rebase resolved conflicts in favour of the stale PR branch, so a
squash merge could silently revert what other PRs had merged to main in the meantime. This check
looks for that pattern. It blames every line the PR deletes at the merge base and maps the blamed
commit to the first-parent commit X of the base branch that brought it in. X is flagged when it
landed after the PR's earliest commit was authored (author dates survive rebases), the PR deletes
at least MIN_LINES non-trivial lines of it, most of those lines are not re-added elsewhere (moves
are fine) and the PR description and commit messages do not mention X's #PR number or sha.

Usage: stale_revert_check.py --base SHA --head SHA [--pr-body-file PATH] [--summary-file PATH] [--fail]
By default findings are reported as warning annotations and the step summary, and the exit code is 0:
the pipeline treats any failed workflow run on its branch as CI for its agent to fix, and some findings
are deliberate rewrites of fresh code. With --fail, findings are error annotations and the exit code
is 1. A history too shallow to check always exits 0.
"""

import argparse
import html
import os
import re
import subprocess
import sys
from collections import defaultdict
from dataclasses import dataclass
from datetime import datetime, timezone
from itertools import groupby
from pathlib import Path

MIN_LINES = 3  # fewer non-trivial deleted lines from one commit are not reported
MOVED_SHARE = 0.5  # deleted lines re-added elsewhere at this share or more are a move
MAX_ANNOTATIONS = 10
SAMPLE_LINES = 5
GENERATED = re.compile(
    r"/Migrations/|\.Designer\.cs$|ModelSnapshot\.cs$|\.min\.(js|css)$|\.svg$|\.snap$"
    r"|/(packages\.lock\.json|package-lock\.json|pnpm-lock\.yaml|[^/]*\.lock)$")
TRIVIAL_WORDS = {"", "else", "try", "finally"}
HUNK = re.compile(r"^@@ -(\d+)(?:,\d+)? \+\d+(?:,\d+)? @@")
BLAME_HEADER = re.compile(r"^([0-9a-f]{40}) \d+ (\d+)")


@dataclass(eq=False)
class Finding:
    sha: str
    subject: str
    merged_at: int
    lines: list  # (path, line at the merge base, text)

    @property
    def reference(self):
        """How to name X so a PR description acknowledges it: its PR number, else its sha."""
        numbers = re.findall(r"#(\d+)", self.subject)
        return f"#{numbers[-1]}" if numbers else self.sha[:9]


def git(*args):
    """Run git in the current directory and return its stdout."""
    return subprocess.run(["git", "-c", "core.quotepath=off", *args], check=True,
                          capture_output=True, encoding="utf-8", errors="replace").stdout


def is_trivial(text):
    """Blank lines, punctuation-only lines (braces, `//`, `///`) and bare else/try/finally."""
    return re.sub(r"\W+", " ", text).strip() in TRIVIAL_WORDS


def parse_diff(merge_base, head):
    """Diff the PR against the merge base and return (deleted, added).

    deleted: (path, line at the merge base, text) of each line removed from a modified or renamed
    file (the --diff-filter=MR set). added: the trimmed text of every line the PR adds, any file.
    """
    deleted, added = [], set()
    old_path = new_path = None
    old_line, in_hunk = 0, False
    diff = git("diff", "-U0", "-M", "--no-color", "--no-ext-diff", "--src-prefix=a/",
               "--dst-prefix=b/", merge_base, head)
    for row in diff.splitlines():
        if row.startswith("diff --git "):
            old_path, new_path, in_hunk = None, None, False
        elif not in_hunk and row.startswith("--- "):
            old_path = None if row == "--- /dev/null" else row[6:].rstrip("\t")
        elif not in_hunk and row.startswith("+++ "):
            new_path = None if row == "+++ /dev/null" else row[6:].rstrip("\t")
        elif match := HUNK.match(row):
            old_line, in_hunk = int(match[1]), True
        elif in_hunk and row.startswith("-"):
            if old_path and new_path and not GENERATED.search("/" + old_path):
                deleted.append((old_path, old_line, row[1:]))
            old_line += 1
        elif in_hunk and row.startswith("+"):
            added.add(row[1:].strip())
    return deleted, added


def blame(merge_base, deleted):
    """Map each deleted (path, line) to the commit that last changed it at the merge base."""
    lines_by_path = defaultdict(list)
    for path, line, _ in deleted:
        lines_by_path[path].append(line)
    origin = {}
    for path, lines in lines_by_path.items():
        options = []
        # Consecutive line numbers share (line - index), so each group is one -L start,end range.
        for _, group in groupby(enumerate(sorted(lines)), key=lambda pair: pair[1] - pair[0]):
            numbers = [line for _, line in group]
            options += ["-L", f"{numbers[0]},{numbers[-1]}"]
        for row in git("blame", "-w", "--porcelain", *options, merge_base, "--", path).splitlines():
            if match := BLAME_HEADER.match(row):
                origin[(path, int(match[2]))] = match[1]
    return origin


def landing_commits(base, since):
    """Map commits to the first-parent commit of `base` that brought them onto the base branch.

    First-parent commits (squash merges) map to themselves, commits of a merged branch to the merge.
    Merges at or before `since` are not expanded: what they brought in predates the PR's work.
    """
    landed = {}
    for row in git("log", "--first-parent", "--format=%H %ct %P", base).splitlines():
        sha, committed, *parents = row.split()
        landed[sha] = sha
        if len(parents) > 1 and int(committed) > since:
            # <merge>^1..<merge> is everything the merge brought in, from any further parent.
            for commit in git("rev-list", f"{sha}^1..{sha}").split():
                landed.setdefault(commit, sha)
    return landed


def acknowledged(finding, text):
    """True when `text` names one of X's #N references or a 7+ character prefix of its sha."""
    if any(re.search(rf"#{number}(?!\d)", text) for number in re.findall(r"#(\d+)", finding.subject)):
        return True
    return any(finding.sha.startswith(token) for token in re.findall(r"\b[0-9a-f]{7,40}\b", text.lower()))


def find_stale_reverts(base, head, pr_body):
    """Return the Findings for `head`, or None when the history is too shallow to check."""
    try:
        merge_base = git("merge-base", base, head).strip()
    except subprocess.CalledProcessError:
        return None
    pr_commits = dict(row.split() for row in
                      git("log", "--no-merges", "--format=%H %at", f"{base}..{head}").splitlines())
    if not pr_commits:
        return []
    pr_start = min(int(authored) for authored in pr_commits.values())
    messages = git("log", "--no-merges", "--format=%B", f"{base}..{head}")

    deleted, added = parse_diff(merge_base, head)
    deleted = [entry for entry in deleted if not is_trivial(entry[2])]
    origin = blame(merge_base, deleted)
    landed = landing_commits(base, pr_start)
    lines_by_commit = defaultdict(list)
    for path, line, text in deleted:
        landing = landed.get(origin.get((path, line)))
        if landing and landing not in pr_commits:
            lines_by_commit[landing].append((path, line, text))

    findings = []
    for sha, lines in lines_by_commit.items():
        if len(lines) < MIN_LINES:
            continue
        committed, subject = git("show", "-s", "--format=%ct%x00%s", sha).rstrip("\n").split("\0", 1)
        finding = Finding(sha, subject, int(committed), lines)
        moved = sum(text.strip() in added for _, _, text in lines)
        if finding.merged_at > pr_start and moved < MOVED_SHARE * len(lines) \
                and not acknowledged(finding, pr_body + "\n" + messages):
            findings.append(finding)
    return sorted(findings, key=lambda finding: finding.merged_at)


def escape(value, is_property=False):
    """Escape a workflow-command value (annotation message or property)."""
    value = value.replace("%", "%25").replace("\r", "%0D").replace("\n", "%0A")
    return value.replace(":", "%3A").replace(",", "%2C") if is_property else value


def when(timestamp):
    return datetime.fromtimestamp(timestamp, timezone.utc).strftime("%Y-%m-%d %H:%M UTC")


def annotate(findings, level):
    """Print one `level` (warning or error) annotation per flagged commit and file, at its first deleted line."""
    places = {}  # (commit, path) -> first deleted line; dicts keep insertion order
    for finding in findings:
        for path, line, _ in finding.lines:
            places.setdefault((finding, path), line)
    for (finding, path), line in list(places.items())[:MAX_ANNOTATIONS]:
        message = (f"This PR deletes {len(finding.lines)} line(s) that {finding.sha[:9]} "
                   f"\"{finding.subject}\" merged on {when(finding.merged_at)}, after this PR's "
                   f"work began (line numbers are from the merge base). Restore them, or if the "
                   f"deletion is intentional mention {finding.reference} in the PR description.")
        print(f"::{level} file={escape(path, True)},line={line}::{escape(message)}")


def cell(text):
    return html.escape(text).replace("|", "&#124;")


def summary_markdown(findings):
    """The step summary: a table of the flagged commits and how to proceed."""
    out = [f"## Stale revert check: this PR reverts {len(findings)} change(s) from the base branch",
           "", "This PR deletes lines that the changes below merged after this PR's work began, and "
           "does not re-add them elsewhere: the mark of a branch carrying stale file contents (a "
           "conflict resolved in favour of the old version, or an old copy of a file re-applied).", "",
           "| Merged change | Merged at | Lines deleted | Files | Sample lines |",
           "| --- | --- | ---: | --- | --- |"]
    for finding in findings:
        files = sorted({path for path, _, _ in finding.lines})
        samples = "<br>".join(f"<code>{cell(text.strip())}</code>"
                              for _, _, text in finding.lines[:SAMPLE_LINES])
        out.append(f"| {cell(finding.reference)} <code>{finding.sha[:9]}</code> {cell(finding.subject)} "
                   f"| {when(finding.merged_at)} | {len(finding.lines)} "
                   f"| {'<br>'.join(f'<code>{cell(f)}</code>' for f in files)} | {samples} |")
    references = ", ".join(finding.reference for finding in findings)
    out += ["", "**How to proceed:** restore the deleted lines (rebase onto the base branch and keep "
            "its version of them). If a deletion is intentional, mention the change in the PR "
            f"description ({references}); editing the description re-runs this check.", ""]
    return "\n".join(out)


def main(argv=None):
    """Run the check; return the process exit code."""
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--base", required=True, help="base branch commit of the PR")
    parser.add_argument("--head", required=True, help="head commit of the PR")
    parser.add_argument("--pr-body-file", help="file holding the PR description")
    parser.add_argument("--summary-file", default=os.environ.get("GITHUB_STEP_SUMMARY"),
                        help="markdown file to append the report to (default: $GITHUB_STEP_SUMMARY)")
    parser.add_argument("--fail", action="store_true",
                        help="report findings as errors and exit 1 (default: warnings, exit 0)")
    args = parser.parse_args(argv)
    pr_body = "" if not args.pr_body_file else \
        Path(args.pr_body_file).read_text(encoding="utf-8", errors="replace")

    findings = find_stale_reverts(args.base, args.head, pr_body)
    if findings is None:
        print("::warning title=Stale revert check skipped::No merge base between "
              f"{args.base} and {args.head}: the history is too shallow (check out with fetch-depth: 0).")
        return 0
    annotate(findings, "error" if args.fail else "warning")
    report = summary_markdown(findings) if findings else "Stale revert check: no stale reverts found.\n"
    if args.summary_file:
        with open(args.summary_file, "a", encoding="utf-8") as file:
            file.write(report)
    print(report)
    return 1 if findings and args.fail else 0


if __name__ == "__main__":
    sys.exit(main())
