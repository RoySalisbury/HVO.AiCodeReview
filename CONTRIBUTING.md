# Contributing

Thank you for your interest in contributing to HVO.AiCodeReview! This document outlines the workflow, standards, and expectations for contributions.

## Table of Contents

- [Getting Started](#getting-started)
- [Branch Naming](#branch-naming)
- [Development Workflow](#development-workflow)
- [Coding Standards](#coding-standards)
- [Running Tests Locally](#running-tests-locally)
- [Pull Request Process](#pull-request-process)
- [PR Checklist](#pr-checklist)
- [Issue & PR Labels](#issue--pr-labels)

---

## Getting Started

1. Clone the repository and set up your environment following [Getting Started](docs/getting-started.md).
2. Copy `appsettings.Development.template.json` → `appsettings.Development.json` and fill in your values.
3. Verify the build: `dotnet build` — expect **zero warnings, zero errors**.
4. Run automated tests: `dotnet test --filter 'TestCategory!=Manual&TestCategory!=LiveAI&TestCategory!=Benchmark&TestCategory!=LiveDevOps'`

---

## Branch Naming

| Pattern | Use For |
|---------|---------|
| `feature/<issue#>-<short-desc>` | New features and enhancements (e.g., `feature/34-rate-limit-handling`) |
| `fix/<issue#>-<short-desc>` | Bug fixes and corrective changes (e.g., `fix/42-null-ref-on-empty-diff`) |

Always branch from `main`. Use the `feature/` or `fix/` pattern for all work, including documentation-only and refactor-only changes.

---

## Development Workflow

1. **Create a feature branch** from `main`:
   ```bash
   git checkout main && git pull origin main
   git checkout -b feature/{issue-number}-{short-description}
   ```

2. **Make incremental commits** with [Conventional Commits](https://www.conventionalcommits.org/):
   ```
   feat: add rate limit header parsing (#34)
   fix: handle null diff in file review (#42)
   docs: update configuration reference (#50)
   test: add consensus threshold tests (#38)
   refactor: extract prompt builder (#45)
   ```

3. **Run build and tests** before pushing:
   ```bash
   dotnet build
   dotnet test --filter 'TestCategory!=Manual&TestCategory!=LiveAI&TestCategory!=Benchmark&TestCategory!=LiveDevOps'
   ```

4. **Push and create a PR** targeting `main`.

---

## Coding Standards

- **Language**: C# / .NET 10
- **Style**: Follow existing conventions in the codebase
- **Warnings**: Build must produce **zero warnings and zero errors**
- **Tests**: All new logic must have unit tests
- **Documentation**: Update docs if the change adds or modifies features, configuration, or API endpoints

---

## Running Tests Locally

```bash
# All automated tests (fast, no API cost)
dotnet test --filter 'TestCategory!=Manual&TestCategory!=LiveAI&TestCategory!=Benchmark&TestCategory!=LiveDevOps'

# Include LiveDevOps tests (needs Azure DevOps PAT)
dotnet test --filter 'TestCategory!=Manual&TestCategory!=LiveAI&TestCategory!=Benchmark'

# LiveAI tests only (real Azure OpenAI — costs money)
dotnet test --filter TestCategory=LiveAI
```

See [Testing](docs/testing.md) for full details on test categories, infrastructure, and configuration.

---

## Pull Request Process

1. **Verify** the build and tests pass with zero warnings.
2. **Create the PR** with a clear title and description:
   - Summary of what the PR does
   - Which issue it resolves (`Resolves #N`)
   - Key implementation details
   - Files changed
3. **Address all review comments** — fix code, respond, or discuss.
4. **Re-run tests** after any review-driven changes.
5. PRs are **squash-merged** into `main`.

---

## PR Checklist

Before requesting review, verify:

- [ ] Feature branch created from `main` with correct naming
- [ ] All new logic has unit tests
- [ ] `dotnet build` — 0 errors, 0 warnings
- [ ] `dotnet test` — all automated tests pass
- [ ] Documentation updated (if applicable)
- [ ] Configuration changes documented in [docs/configuration.md](docs/configuration.md)
- [ ] Issue linked in PR description (`Resolves #N`)

---

## Issue & PR Labels

| Label | Description |
|-------|-------------|
| `bug` | Something isn't working |
| `enhancement` | New feature or improvement |
| `documentation` | Documentation changes only |
| `refactor` | Code refactoring with no behavior change |
| `in-progress` | Work is actively being done |
| `help wanted` | Looking for contributors |
| `good first issue` | Good for newcomers |
