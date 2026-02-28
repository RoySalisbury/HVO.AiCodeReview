# Testing

The project includes comprehensive integration tests that run against a real Azure DevOps instance using **disposable test repositories**.

## Disposable Test Repositories

Each integration test run creates a **brand-new, temporary Azure DevOps repository**, exercises the review logic against it, and then **permanently deletes** the repository (soft-delete + hard-delete from the recycle bin). This ensures zero leftover artifacts — no abandoned PRs, branches, comments, or metadata polluting the project.

**Lifecycle per test:**

```
1. Create repo  "AiCodeReview-IntTest-{8-char-guid}"
2. Push initial commit to "main" (includes safety marker file)
3. Create test branch with a code change (diff)
4. Open a draft PR
5. Run the test scenario
6. Pass all 6 safety checks
7. Soft-delete the repo
8. Hard-delete from ADO recycle bin (permanent removal)
```

The `TestPullRequestHelper` class (in `Helpers/TestPullRequestHelper.cs`) manages this entire lifecycle and implements `IAsyncDisposable` — cleanup happens automatically when the test completes.

## 6-Layer Safety System

Because tests delete repositories via the Azure DevOps API, a **6-layer safety system** prevents any possibility of accidentally deleting the wrong repo. **All 6 checks must pass** before deletion proceeds; failure of any single check blocks the delete.

| Layer | Check | Details |
|-------|-------|---------|
| **1. Instance Tracking** | `_createdByThisInstance` flag | Only set to `true` when THIS `TestPullRequestHelper` instance successfully created the repo. A helper that didn't create the repo will never delete it. |
| **2. Never-Delete List** | Hardcoded `HashSet<string>` | Repository names `"POCResearchScratchProjects"` and `"OneVision"` (and any others added) are unconditionally protected. Even if all other checks pass, these repos will never be deleted. |
| **3. Name Prefix** | Must start with `AiCodeReview-IntTest-` | Only repos with the test naming convention can be deleted. Any repo without this prefix is blocked. |
| **4. Marker File** | `.test-repo-marker.json` in repo root | During initial commit, a JSON marker file is pushed containing: a **magic string** (`AITK-DISPOSABLE-TEST-REPO-7F3A9B2E-SAFE-TO-DELETE`), a unique **instance token**, and the **repo ID**. Before deletion, all three values are read back from the repo and verified to match. |
| **5. Creation Recency** | Repo must be < 2 hours old | Checks the timestamp of the first commit. Repos that have existed for more than 2 hours are considered "too old" and will not be auto-deleted. |
| **6. PAT Identity & ACL Permissions** | Delete permission verified via Security API | Fetches the PAT-authenticated user's identity via the Connection Data API, then queries the Git Repositories security namespace ACLs (`2e9eb7ed-3c0a-47d4-87c1-0ffdd275fd87`) for the repo's security token (`repoV2/{projectId}/{repoId}`). Verifies the user has the **DeleteRepository permission bit (512)** explicitly set. This also serves as a creator-ownership heuristic — in most organizations, the only individual user with explicit Allow on a repo is its creator. |

**Example safety log output from a test run:**

```
[TestHelper] Creating disposable repo: AiCodeReview-IntTest-c82ce2b1
[TestHelper] Repo created: 94981ecb-... (project: 774476a6-...)
[TestHelper] PAT identity captured: f14f85db-... (descriptor: Microsoft.IdentityModel.Claims.ClaimsIde...)
[TestHelper] Created PR #62703 in repo AiCodeReview-IntTest-c82ce2b1
[SAFETY] ✓ Marker file verified: magic string, instance token, and repo ID all match.
[SAFETY] ✓ Repo age: 0 minutes (within 120 minute limit).
[SAFETY] ✓ PAT user has Delete permission on repo (explicit: True, allow bits: 229246, deny bits: 0).
[TestHelper] All 6 safety checks passed. Deleting disposable repo AiCodeReview-IntTest-c82ce2b1...
[TestHelper] Repo soft-deleted.
[TestHelper] Repo permanently purged from recycle bin.
```

## PAT Requirements for Tests

The PAT used for integration tests requires the following **additional** scope beyond what the service itself needs:

| Scope | Permission | Used For |
|-------|-----------|----------|
| Code | **Manage** | Creating and deleting disposable test repositories |

## Run All Automated Tests

```bash
# From repo root
dotnet test --filter 'TestCategory!=Manual&FullyQualifiedName!~InspectPR&FullyQualifiedName!~CleanupTestPR'
```

## Test Categories

