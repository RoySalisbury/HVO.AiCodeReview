# HVO.AiCodeReview — Copilot Session Context

> This file captures the full development history and architectural context from prior Copilot sessions.
> Attach this file as context when starting a new chat to preserve continuity.

---

## 1. Project Overview

**HVO.AiCodeReview** is a .NET 10 ASP.NET Core Web API that performs **AI-powered code reviews on Azure DevOps pull requests**. It analyzes PR diffs using Azure OpenAI, posts inline comments and summary threads, adds reviewer votes, and tracks full review history — all driven by a single HTTP API call.

- **Runtime**: .NET 10 / ASP.NET Core Web API on `http://localhost:5094`
- **AI**: Azure.AI.OpenAI v2.1.0, deployment `gpt-4o`
- **Target**: Azure DevOps REST API v7.1
- **Azure DevOps Org**: `HiltonGrandVacations`, Project: `OneVision`
- **GitHub Repo**: `https://github.com/RoySalisbury/HVO.AiCodeReview`
- **Local Path**: `/Users/roys/Development/_gitHub/RoySalisbury/HVO.AiCodeReview`

---

## 2. Repository Structure

Follows the HVO.Enterprise convention (`src/`, `tests/`, `scripts/`, `docs/`):

```
HVO.AiCodeReview/
├── .gitignore                          # Includes appsettings.Development.json & appsettings.Test.json
├── HVO.AiCodeReview.sln                # Solution at root with src/tests solution folders
├── README.md                           # Comprehensive docs (1004 lines)
├── docs/                               # Documentation (empty, ready for use)
├── scripts/
│   ├── ai-code-review.sh               # Bash pipeline integration script (curl/jq)
│   └── azure-pipelines-template.yml    # Azure Pipelines template
├── src/
│   └── HVO.AiCodeReview/               # Web API project
│       ├── HVO.AiCodeReview.csproj
│       ├── Program.cs
│       ├── Dockerfile
│       ├── appsettings.json            # Base config (committed)
│       ├── appsettings.Development.json          # SECRETS - gitignored
│       ├── appsettings.Development.template.json # Template with placeholders
│       ├── custom-instructions.json    # AI review instructions
│       ├── Controllers/
│       │   └── CodeReviewController.cs
│       ├── Models/
│       │   ├── AiProviderSettings.cs
│       │   ├── AzureDevOpsSettings.cs
│       │   ├── AzureOpenAISettings.cs
│       │   ├── CodeReviewResult.cs
│       │   ├── FileChange.cs           # Props: FilePath, ChangeType, OriginalContent, ModifiedContent, UnifiedDiff, ChangedLineRanges
│       │   ├── PullRequestInfo.cs
│       │   ├── ReviewMetadata.cs
│       │   ├── ReviewMetricsResponse.cs
│       │   ├── ReviewRequest.cs
│       │   ├── ReviewResponse.cs
│       │   └── ReviewStatusUpdate.cs
│       ├── Properties/
│       │   └── launchSettings.json
│       └── Services/
│           ├── AzureDevOpsService.cs
│           ├── AzureOpenAiReviewService.cs  # (renamed from CodeReviewService.cs)
│           ├── CodeReviewOrchestrator.cs
│           ├── CodeReviewServiceFactory.cs
│           ├── ConsensusReviewService.cs
│           ├── IDevOpsService.cs            # Provider-agnostic DevOps interface
│           ├── ICodeReviewOrchestrator.cs
│           ├── ICodeReviewService.cs        # Interface: ReviewAsync, ReviewFileAsync, VerifyThreadResolutionsAsync
│           └── ReviewRateLimiter.cs
└── tests/
    └── HVO.AiCodeReview.Tests/
        ├── HVO.AiCodeReview.Tests.csproj
        ├── appsettings.Test.json             # SECRETS - gitignored
        ├── appsettings.Test.template.json    # Template with placeholders
        ├── MSTestSettings.cs                 # [assembly: Parallelize(Scope = ExecutionScope.MethodLevel)]
        ├── AiQualityVerificationTests.cs     # 3 LiveAI tests
        ├── AiSmokeTest.cs
        ├── MultiProviderTests.cs             # 15 tests (factory, consensus, settings)
        ├── ReviewFlowIntegrationTests.cs
        ├── ReviewLifecycleTests.cs           # 5 independent parallel tests
        ├── ServiceIntegrationTests.cs
        ├── UnifiedDiffTests.cs
        └── Helpers/
            ├── FakeCodeReviewService.cs      # ResultFactory & VerificationResultFactory overrides
            ├── TestPullRequestHelper.cs
            └── TestServiceBuilder.cs         # BuildWithFakeAi() and BuildWithRealAi()
```

