# Architecture & Review Logic

## Architecture Overview

```
┌─────────────────────┐       ┌──────────────────────────────────┐
│  Azure DevOps       │       │  AI Code Review Service           │
│  Pipeline / Webhook  │──────▶│  ASP.NET Core Web API (.NET 8)    │
└─────────────────────┘       │                                  │
                              │  ┌────────────────────────────┐  │
                              │  │ ReviewController            │  │
                              │  │ (CancellationToken support) │  │
                              │  └───────────┬────────────────┘  │
                              │              │                   │
                              │  ┌───────────▼────────────────┐  │
                              │  │ CodeReviewOrchestrator      │  │
                              │  │ (Two/Three-Pass + Strategy) │  │
                              │  └──┬────────┬────────┬───────┘  │
                              │     │        │        │          │
                              │  ┌──▼───┐ ┌──▼─────┐ ┌▼───────┐ │
                              │  │Azure │ │Prompt  │ │Code    │ │
                              │  │DevOps│ │Assembly│ │Review  │ │
                              │  │Svc   │ │Pipeline│ │Factory │ │
                              │  └──┬───┘ └──┬─────┘ └┬───┬───┘ │
                              │     │        │        │   │     │
                              │     │   ┌────▼────┐   │   │     │
                              │     │   │Model    │   │   │     │
                              │     │   │Adapter  │   │   │     │
                              │     │   │Resolver │   │   │     │
                              │     │   └─────────┘   │   │     │
                              │     │        ┌────────▼┐  │     │
                              │     │        │Pass    │  │     │
                              │     │        │Model   │  │     │
                              │     │        │Resolver│  │     │
                              │     │        └───┬────┘  │     │
                              │     │        ┌───▼────┐  │     │
                              │     │        │Depth   │  │     │
                              │     │        │Model   │  │     │
                              │     │        │Resolver│  │     │
                              │     │        └────┬───┘  │     │
                              │     │       ┌─────▼┐ ┌───▼────┐ │
                              │     │       │Single│ │Con-    │ │
                              │     │       │ AI   │ │sensus  │ │
                              │     │       │Review│ │Review  │ │
                              │     │       └──┬───┘ └─┬──────┘ │
                              │     │   ┌──────▼───────▼─────┐  │
                              │     │   │ VectorStore Review  │  │
                              │     │   │ (Assistants API)    │  │
                              │     │   └──────────┬─────────┘  │
                              └─────┼──────────────┼────────────┘
                                    │              │
                              ┌─────▼─────┐  ┌─────▼──────────┐
                              │ Azure     │  │ Azure OpenAI   │
                              │ DevOps    │  │ (Multiple      │
                              │ REST API  │  │  Deployments)  │
                              └───────────┘  └────────────────┘
```

**Flow:**

1. A pipeline task (or manual call) sends `POST /api/review` with the PR details. CancellationToken is propagated from the HTTP request.
2. The **Rate Limiter** checks if the PR was reviewed too recently — rejects immediately if so.
3. The **Orchestrator** fetches PR state and metadata from Azure DevOps, then decides the action:
   - **Full Review** — first review; calls the AI in two passes, posts comments, votes.
   - **Re-Review** — new commits detected; calls the AI again, deduplicates comments, resolves fixed threads via AI verification.
   - **Vote Only** — draft-to-active transition with no code changes; submits vote only.
   - **Skip** — no changes since last review; records a skip event in history.
