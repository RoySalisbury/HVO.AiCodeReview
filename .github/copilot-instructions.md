# Copilot Instructions

## Issue & PR Workflow

Follow this process for every issue. **Never auto-start the next issue unless explicitly instructed.**

### 1. Start Work

- Create a new branch from `main` for the issue (e.g., `feature/<issue#>-<short-description>` or `fix/<issue#>-<short-description>`).

### 2. Implement

- Work the issue on the branch.
- Ensure the project builds with **zero warnings and zero errors**.
- Run **all** tests and confirm they pass with **zero warnings and zero errors**.

### 3. Submit

- Commit, push, and create a PR.
- **Stop and wait** — do not proceed until instructed.

### 4. Code Review

- After the PR is code-reviewed, address **all** review comments.
- Rebuild the project — **zero warnings and zero errors**.
- Re-run **all** tests — **zero warnings and zero errors**.
- Commit and push the fixes.

> **Hard rule:** Never create a PR or merge that has warnings or errors unless specifically instructed otherwise.

### 5. Merge & Clean Up

- Merge the PR.
- Switch back to `main` and pull latest.
- Delete the merged branch (local and remote).

### 6. Wait

- **Do not** start the next issue automatically. Wait for explicit instructions.

## Conventions

- **Branch naming**: `feature/<issue#>-<short-desc>`, `fix/<issue#>-<short-desc>`
- **Commit messages**: Conventional commits (`feat:`, `fix:`, `chore:`, `refactor:`, `test:`, `docs:`)
- **Merge strategy**: Squash merge into `main`