---

## 3. Architecture

### Multi-Provider AI Architecture

The system supports single-provider and consensus (multi-provider) modes:

- **`AiProviderSettings`** — Config model: `Mode` (single/consensus), `ActiveProvider`, `ConsensusThreshold`, `MaxParallelReviews`, `Dictionary<string, ProviderConfig> Providers`
- **`ProviderConfig`** — Per-provider: `Type`, `DisplayName`, `Endpoint`, `ApiKey`, `Model`, `CustomInstructionsPath`, `Enabled`
- **`CodeReviewServiceFactory`** — Config-driven factory creating the right `ICodeReviewService`. Extension method `AddCodeReviewService()` used in `Program.cs`. Falls back to legacy `AzureOpenAISettings` when no providers configured.
- **`AzureOpenAiReviewService`** — Azure OpenAI implementation with dual constructors (legacy `IOptions` + factory raw params)
- **`ConsensusReviewService`** — Meta-provider: fans out to N providers via `FanOutAsync<T>()`, merges results with line-overlap consensus (3-line tolerance), `CommentsOverlap()`, `VerdictSeverity()`, majority vote for thread verification
- **`ICodeReviewService`** — Interface: `ReviewAsync`, `ReviewFileAsync`, `VerifyThreadResolutionsAsync`

### Key Service Flow

1. `CodeReviewController` receives `POST /api/review` with `{projectName, repositoryName, pullRequestId}`
2. `CodeReviewOrchestrator` coordinates the full review lifecycle:
   - Fetches PR info, checks rate limiter, checks for changes since last review
   - Handles draft PR awareness (review without vote, vote-only on draft→active transition)
   - File-by-file parallel review with configurable `MaxParallelReviews`
   - Posts inline comments with deduplication (tag-based)
   - Posts summary thread with file inventory, verdicts, observations
   - Adds reviewer vote (Approved / Approved with Suggestions / Waiting for Author / Rejected)
   - Stores review metadata in PR Properties (review count, history, commit tracking)
   - Appends review history table to PR description
   - Resolves previously-flagged threads if AI verifies the code is fixed

### Feature Inventory

| Feature | Implementation |
|---------|---------------|
| Tag-based dedup | `CommentAttributionTag` in settings, checked before posting |
| PR Properties | Metadata stored as Azure DevOps PR properties (review count, history, source/target commits) |
| Review history | JSON array in PR properties + visible table in PR description |
| Configurable system prompts | `custom-instructions.json` injected between identity preamble and response format |
| Inline comment accuracy | File-by-file review with unified diff + changed-lines filter |
| Density-based threshold | Comment noise reduction |
| False positive filter | AI verification pass |
| 429 retry logic | Built into Azure OpenAI client |
| Focus tiers | Severity levels: Bug, Security, Concern, Performance, Suggestion |
| Thread resolution | AI verification for previously-flagged threads on re-review |
| Disposable test repos | 6-layer safety system for integration tests |

---

## 4. Test Infrastructure

- **Framework**: MSTest with method-level parallelization
- **Total tests**: 42 (41 pass, 1 skipped)
- **Test categories**:
  - `LiveAI` — 3 tests requiring real Azure OpenAI (excluded by default)
  - `Manual` — Cleanup/utility tests
  - Regular — All others run automatically
- **`TestServiceBuilder`** — `BuildWithFakeAi()` for unit tests, `BuildWithRealAi()` for integration tests
- **`FakeCodeReviewService`** — Implements all 3 interface methods with `ResultFactory` and `VerificationResultFactory` overrides

### Test Filter Command
```bash
dotnet test --filter 'TestCategory!=Manual&FullyQualifiedName!~InspectPR&FullyQualifiedName!~CleanupTestPR'
```

---

## 5. Configuration

### appsettings.json (base, committed)
Contains structure but no secrets.