| Test File | Tests | Category | Description |
|-----------|-------|----------|-------------|
| `ReviewLifecycleTests.cs` | 5 | Integration | Independent, parallelizable lifecycle tests: first review, skip, re-review dedup, draft→active vote-only, reset + re-review. Each test creates its own disposable repo. |
| `ServiceIntegrationTests.cs` | 7 | Integration | History round-trips, ReviewCount, description table, tag resilience, metrics API, property read/write, description PATCH. Each test gets its own disposable repo. |
| `UnifiedDiffTests.cs` | 9 | Unit | LCS-based unified diff computation and `@@ hunk @@` header parsing. Pure unit tests — no external dependencies. |
| `MultiProviderTests.cs` | 18 | Unit | Factory tests (fallback, single, consensus, unknown type, disabled provider), consensus aggregation (overlap detection, threshold, voting, metrics), and settings binding. No external dependencies. |
| `LayeredPromptTests.cs` | 38 | Unit | Prompt assembly pipeline tests: scope filtering, rule ordering, identity/custom-instruction toggles, cache behavior, hot-reload, model adapter injection, and edge cases. |
| `ModelAdapterTests.cs` | 35 | Unit | Model adapter resolver tests: pattern matching, file loading, fallback behavior, multi-adapter scenarios, caching, pricing metadata, and cost calculation. |
| `TwoPassReviewTests.cs` | 21 | Unit | Two-pass architecture: Pass 1 summary generation, Pass 2 cross-file context injection, merge logic, and fallback when Pass 1 fails. |
| `ReviewDepthTests.cs` | 22 | Unit + Integration | Review depth modes: enum parsing, JSON serialization, Quick/Standard/Deep summary badges, deep analysis rendering, verdict override, Quick/Standard/Deep integration tests with disposable repos. |
| `ReviewProfileTests.cs` | 13 | Unit | Configurable review profile: severity thresholds, density settings, truncation limits, and defaults. |
| `TruncationConfigTests.cs` | 14 | Unit | File truncation limit configuration: default 5000 lines, configurable override, edge cases. |
| `FileClassificationTests.cs` | 24 | Unit | File type detection, binary exclusion, generated-file detection, language categorization. |
| `ThreadManagementTests.cs` | 17 | Unit | Comment thread lifecycle: deduplication, status transitions, fixed-thread resolution, attribution tags. |
| `BuildSummaryMarkdownTests.cs` | 10 | Unit | Summary thread Markdown formatting: file inventory, verdict display, observation tables. |
| `RaceConditionTests.cs` | 3 | Unit | Concurrency and thread-safety tests for shared state and parallel review operations. |
| `ResilienceTests.cs` | 7 | Unit | Azure DevOps HTTP resilience: transient 5xx retry, 429 retry with Retry-After, 408 retry, non-transient 4xx no-retry, retry exhaustion, multiple 429s, DI wiring. |
| `PassModelResolverTests.cs` | 10 | Unit | Per-pass model routing: pass-specific resolution, depth fallback, active-provider fallback, pass-over-depth priority, multi-pass routing, HasPassRouting, DI wiring, orchestrator integration. |
| `VectorStoreReviewServiceTests.cs` | 24 | Unit | Vector Store review service: file upload, vector store creation, assistant lifecycle, response parsing, cleanup, error handling. |
| `VectorStoreIntegrationTest.cs` | 1 | LiveAI | Live Vector Store integration test against real Azure OpenAI Assistants API. |
| `ModelBenchmarkTests.cs` | 8 | Benchmark | Model quality comparison: 5 individual model tests + 3 all-model comparison tests (one per depth). Runs against 10 known-bad-code issues. Produces comparison tables and cost estimates. |
| `AiQualityVerificationTests.cs` | 3 | LiveAI | Push code with **known, deliberate issues** (hardcoded secrets, SQL injection, null derefs, resource leaks) and verify the real AI flags them. Includes a fix-and-reverify cycle. Run with `--filter TestCategory=LiveAI`. |
| `LiveAiDepthModeTests.cs` | 4 | LiveAI | Real AI tests for all three review depth modes: Quick (no inline), Standard (inline + verdicts), Deep (+ cross-file analysis). Includes a depth comparison test that runs all 3 modes on the same multi-file known-bad code and compares output. Uses `PushMultipleFilesAsync` for cross-file scenarios. |
| `AiSmokeTest.cs` | 2 | Manual | Manual-only tests that call real Azure OpenAI (basic prompt + JSON mode). Run with `--filter TestCategory=Manual`. |
| `SimulationPR63643.cs` | 1 | Manual | Simulation test against a specific PR for debugging and validation. |
| `ReviewFlowIntegrationTests.cs` | 3 (Ignored) | — | Legacy monolithic lifecycle test. Replaced by `ReviewLifecycleTests.cs`. Kept for reference. |

