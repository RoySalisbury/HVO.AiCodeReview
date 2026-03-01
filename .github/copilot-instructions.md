# Copilot Instructions

## Project Overview

**HVO.AiCodeReview** is a .NET 10 ASP.NET Core Web API that performs AI-powered code reviews on Azure DevOps pull requests. It analyzes PR diffs using Azure OpenAI, posts inline comments and summary threads, adds reviewer votes, and tracks full review history — all driven by a single HTTP API call.

- **Runtime**: .NET 10 / ASP.NET Core Web API
- **AI**: Azure OpenAI (GPT-4o, GPT-4o-mini, o4-mini, o3-mini, GPT-5-mini)
- **Target**: Azure DevOps REST API v7.1
- **Tests**: MSTest with method-level parallelization (509+ tests)

## Key Architecture

- **Multi-Provider AI**: Single or consensus mode with pluggable providers
- **Three Review Depths**: Quick (Pass 1 only), Standard (Pass 1+2), Deep (Pass 1+2+3)
- **Per-Pass Model Routing**: Each review pass can use a different AI model
- **Layered Prompts**: Versioned rule catalog with hot-reload
- **Rate Limiting**: PR-level cooldown + API-level 429 retry with global cooldown signal

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

## Documentation

All documentation lives in `/docs` and is versioned with the code:

| Guide | Description |
|-------|-------------|
| [Getting Started](../docs/getting-started.md) | Prerequisites, quick start, project structure |
| [Configuration](../docs/configuration.md) | Application settings, multi-provider AI config, environment variables |
| [API Reference](../docs/api-reference.md) | Endpoint documentation |
| [Architecture](../docs/architecture.md) | System design, review flow, depth modes |
| [Testing](../docs/testing.md) | Test guide, categories, infrastructure |
| [Pipeline Integration](../docs/pipeline-integration.md) | Azure DevOps pipeline setup |
| [Model Benchmarks](../docs/model-benchmarks.md) | Benchmark results and model selection |

## Dev Container Tool Policy

The dev container does **not** include Node.js, Python, or Azure CLI. Do **not** attempt to use these tools or suggest installing them.

- **Scripting & automation**: Use `bash`/`zsh` shell scripts, `gh` CLI, or `dotnet` CLI
- **JSON processing**: Use `jq` (installed) or .NET `System.Text.Json`
- **Issue/PR management**: Use `gh issue create`, `gh pr create`, etc.
- **Search**: Use `rg` (ripgrep, installed) for text search
- **EF Core / database CLI**: Not installed — use `dotnet` CLI or EF via code
- **Never** suggest `npm`, `npx`, `pip`, `python`, `az`, or `dotnet-script` commands

### Heredoc / Multi-Line String Warning

**Do NOT use `cat << 'EOF'` or any heredoc syntax in terminal commands.** Heredocs are unreliable in this environment — content frequently gets corrupted, garbled, or truncated. Instead:

1. Write multi-line content to a file using the file-creation tool (e.g., `create_file`).
2. Reference that file in the terminal command (e.g., `gh issue create --body-file /tmp/issue-body.md`).

This applies to **all** cases where you need to pass multi-line text to a CLI command.