### appsettings.Development.json (gitignored, has secrets)
Required keys:
- `AzureDevOps:Organization`, `AzureDevOps:PersonalAccessToken`
- `AzureOpenAI:Endpoint`, `AzureOpenAI:ApiKey`, `AzureOpenAI:DeploymentName`
- `AzureDevOps:ReviewTagName`, `AddReviewerVote`, `MinReviewIntervalMinutes`, `CommentAttributionTag`, `ResolveFixedThreadsOnReReview`

### appsettings.Test.json (gitignored, has secrets)
Same secrets plus `AiProvider` section (multi-provider config) and `TestSettings` (project/repo/branch).

### Template files provided
- `appsettings.Development.template.json` — placeholders for onboarding
- `appsettings.Test.template.json` — placeholders for test setup

---

## 6. Important Model Properties (Common Pitfalls)

- `FileChange` properties: `FilePath`, `ChangeType`, `OriginalContent`, **`ModifiedContent`** (NOT `Content`), `UnifiedDiff`, `ChangedLineRanges`
- `ThreadVerificationCandidate` property: **`CurrentCode`** (NOT `CurrentCodeContext`)

---

## 7. Migration History

The project was migrated from Azure DevOps (`HiltonGrandVacations/OneVision/POCResearchScratchProjects/Projects/AiCodeReview`) to GitHub (`RoySalisbury/HVO.AiCodeReview`) on 2026-02-14.

### What changed during migration:
- **Directory structure**: Flat → `src/`, `tests/`, `scripts/`, `docs/` (HVO.Enterprise convention)
- **Project names**: `AiCodeReview` → `HVO.AiCodeReview`, `AiCodeReview.Tests` → `HVO.AiCodeReview.Tests`
- **Solution file**: Moved to repo root with solution folders
- **Dockerfile**: Updated for repo-root build context, entrypoint `HVO.AiCodeReview.dll`
- **InternalsVisibleTo**: Updated to `HVO.AiCodeReview.Tests`
- **ProjectReference**: Updated to `../../src/HVO.AiCodeReview/HVO.AiCodeReview.csproj`
- **Pipeline scripts**: Moved from `AiCodeReview/pipeline/` to `scripts/`
- **Secrets**: `.gitignore` updated to exclude `appsettings.Development.json` and `appsettings.Test.json`
- **No C# code logic changes** — all business logic, namespaces, and APIs are identical

### What was NOT changed:
- C# namespaces (still `AiCodeReview.*` — can be renamed later if desired)
- API endpoints and behavior
- Azure DevOps integration (still reviews Azure DevOps PRs)

---

## 8. Development Phases (Chronological)

1. **Core features** — Tag-based dedup, PR Properties, review history, configurable prompts, inline comments, file-by-file review, noise reduction, unified diff, changed-lines filter, density thresholds, false positive filter, 429 retry, focus tiers, thread resolution, AI verification, disposable test repos with 6-layer safety
2. **Test infrastructure overhaul** — `TestServiceBuilder`, `ReviewLifecycleTests` (5 parallel), `AiQualityVerificationTests` (3 LiveAI), deprecated monolithic test
3. **Multi-provider architecture** — `AiProviderSettings`, `AzureOpenAiReviewService` (renamed), `ConsensusReviewService`, `CodeReviewServiceFactory`, updated orchestrator/Program.cs/settings/tests
4. **Multi-provider tests** — 15 new tests in `MultiProviderTests.cs` covering factory (5) + consensus (8) + settings (2)
5. **GitHub migration** — Restructured to HVO.Enterprise convention, pushed to GitHub

---

## 9. Build & Run Commands

```bash
# Build
cd /Users/roys/Development/_gitHub/RoySalisbury/HVO.AiCodeReview
dotnet build

# Run locally
cd src/HVO.AiCodeReview
dotnet run --launch-profile http
# → http://localhost:5094/swagger

# Run tests (all automated)
dotnet test --filter 'TestCategory!=Manual&FullyQualifiedName!~InspectPR&FullyQualifiedName!~CleanupTestPR'

# Trigger a review via API
curl -X POST http://localhost:5094/api/review \
  -H "Content-Type: application/json" \
  -d '{"projectName":"OneVision","repositoryName":"POCResearchScratchProjects","pullRequestId":12345}'
```

---

## 10. Issue & Pull Request Workflow

All development work follows this structured workflow. **Every step is mandatory** — do not skip steps or defer them.

### Starting Work on an Issue