## Test Infrastructure

| Component | Purpose |
|-----------|---------|
| `TestServiceBuilder.cs` | Shared DI container builder. `BuildWithFakeAi()` registers `FakeCodeReviewService` for deterministic tests. `BuildWithRealAi(modelOverride?)` registers the real `CodeReviewService` and optionally overrides the AI model deployment name. |
| `TestPullRequestHelper.cs` | Creates/manages disposable test repos with the 6-layer safety system (instance tracking, never-delete list, name prefix, marker file, creation recency, PAT ACL verification). Supports single-file (`PushNewCommitAsync`) and multi-file (`PushMultipleFilesAsync`) pushes for cross-file analysis testing. |
| `FakeCodeReviewService.cs` | Deterministic fake with `ResultFactory`, `VerificationResultFactory`, and `DeepAnalysisFactory` for custom per-test behavior. Supports `ModelNameOverride` for per-pass model routing tests. Produces 2 stable inline comments per file for dedup testing. |

## Running Tests

```bash
# From repo root

# All automated tests (fake AI — fast, no API cost)
dotnet test --filter 'TestCategory!=Manual&TestCategory!=LiveAI&TestCategory!=Benchmark'

# LiveAI tests only (real Azure OpenAI — costs money, slower)
dotnet test --filter TestCategory=LiveAI

# Benchmark tests only (real Azure OpenAI — runs all models × all depths)
dotnet test --filter TestCategory=Benchmark

# Everything including LiveAI (but not manual or benchmark)
dotnet test --filter 'TestCategory!=Manual&TestCategory!=Benchmark'

# AI Smoke tests (manual)
dotnet test --filter TestCategory=Manual

# Run with a different model (override via environment)
AzureOpenAI__DeploymentName=gpt-4.1 dotnet test --filter TestCategory=LiveAI
```

## Manual Test Utilities

Two tests are tagged `[Ignore]` (run explicitly by filter) for ad-hoc use:

| Test | Purpose |
|------|---------|
| `InspectPR_NoCleanup` | Creates a disposable repo + PR and leaves it alive (sets `SkipCleanupOnDispose = true`) so you can manually inspect the PR in the Azure DevOps UI. |
| `CleanupTestPR` | Manually deletes a specific repo by name/ID. Performs its own safety checks (prefix, never-delete list, marker file) before deletion. |

## Test Configuration

Tests read from `appsettings.Test.json` (gitignored). Copy `appsettings.Test.template.json` and fill in your values:

```json
{
  "AzureDevOps": {
    "Organization": "YourOrg",
    "PersonalAccessToken": "your-pat",
    "ReviewTagName": "ai-code-review",
    "AddReviewerVote": true,
    "MinReviewIntervalMinutes": 0
  },
  "AzureOpenAI": {
    "Endpoint": "https://your-resource.openai.azure.com/",
    "ApiKey": "your-key",
    "DeploymentName": "gpt-4o"
  },
  "TestSettings": {
    "Project": "YourProject"
  }
}
```

> **Note:** `MinReviewIntervalMinutes` is set to `0` in test configuration to disable rate limiting during rapid test execution. The `Repository` and `TargetBranch` settings are not used — each test creates its own disposable repo with its own branches.

## Test Roadmap / Future Enhancements

Planned improvements to the testing infrastructure:

| Enhancement | Description | Status |
|-------------|-------------|--------|
| **Multi-model comparison** | Run the same tests against different models and compare comment quality, latency, and cost. | Done |
| **Cost & latency tracking** | Instrument tests to log token usage and wall-clock time per model, enabling data-driven model selection. | Done |
| **Refactor ServiceIntegrationTests** | Migrate `ServiceIntegrationTests.cs` from its private `BuildServices()` to use `TestServiceBuilder.BuildWithFakeAi()` for consistency. | Medium |
| **Multi-file PR edge cases** | Test PRs with 10–50 changed files to verify parallel review, density threshold, and rate limiting under load. | Medium |
| **Large file density threshold** | Push a single file with 1000+ lines and only 2 changes to verify the density-based threshold correctly skips low-density files. | Medium |
| **Language-specific known-bad-code** | Expand `KnownBadCode` samples beyond C# to include TypeScript, Python, Java, and SQL to test the AI's cross-language review capability. | Low |
| **Flaky test detection** | Run `LiveAI` tests N times and track which assertions are non-deterministic due to AI variability. Adjust thresholds accordingly. | Low |
| **Pipeline integration tests** | Stand up a real Azure DevOps pipeline webhook scenario end-to-end in a disposable project. | Future |
