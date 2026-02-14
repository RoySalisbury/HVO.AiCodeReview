# HVO.AiCodeReview — Copilot Session Context

> This file captures the full development history and architectural context from prior Copilot sessions.
> Attach this file as context when starting a new chat to preserve continuity.

---

## 1. Project Overview

**HVO.AiCodeReview** is a .NET 8 ASP.NET Core Web API that performs **AI-powered code reviews on Azure DevOps pull requests**. It analyzes PR diffs using Azure OpenAI, posts inline comments and summary threads, adds reviewer votes, and tracks full review history — all driven by a single HTTP API call.

- **Runtime**: .NET 8 / ASP.NET Core Web API on `http://localhost:5094`
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
│   ├── ai-code-review.ps1              # PowerShell pipeline integration script
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
│           ├── IAzureDevOpsService.cs
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

## 10. Current State

- **HEAD**: commit `7abb165` on `main`
- **Build**: 0 warnings, 0 errors
- **Tests**: 42 total (41 passed, 1 skipped)
- **Working tree**: Clean
- **No pending work or known issues**
