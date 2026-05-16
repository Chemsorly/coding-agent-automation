## Brain Repository — Accumulated Knowledge

A `.brain/` directory is available in this workspace containing accumulated project knowledge
from previous pipeline runs. It is a SEPARATE Git repository — do NOT reference `.brain/` files
in code repository commits, commit messages, or pull request descriptions.

Read `.brain/AGENTS.md` for the brain repo structure and instructions on reading relevant knowledge.
Look for project-specific knowledge in `.brain/projects/coding-agent-automation/`.

Do NOT run git commands (commit, push, pull) inside the `.brain/` directory.
The orchestrator handles all git operations on the brain repository.