4. The review depth (`Quick`, `Standard`, or `Deep`) determines which passes are executed. See [Review Depth Modes](#review-depth-modes).
5. **Pass 1** generates a PR-level summary (cross-file context, work item alignment) using full diff context. In **Quick** mode, the orchestrator derives a verdict from risk areas and returns immediately — no inline comments.
6. **Pass 2** (Standard and Deep only) reviews each file in parallel (controlled by `MaxParallelReviews`), injecting the Pass 1 summary for cross-file awareness. The **PromptAssemblyPipeline** builds prompts from the versioned rule catalog; the **ModelAdapterResolver** injects model-specific tuning.
7. **Pass 3** (Deep only) performs a holistic re-evaluation of the entire PR — executive summary, cross-file issues, verdict consistency check, risk level, and recommendations. Can override the verdict if inconsistent.
8. The **CodeReviewServiceFactory** creates either a single-provider or **ConsensusReviewService** (fan-out to multiple AI models, merge by agreement threshold).
9. AI responses are validated, inline comments are filtered by changed-line proximity and density thresholds, and false positives are detected.
10. Results are posted back to the PR as inline comments, a summary thread, reviewer vote, tag, metadata, and history.

---

## Review Depth Modes

The service supports three review depth modes, controlled by the `reviewDepth` field in the API request:

| Mode | Passes | Inline Comments | Summary | Use Case |
|------|--------|----------------|---------|----------|
| **Quick** ⚡ | Pass 1 only | No | Abbreviated summary with risk areas, derived verdict | Fast triage — large PRs, draft reviews, cost-sensitive runs |
| **Standard** | Pass 1 + Pass 2 | Yes | Full summary with per-file verdicts and inline comments | Default mode — balanced depth and cost |
| **Deep** 🔍 | Pass 1 + Pass 2 + Pass 3 | Yes | Full summary + deep analysis section (executive summary, cross-file issues, verdict consistency, risk level, recommendations) | Critical PRs, release branches, security-sensitive changes |

### Quick Mode

Skips Pass 2 entirely. After Pass 1 (PR summary), the orchestrator derives a verdict from the risk areas identified in the summary:
- **0 risks** → APPROVED (vote 10)
- **1–2 risks** → APPROVED WITH SUGGESTIONS (vote 5)
- **3+ risks** → NEEDS WORK (vote −5)

All file verdicts are marked `SKIPPED` and no inline comments are posted. This mode is the fastest and cheapest option.

### Standard Mode (Default)

The default behavior — Pass 1 (PR summary) followed by Pass 2 (per-file parallel review). This is the two-pass architecture described below.

### Deep Mode

Runs all of Standard mode, then adds **Pass 3**: a holistic re-evaluation that:
1. **Executive Summary** — High-level assessment of the entire PR
2. **Cross-File Issues** — Detects issues that span multiple files (e.g., interface changes without updating all callers)
3. **Verdict Consistency** — Checks whether the per-file verdicts collectively justify the overall verdict; can override the verdict if inconsistent
4. **Risk Level** — Overall risk assessment (Low / Medium / High / Critical)
5. **Recommendations** — Actionable next steps for the PR author

The deep analysis section is appended to the PR summary with a 🔍 badge. If the verdict consistency check finds the overall verdict is inconsistent with the per-file results, the verdict is automatically overridden.

---

## Per-Pass Model Routing

While **depth modes** control *which passes* execute, **per-pass model routing** controls *which AI model* handles each pass. This enables cost optimization by assigning lightweight models to simple tasks and reasoning models to complex analysis.

### How It Works

The `PassModelResolver` sits between the orchestrator and the `DepthModelResolver`, resolving the AI service for each review pass independently:

```
HandleReviewAsync()
  ├── Pass 1 (PR Summary)     → GetServiceForPass(PrSummary, depth)     → gpt-4o-mini
  ├── Pass 2 (Per-File)        → GetServiceForPass(PerFileReview, depth) → gpt-4o
  ├── Pass 3 (Deep Analysis)   → GetServiceForPass(DeepReview, depth)    → o4-mini
  └── Thread Verification      → GetServiceForPass(ThreadVerification, depth) → gpt-4o-mini
```

Each pass can use a different model. The `PassModels` dictionary in the review result tracks which model handled each pass for auditability.

### Resolution Order

1. **PassRouting** — If `AiProvider:PassRouting` has a mapping for the current pass, use that provider.
2. **DepthModels** — Fall back to the depth-based model if no pass-specific routing exists.
3. **ActiveProvider** — Use the default provider if neither pass nor depth routing is configured.

### Configuration

See [Configuration → Per-Pass Model Routing](configuration.md#per-pass-model-routing-passrouting) for the full configuration reference and examples.

---

## Review Strategies

The `reviewStrategy` field controls how Pass 2 (per-file review) is executed:

| Strategy | Description |
|----------|-------------|
| **FileByFile** (default) | Each file is reviewed individually via Chat Completions. The orchestrator sends each file's diff with the Pass 1 summary as context. Best for small-to-medium PRs. |
| **Vector** | All changed files are uploaded to an Azure OpenAI Vector Store, an Assistant is created with `file_search` capability, and the entire PR is reviewed in a single Assistants API run. Best for medium-to-large PRs with cross-file dependencies. |
| **Auto** | Smart selection based on file count. If the number of changed files exceeds `Assistants:AutoThreshold` (default: 5), uses Vector; otherwise uses FileByFile. |

### Vector Store Review Flow

When the Vector strategy is used, the `VectorStoreReviewService` follows a 7-step process:

```
1. Upload all changed files to Azure OpenAI Files API (parallel, up to MaxParallelUploads)
2. Create a Vector Store containing all uploaded files
3. Poll until the Vector Store indexing completes
4. Create an Assistant with file_search tool and the code review prompt
5. Create a thread with the review request and start a run
6. Poll until the Assistant run completes
7. Parse the structured JSON response and clean up (delete assistant, vector store, files)
```

The service includes built-in retry logic for rate-limit errors (HTTP 429) with exponential backoff up to 5 retries. All resources (assistant, vector store, files) are cleaned up after the review completes, even on failure.

**When to use each strategy:**

| Scenario | Recommended Strategy |
|----------|---------------------|
| Small PR (1–5 files) | FileByFile |
| Medium PR (5–15 files) | Auto or Vector |
| Large PR (15+ files) | Vector |
| Cross-file refactoring | Vector |
| Quick depth mode | FileByFile (Vector is skipped) |

---

## Two-Pass Review Architecture

When a full review or re-review is triggered, the orchestrator uses a two-pass architecture for higher-quality results:

### Pass 1: PR Summary (Cross-File Context)

- Sends the complete PR diff (all files) to the AI in a single call
- Generates a PR-level summary: overall assessment, cross-file dependencies, work item alignment
- Uses the `pass-1` prompt scope (lightweight rules, no per-file verdicts)
- Result is cached and injected as context into every Pass 2 call

### Pass 2: Per-File Review (Parallel)

- Each file is reviewed individually in parallel (up to `MaxParallelReviews` concurrent)
- The Pass 1 summary is injected into each file's prompt, giving the AI cross-file awareness
- Uses the `single-file` prompt scope (full rule set, detailed verdicts)
- Results are merged and deduplicated before posting

This architecture provides better accuracy than single-pass batch review because:
1. Each file gets the AI's full attention (no context window competition)
2. Cross-file context is provided via the Pass 1 summary
3. Parallel execution maintains performance despite reviewing files individually

---

## Review Decision Logic

The orchestrator evaluates the PR state and metadata to determine the appropriate action:

```
┌──────────────────────────────┐
│  Rate Limiter Check          │
│  (in-memory, no API calls)   │
│  → RateLimited               │
└──────────┬───────────────────┘
           │ allowed
┌──────────▼───────────────────┐
│  Has Previous Review?        │
│  (PR properties metadata)    │
│  No → FullReview             │
└──────────┬───────────────────┘
           │ yes
┌──────────▼───────────────────┐
│  Source Commit Changed?      │
│  Yes → ReReview              │
│  (deduplicates comments)     │
└──────────┬───────────────────┘
           │ no
┌──────────▼───────────────────┐
│  Draft → Active Transition?  │
│  (WasDraft=true, IsDraft=    │
│   false, vote not submitted) │
│  Yes → VoteOnly              │
└──────────┬───────────────────┘
           │ no
┌──────────▼───────────────────┐
│  No changes → Skip           │
│  (records skip in history)   │
└──────────────────────────────┘
```

| Action | AI Called | Comments Posted | Vote Cast | History Entry |
|--------|----------|-----------------|-----------|---------------|
| **Full Review** | Yes | Yes | Yes (non-draft) | Yes |
| **Re-Review** | Yes | Yes (deduped) | Yes (non-draft) | Yes |
| **Vote Only** | No | No | Yes | Yes |
| **Skip** | No | No | No | Yes |
| **Rate Limited** | No | No | No | No |

---

## Incremental (Delta) Review

When a re-review is triggered (new commits pushed since the last review), the orchestrator can perform an **incremental review** — only analyzing files that actually changed between the last-reviewed iteration and the current iteration. Unchanged files have their prior review results carried forward.

### How It Works

```
Re-Review Detected
      │
      ▼
┌─────────────────────────────────┐
│  Compare iterations via ADO API │
│  (baseIteration → currentIter)  │
└────────────┬────────────────────┘
             │
      ┌──────▼──────┐
      │ Changed < N │───▶ Delta Review
      │ (subset)    │     (only changed files sent to AI)
      └──────┬──────┘
             │ all changed or API error
             ▼
      Full Re-Review
      (all files sent to AI)
```

1. **Iteration comparison** — The orchestrator calls the Azure DevOps iteration changes API (`GET pullrequests/{id}/iterations/{target}/changes?baseIteration={base}`) to get the set of files modified between the last-reviewed and current iterations.
2. **File partitioning** — Reviewable files are split into:
   - **Delta files** — changed since last review → sent to AI for review
   - **Carried-forward files** — unchanged → prior review results preserved (existing inline comments remain active on the PR)
3. **Delta summary** — The PR summary includes an "Incremental Review" section showing file counts, iteration range, and estimated token savings.
4. **Token savings** — Estimated as the proportional share of tokens that would have been used to review the carried-forward files.

### Fallback to Full Review

The incremental review gracefully falls back to a full re-review when:
- The iteration changes API returns no results (API error or edge case)
- All reviewable files changed since the last review
- This is the first review (no prior iteration to compare against)

### Response & History

The `ReviewResponse` includes a `DeltaInfo` object (when applicable):

| Field | Description |
|-------|-------------|
| `IsDeltaReview` | Whether this was an incremental review |
| `BaseIteration` | The last-reviewed iteration (comparison base) |
| `CurrentIteration` | The current iteration being reviewed |
| `TotalFilesInPr` | Total files in the PR |
| `DeltaFilesReviewed` | Files changed and sent to AI |
| `CarriedForwardFiles` | Files unchanged since last review |
| `EstimatedTokenSavings` | Approximate tokens saved by not re-reviewing unchanged files |

The `ReviewHistoryEntry` also captures delta metrics: `IsDeltaReview`, `DeltaFilesReviewed`, and `CarriedForwardFiles`.

---

## Review History & Tracking

Every review action generates a `ReviewHistoryEntry` stored in two places:

1. **PR Properties** (source of truth) — Stored as JSON under `AiCodeReview.ReviewHistory`. Resilient to manual tag deletion. Used by the metrics API.
2. **PR Description** (visual convenience) — Appended as a Markdown table between `<!-- AI-REVIEW-HISTORY-START -->` and `<!-- AI-REVIEW-HISTORY-END -->` HTML comment markers.

**Example PR description table:**

| Review # | Date (UTC) | Action | Verdict | Commit | Iteration | Scope |
|----------|-----------|--------|---------|--------|-----------|-------|
| 1 | 2026-02-13 22:15:00 UTC | Full Review | APPROVED WITH SUGGESTIONS | `abc1234` | Iter 1 | 37 files, 4 comments |
| 2 | 2026-02-14 00:50:18 UTC | Skipped | No Changes | `abc1234` | Iter 1 | 0 files, 0 comments |

Each history entry also captures AI metrics (when an AI call was made):

- **ModelName** — The model deployment used (e.g., `gpt-4o-2024-08-06`).
- **Token Counts** — Prompt tokens, completion tokens, total tokens (aggregated across all passes).
- **Review Depth** — The depth mode used (`Quick`, `Standard`, or `Deep`).
- **Timing** — AI response time (`aiDurationMs`) and total end-to-end duration (`totalDurationMs`).
- **Estimated Cost** — Cost in USD calculated from model pricing metadata and actual token usage.

---

## Rate Limiting

The service implements **two tiers** of rate limiting to protect against both misuse and Azure OpenAI API throttling.

### PR-Level Cooldown

Prevents the same PR from being reviewed too frequently. This protects against misuse and unnecessary AI API costs.

**How it works:**

- Uses a `ConcurrentDictionary<string, DateTime>` keyed by `{organization}:{project}:{repo}:{prId}` (case-insensitive).
- Checked **first**, before any Azure DevOps or Azure OpenAI API calls.
- After any action (Full Review, Re-Review, Vote Only, Skip), the PR's timestamp is recorded.
- Stale entries (older than 24 hours) are automatically evicted.

**Configuration:**

| Setting | Default | Description |
|---------|---------|-------------|
| `MinReviewIntervalMinutes` | `5` | Minutes to wait between reviews on the same PR. Set to `0` to disable. |

> **Note:** The rate limiter is in-memory and resets when the service restarts. This is by design for simplicity; if you need persistent rate limiting across restarts or multiple instances, consider using Redis or a similar distributed cache.

### API-Level Rate-Limit Handling (429 Retry)

When Azure OpenAI returns an HTTP 429 (Too Many Requests), the service automatically retries with intelligent back-off:

**Retry delay resolution (in priority order):**

1. **`Retry-After` HTTP header** — Parsed from the raw response via `ClientResultException.GetRawResponse().Headers`. Supports both integer seconds and RFC 1123 HTTP-date formats.
2. **Exception message regex** — Falls back to parsing `"retry after N seconds"` from the error message text.
3. **Default 30 seconds** — Used when neither header nor message contains retry information.

The computed delay is clamped to `[1, 120]` seconds and a 5-second buffer is added above the `Retry-After` value to reduce the chance of an immediate second 429.

**Retry budget:**

| Parameter | Value | Description |
|-----------|-------|-------------|
| Max retries | 5 | Per API call (up from previous 3) |
| Max cumulative wait | 5 minutes | Stops retrying if total delay exceeds this |
| Default delay | 30 seconds | When no `Retry-After` data is available |
| Max per-retry delay | 120 seconds | Clamp to prevent indefinitely long waits |
| Buffer | 5 seconds | Added above `Retry-After` to avoid re-throttling |

**Global cooldown signal (`GlobalRateLimitSignal`):**

When **any** concurrent caller receives a 429, a singleton `IGlobalRateLimitSignal` broadcasts a global cooldown to **all** callers:

- All in-flight file reviews pause until the cooldown expires before making their next API call.
- Lock-free implementation via `Interlocked.CompareExchange` — if two 429s arrive simultaneously, the longer cooldown wins.
- The orchestrator checks `WaitIfCoolingDownAsync()` before each `ReviewFileAsync` call in Pass 2.

This prevents the thundering-herd problem where multiple concurrent file reviews all independently retry against a rate-limited endpoint.

---

## RPM-Aware Throttling & Cost Estimation

### Azure DevOps HTTP Resilience (Polly)

All Azure DevOps REST API calls go through `Microsoft.Extensions.Http.Resilience` (Polly v8), configured via `AddStandardResilienceHandler` in `Program.cs`. This provides automatic resilience for transient network failures:

| Layer | Configuration | Description |
|-------|---------------|-------------|
| **Retry** | 5 attempts, exponential backoff (2s base) with jitter | Retries on 408, 429, 5xx, `HttpRequestException`, and `TimeoutRejectedException`. Respects `Retry-After` headers from 429 responses. |
| **Circuit Breaker** | 90% failure ratio, minimum throughput 5 requests, 15s break | Opens circuit after sustained failures to prevent cascading load on a degraded Azure DevOps instance. |
| **Total Timeout** | 3 minutes | Outer timeout spanning all retry attempts — prevents indefinite waits. |
| **Attempt Timeout** | 60 seconds | Per-attempt timeout — cancels individual requests that hang. |

This layer is transparent to application code — `AzureDevOpsService` methods (`Get/Post/SendAsync`) automatically benefit from retry and circuit-breaking without any code changes.

### RPM-Aware Throttling

During Pass 2 (per-file review), the orchestrator automatically throttles API calls based on the model's RPM (requests per minute) limit from the model adapter metadata:

1. **Automatic delay calculation** — The minimum interval between API calls is calculated as `60 / RPM` seconds (e.g., 150 RPM → 400ms between calls).
2. **Lock-free ticket system** — Each file review acquires a "ticket number" and waits the appropriate multiple of the delay interval before proceeding. This prevents thundering-herd scenarios without using locks.
3. **Concurrency clamping** — `MaxParallelReviews` is automatically clamped to `min(configured, RPM)` so the service never launches more concurrent reviews than the model can handle.
4. **RPM capacity warning** — Before Pass 2 begins, if the estimated number of API calls exceeds 80% of the model's RPM, a warning is logged to help identify potential throttling.

This is driven entirely by the `requestsPerMinute` field in `model-adapters.json`. If no RPM data is configured, no throttling is applied.

### Cost Estimation

Every review response includes an `EstimatedCost` field (decimal, USD) when pricing data is available in the model adapter:

- **Calculation** — `(promptTokens / 1M × inputCostPer1MTokens) + (completionTokens / 1M × outputCostPer1MTokens)`
- **History tracking** — Each `ReviewHistoryEntry` also captures the estimated cost, enabling cost-over-time analysis via the metrics API.
- **Pricing source** — Configured per adapter in `model-adapters.json` via `inputCostPer1MTokens` and `outputCostPer1MTokens`.
- **Null when unavailable** — If pricing data is missing from the adapter, the field is omitted from the JSON response.

**Example cost per review (typical 10-file PR at Standard depth):**

| Model | ~Prompt Tokens | ~Completion Tokens | Estimated Cost |
|-------|---------------:|------------------:|---------------:|
| gpt-4o-mini | 25,000 | 5,000 | ~$0.007 |
| gpt-4o | 25,000 | 5,000 | ~$0.113 |
| o4-mini | 25,000 | 5,000 | ~$0.050 |