1. **Select the issue** — Choose from the prioritized issue list (see triage notes or ask the user).
2. **Create a feature branch** from `main`:
   ```bash
   git checkout main && git pull origin main
   git checkout -b feature/{issue-number}-{short-description}
   ```
   Branch naming: `feature/34-rate-limit-handling`, `feature/39-telemetry-integration`, etc.
3. **Post a comment on the issue** indicating work has started:
   ```bash
   gh issue comment {number} --body "Starting work on this issue on branch \`feature/{number}-{description}\`."
   ```
4. **Add the `in-progress` label** (if the label exists):
   ```bash
   gh issue edit {number} --add-label "in-progress"
   ```

### During Development

- Make **incremental commits** with descriptive messages referencing the issue:
  ```
  feat: <description> (#34)
  fix: <description> (#34)
  docs: <description> (#34)
  ```
- **Run all tests** before pushing — every push must be green:
  ```bash
  dotnet build --no-restore
  dotnet test --filter 'TestCategory!=LiveAI&TestCategory!=Benchmark&TestCategory!=Manual&TestCategory!=LiveDevOps' --no-build
  ```
- Write **new unit tests** for all new logic (helpers, services, models).
- **Update `README.md`** if the change adds or modifies:
  - Features (update Features table)
  - Configuration settings or sections
  - Files in the project structure tree
  - Test files or test counts
  - Table of Contents entries for new sections
- **Update this session context file** if the change introduces new architectural patterns, services, or conventions.

### Creating a Pull Request

1. **Verify build & tests** one final time:
   ```bash
   dotnet build && dotnet test --filter 'TestCategory!=LiveAI&TestCategory!=Benchmark&TestCategory!=Manual&TestCategory!=LiveDevOps'
   ```
2. **Ensure README is updated** — this is required for every PR, not optional.
3. **Push the branch**:
   ```bash
   git push -u origin feature/{number}-{description}
   ```
4. **Create the PR** with a comprehensive body:
   ```bash
   gh pr create --title "feat: <description> (#{issue-number})" --base main --body "..."
   ```
   PR body must include:
   - **Summary** — What the PR does and which issue it resolves (`Resolves #N`)
   - **Problem** — What was wrong or missing
   - **Solution** — How it was fixed, with key implementation details
   - **Files changed** — Table of modified/added files with descriptions
   - **Tests** — New test count, total test count, pass/fail/skip summary

### Before Merging a PR

These steps are **blocking** — do not merge until all are complete:

1. **Check for PR review comments** — Read all comments on the PR:
   ```bash
   gh pr view {pr-number} --comments
   ```
   - Address every code review comment (fix code, respond, or discuss).
   - Push additional commits to the PR branch for any required changes.
   - Do not dismiss or ignore review feedback.

2. **Verify all conversations are resolved** — If the PR has unresolved review threads, resolve them before merging.

3. **Re-run tests after any review-driven changes**:
   ```bash
   dotnet build && dotnet test --filter 'TestCategory!=LiveAI&TestCategory!=Benchmark&TestCategory!=Manual&TestCategory!=LiveDevOps'
   ```

4. **Squash-merge into `main`**:
   ```bash
   gh pr merge {pr-number} --squash --delete-branch
   ```

### After Merging

1. **Close the linked issue** (if not auto-closed by `Resolves #N`):
   ```bash
   gh issue close {number} --comment "Completed in PR #{pr-number}."
   ```
2. **Switch back to `main`** and pull:
   ```bash
   git checkout main && git pull origin main
   ```
3. **Remove the `in-progress` label** if it was added:
   ```bash
   gh issue edit {number} --remove-label "in-progress"
   ```

### Quick Reference: PR Checklist

Before requesting merge, verify all of the following:

- [ ] Feature branch created from `main` with correct naming
- [ ] Issue commented with "work started" note
- [ ] All new logic has unit tests
- [ ] `dotnet build` — 0 errors, 0 warnings
- [ ] `dotnet test` — all pass (excluding LiveAI/Benchmark/Manual)
- [ ] `README.md` updated (features, structure, test counts, TOC)
- [ ] PR body includes summary, problem, solution, files, tests
- [ ] PR review comments addressed (if any)
- [ ] All review conversations resolved
- [ ] Tests re-run after any review-driven changes

---

## 11. Current State

- **HEAD**: commit `7abb165` on `main`
- **Build**: 0 warnings, 0 errors
- **Tests**: 42 total (41 passed, 1 skipped)
- **Working tree**: Clean
- **No pending work or known issues**
