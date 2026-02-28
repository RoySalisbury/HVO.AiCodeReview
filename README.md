# AI Code Review Service

A centralized, AI-powered code review service for Azure DevOps pull requests. The service analyzes PR diffs using Azure OpenAI with configurable model deployments (GPT-4o, GPT-4o-mini, o4-mini, o3-mini, GPT-5-mini, etc.), posts inline comments and a summary thread, adds a reviewer vote, and tracks full review history with cost estimation — all driven by a single HTTP API call.

---

## Table of Contents

- [Features](#features)
- [Architecture](#architecture)
- [Prerequisites](#prerequisites)
- [Configuration](#configuration)
  - [Application Settings](#application-settings)
  - [Multi-Provider AI Configuration](#multi-provider-ai-configuration)
  - [Depth-Specific Model Routing](#depth-specific-model-routing)
  - [Environment Variables](#environment-variables)
  - [Custom Review Instructions](#custom-review-instructions)
  - [Prompt Layers](#prompt-layers-in-order)
  - [Prompt Scopes](#prompt-scopes)
  - [Rule Catalog](#rule-catalog-review-rulesjson)
  - [Model Adapters](#model-adapters)
  - [Assistants API / Vector Store Settings](#assistants-api--vector-store-settings)
  - [Legacy Custom Instructions](#legacy-custom-instructions)
- [Running the Service](#running-the-service)
  - [Local Development](#local-development)
  - [Docker / Container](#docker--container)
  - [Production Deployment](#production-deployment)
- [API Endpoints](#api-endpoints)
  - [POST /api/review](#post-apireview)
  - [GET /api/review/metrics](#get-apireviewmetrics)
  - [GET /api/review/health](#get-apireviewhealth)
- [Azure DevOps Pipeline Integration](#azure-devops-pipeline-integration)
- [Review Depth Modes](#review-depth-modes)
- [Review Strategies](#review-strategies)
- [Two-Pass Review Architecture](#two-pass-review-architecture)
- [Review Decision Logic](#review-decision-logic)
- [Review History & Tracking](#review-history--tracking)
- [Rate Limiting](#rate-limiting)
  - [PR-Level Cooldown](#pr-level-cooldown)
  - [API-Level Rate-Limit Handling (429 Retry)](#api-level-rate-limit-handling-429-retry)
- [RPM-Aware Throttling & Cost Estimation](#rpm-aware-throttling--cost-estimation)
- [Model Benchmarks & Selection](#model-benchmarks--selection)
- [Project Structure](#project-structure)
- [Testing](#testing)
  - [Disposable Test Repositories](#disposable-test-repositories)
  - [6-Layer Safety System](#6-layer-safety-system)
  - [PAT Requirements for Tests](#pat-requirements-for-tests)
  - [Run All Automated Tests](#run-all-automated-tests)
  - [Test Categories](#test-categories)
  - [Test Infrastructure](#test-infrastructure)
  - [Running Tests](#running-tests)
  - [Manual Test Utilities](#manual-test-utilities)
- [Test Roadmap / Future Enhancements](#test-roadmap--future-enhancements)

---

## Features

| Feature | Description |
|---------|-------------|
| **AI-Powered Review** | Uses Azure OpenAI with configurable model deployments to analyze diffs and produce structured reviews with per-file verdicts, inline comments, and observations. |
| **Two-Pass Architecture** | Pass 1 generates a cross-file PR summary for context. Pass 2 reviews each file individually with that context injected, improving accuracy for multi-file changes. |
| **Review Depth Modes** | Three review depths — **Quick** (Pass 1 only, no inline comments), **Standard** (Pass 1 + Pass 2, default), and **Deep** (Pass 1 + Pass 2 + Pass 3 holistic re-evaluation with cross-file issue detection, verdict consistency check, and executive summary). |
| **Depth-Specific Model Routing** | Each review depth can target a different AI model via `DepthModels` config — e.g., Quick → gpt-4o-mini (fast/cheap), Deep → o4-mini (reasoning). The `DepthModelResolver` handles routing at runtime. |
| **Review Strategies** | Three Pass 2 strategies — **FileByFile** (default per-file Chat Completions), **Vector** (Assistants API + Vector Store for holistic review), and **Auto** (automatically selects based on file count threshold). |
| **Vector Store Review** | Uploads all changed files to an Azure OpenAI Vector Store, creates an Assistant with `file_search`, and reviews the entire PR in a single run. Best for medium-to-large PRs with cross-file dependencies. |
| **Reasoning Model Support** | Full compatibility with o-series models (o1, o3-mini, o4-mini) — automatic `max_completion_tokens` parameter, temperature bypass, and JSON mode adaptation. |
| **Layered Prompt Architecture** | System prompts are assembled from a versioned rule catalog (`review-rules.json`) with scoped rules, priorities, hot-reload, and per-scope identity/custom-instruction toggles. |
| **Per-Model Adapter** | Model-specific preambles and metadata (`ModelAdapterResolver`) tune each AI model's behavior — verbosity, severity calibration, output format hints, pricing, rate limits, and context windows per deployment. |
| **RPM-Aware Throttling** | Automatic per-call delay in Pass 2 based on model RPM limits. Lock-free ticket-based system with concurrency clamping to prevent rate-limit errors. |
| **Cost Estimation** | Every review response includes an `EstimatedCost` (USD) calculated from model-specific pricing metadata and actual token usage. Also tracked in review history. |
| **Inline PR Comments** | Posts targeted inline comments on specific lines with severity levels (Bug, Security, Concern, Performance, Suggestion). |
| **Summary Thread** | Posts a comprehensive review summary as a PR comment thread with file inventory, per-file reviews, observations, and overall verdict. |
| **Reviewer Vote** | Automatically adds itself as a reviewer with a vote (Approved / Approved with Suggestions / Waiting for Author / Rejected) on non-draft PRs. |
| **Smart Re-Review** | Detects new commits and only re-reviews when code has actually changed. Deduplicates inline comments to avoid repeating feedback. |
| **Draft PR Awareness** | Reviews draft PRs without voting. Automatically submits a vote when a draft transitions to active (vote-only flow). |
| **Review History** | Full history stored in PR properties (source of truth) and appended as a visible table in the PR description. History is automatically pruned to stay within Azure DevOps property size limits. |
| **AI Metrics** | Captures token counts (prompt, completion, total), model name, AI latency, estimated cost, and total review duration per review. |
| **Metrics API** | Dedicated endpoint returns full review history and aggregated AI metrics for any PR. |
| **Multi-Provider Consensus** | Fan-out to multiple AI models; only comments that meet the agreement threshold are posted. Each comment shows which models flagged it. |
| **Configurable Review Profile** | Adjust severity thresholds, comment density limits, file truncation size, and parallel review concurrency via `appsettings.json`. |
| **Rate Limiting** | Two-tier rate limiting: (1) In-memory PR-level cooldown prevents the same PR from being reviewed too frequently; (2) API-level retry with `Retry-After` header parsing, global cooldown signal, and up to 5 retries with a 5-minute cumulative cap for Azure OpenAI 429 responses. |
| **Configurable Prompt** | Domain-specific review instructions loaded from a JSON file, injected between the fixed identity preamble and response format rules. |
| **Health Check** | `/api/review/health` verifies Azure DevOps connectivity and reports degraded status if dependencies are unreachable. |
| **CancellationToken Support** | API endpoints propagate cancellation tokens so disconnected callers don't continue consuming AI tokens. |
| **Tag / Label** | Applies a decorative `ai-code-review` label to reviewed PRs for easy filtering in the PR list. |
| **Swagger UI** | Interactive API documentation available at `/swagger` in development mode. |
| **Model Benchmarks** | Built-in benchmark test suite with 10 known-bad-code scenarios to compare model quality, latency, and cost across all depths. |

---

## Architecture

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
                              │     │        │Depth    │  │     │
                              │     │        │Model    │  │     │
                              │     │        │Resolver │  │     │
                              │     │        └────┬────┘  │     │
                              │     │       ┌─────▼┐ ┌────▼───┐ │
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

## Prerequisites

| Requirement | Details |
|-------------|---------|
| **.NET 8 SDK** | [Download](https://dotnet.microsoft.com/download/dotnet/8.0). Required to build and run. |
| **Azure OpenAI Resource** | An Azure OpenAI resource with a deployed model (e.g., GPT-4o, GPT-4, GPT-5). You need the endpoint URL, API key, and deployment name. |
| **Azure DevOps PAT** | A Personal Access Token for the Azure DevOps organization. Required permissions: **Code** (Read), **Pull Request Threads** (Read & Write), **Identity** (Read). |
| **Azure DevOps Organization** | The organization name from your Azure DevOps URL (`https://dev.azure.com/{Organization}`). |

### Azure DevOps PAT Scopes

The PAT used by the service must have the following scopes:

| Scope | Permission | Used For |
|-------|-----------|----------|
| Code | Read | Reading PR details, iterations, file diffs |
| Code | Write | Adding labels/tags to PRs |
| Pull Request Threads | Read & Write | Posting inline comments, summary threads |
| Identity | Read | Auto-discovering the service account's identity GUID |
| Graph | Read | Resolving reviewer identity |

---

## Configuration

All configuration is read from the standard ASP.NET Core configuration system, supporting `appsettings.json`, `appsettings.{Environment}.json`, environment variables, and command-line arguments.

### Application Settings

**`appsettings.json`** (template — no secrets):

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning"
    }
  },
  "AllowedHosts": "*",
  "AzureDevOps": {
    "Organization": "",
    "PersonalAccessToken": "",
    "ServiceAccountIdentityId": "",
    "ReviewTagName": "ai-code-review",
    "AddReviewerVote": true,
    "MinReviewIntervalMinutes": 5
  },
  "AzureOpenAI": {
    "Endpoint": "",
    "ApiKey": "",
    "DeploymentName": "",
    "CustomInstructionsPath": "custom-instructions.json"
  }
}
```

#### AzureDevOps Section

| Setting | Type | Default | Description |
|---------|------|---------|-------------|
| `Organization` | string | `""` | **(Required)** Azure DevOps organization name. |
| `PersonalAccessToken` | string | `""` | **(Required)** PAT with the scopes listed above. |
| `ServiceAccountIdentityId` | string | `""` | GUID of the PAT owner. If empty, auto-discovered at first use via the connection data API. |
| `ReviewTagName` | string | `"ai-code-review"` | Label applied to PRs after review. Purely decorative. |
| `AddReviewerVote` | bool | `true` | Whether to add the service as a reviewer with a vote on non-draft PRs. |
| `MinReviewIntervalMinutes` | int | `5` | Cooldown between reviews on the same PR. Set to `0` to disable rate limiting. |

#### AzureOpenAI Section (Legacy — Backward Compatible)

> **Note:** The `AzureOpenAI` section still works as a fallback. If no `AiProvider:Providers` are configured,
> the service reads from this section. For new setups, prefer the `AiProvider` section below.

| Setting | Type | Default | Description |
|---------|------|---------|-------------|
| `Endpoint` | string | `""` | **(Required)** Azure OpenAI endpoint URL (e.g., `https://your-resource.openai.azure.com/`). |
| `ApiKey` | string | `""` | **(Required)** Azure OpenAI API key. |
| `DeploymentName` | string | `""` | **(Required)** The model deployment name (e.g., `gpt-4o`, `gpt-4`, `gpt-5`). Any chat-completion model that supports structured JSON output will work. |
| `CustomInstructionsPath` | string | `"custom-instructions.json"` | Path to optional custom review instructions file. Relative to app directory. |

### Multi-Provider AI Configuration

The `AiProvider` section supports pluggable AI backends. You can run a single provider or multiple
providers in **consensus mode** where the same code is reviewed by several models and only comments
that meet the agreement threshold are retained.

```json
{
  "AiProvider": {
    "MaxParallelReviews": 5,
    "MaxInputLinesPerFile": 5000,
    "Mode": "single",
    "ActiveProvider": "azure-openai",
    "ConsensusThreshold": 2,
    "DepthModels": {
      "Quick": "azure-openai-mini",
      "Standard": "azure-openai-mini",
      "Deep": "azure-openai-o4-mini"
    },
    "Providers": {
      "azure-openai": {
        "Type": "azure-openai",
        "DisplayName": "Azure OpenAI (gpt-4o)",
        "Endpoint": "https://your-resource.openai.azure.com/",
        "ApiKey": "your-key",
        "Model": "gpt-4o",
        "Enabled": true
      }
    }
  }
}
```

| Setting | Type | Default | Description |
|---------|------|---------|-------------|
| `MaxParallelReviews` | int | `5` | Maximum concurrent per-file AI review calls. |
| `MaxInputLinesPerFile` | int | `5000` | Maximum number of source lines sent to AI per file. Files exceeding this are truncated. |
| `Mode` | string | `"single"` | `"single"` = use `ActiveProvider` only. `"consensus"` = fan out to ALL enabled providers and merge. |
| `ActiveProvider` | string | `"azure-openai"` | Which provider key to use when Mode = single. |
| `ConsensusThreshold` | int | `2` | In consensus mode, minimum providers that must flag a comment for it to be kept. |
| `DepthModels` | object | `{}` | Maps review depth → provider key. See [Depth-Specific Model Routing](#depth-specific-model-routing). |

#### Provider Config

Each entry under `Providers` has:

| Field | Description |
|-------|-------------|
| `Type` | Provider type: `azure-openai`. More planned: `github-copilot`, `openai`, `local`. |
| `DisplayName` | Human-readable label for logs and comment attribution. |
| `Endpoint` | API endpoint URL. |
| `ApiKey` | API key or token. |
| `Model` | Model / deployment name. |
| `CustomInstructionsPath` | Optional path to custom review instructions JSON. |
| `Enabled` | Set `false` to temporarily disable without removing config. |

#### Example: Consensus Mode with Two Models

```json
{
  "AiProvider": {
    "Mode": "consensus",
    "ConsensusThreshold": 2,
    "Providers": {
      "azure-openai-4o": {
        "Type": "azure-openai",
        "DisplayName": "gpt-4o",
        "Endpoint": "https://your-resource.openai.azure.com/",
        "ApiKey": "your-key",
        "Model": "gpt-4o",
        "Enabled": true
      },
      "azure-openai-4.1": {
        "Type": "azure-openai",
        "DisplayName": "gpt-4.1",
        "Endpoint": "https://your-resource.openai.azure.com/",
        "ApiKey": "your-key",
        "Model": "gpt-4.1",
        "Enabled": true
      }
    }
  }
}
```

Both models review every file in parallel. Only comments that **both** flag (same file, overlapping line ranges) are posted. Each comment is prefixed with `[gpt-4o+gpt-4.1]` for attribution.

### Depth-Specific Model Routing

The `DepthModels` section under `AiProvider` maps each review depth to a specific provider key, allowing cost-optimized model selection per depth:

```json
{
  "AiProvider": {
    "ActiveProvider": "azure-openai",
    "DepthModels": {
      "Quick": "azure-openai-mini",
      "Standard": "azure-openai-mini",
      "Deep": "azure-openai-o4-mini"
    },
    "Providers": {
      "azure-openai": {
        "Type": "azure-openai",
        "DisplayName": "Azure OpenAI (gpt-4o)",
        "Endpoint": "https://your-resource.openai.azure.com/",
        "ApiKey": "your-key",
        "Model": "gpt-4o",
        "Enabled": true
      },
      "azure-openai-mini": {
        "Type": "azure-openai",
        "DisplayName": "Azure OpenAI (gpt-4o-mini)",
        "Endpoint": "https://your-resource.openai.azure.com/",
        "ApiKey": "your-key",
        "Model": "gpt-4o-mini",
        "Enabled": true
      },
      "azure-openai-o4-mini": {
        "Type": "azure-openai",
        "DisplayName": "Azure OpenAI (o4-mini)",
        "Endpoint": "https://your-resource.openai.azure.com/",
        "ApiKey": "your-key",
        "Model": "o4-mini",
        "Enabled": true
      }
    }
  }
}
```

**How it works:**

1. The `DepthModelResolver` reads `DepthModels` at startup and pre-builds an `ICodeReviewService` per depth.
2. When a review is requested, the orchestrator looks up the depth → provider mapping.
3. If no mapping exists for the requested depth, the `ActiveProvider` is used as fallback.
4. Invalid depth names or disabled/missing providers are logged as warnings and fall back to the default.

| Depth | Recommended Model | Rationale |
|-------|-------------------|-----------|
| **Quick** | gpt-4o-mini | Fastest + cheapest. Quick mode only generates a PR summary (no file reviews), so a lightweight model is ideal. |
| **Standard** | gpt-4o-mini | Best cost/quality balance for per-file reviews. High throughput (4,990 RPM) handles large PRs quickly. |
| **Deep** | o4-mini | Reasoning model provides deeper analysis for cross-file issues and verdict consistency. Worth the higher cost for critical PRs. |

See [Model Benchmarks & Selection](#model-benchmarks--selection) for the data behind these recommendations.

#### Adding a New Provider Type

1. Create a class implementing `ICodeReviewService` (see `AzureOpenAiReviewService` for reference)
2. Add a case to the `switch` in `CodeReviewServiceFactory.CreateProvider()`
3. Add a config block under `AiProvider:Providers` with the new `Type` value

### Environment Variables

All settings can be provided via environment variables using the `__` (double underscore) separator:

```bash
# Legacy single-provider (still works)
export AzureOpenAI__Endpoint="https://my-resource.openai.azure.com/"
export AzureOpenAI__ApiKey="your-api-key"
export AzureOpenAI__DeploymentName="gpt-4o"

# New multi-provider config
export AiProvider__Mode="single"
export AiProvider__ActiveProvider="azure-openai"
export AiProvider__Providers__azure-openai__Type="azure-openai"
export AiProvider__Providers__azure-openai__Endpoint="https://my-resource.openai.azure.com/"
export AiProvider__Providers__azure-openai__ApiKey="your-api-key"
export AiProvider__Providers__azure-openai__Model="gpt-4o"

# Azure DevOps
export AzureDevOps__Organization="MyOrg"
export AzureDevOps__PersonalAccessToken="your-pat-here"
export AzureDevOps__MinReviewIntervalMinutes="10"
```

This is the recommended approach for production / CI environments — avoid storing secrets in config files.

### Custom Review Instructions

The AI system prompt is assembled by the **Prompt Assembly Pipeline** from a layered rule catalog:

#### Prompt Layers (in order)

| Layer | Source | Description |
|-------|--------|-------------|
| 1. Identity | `review-rules.json` → `identity` | Establishes the AI as a senior code reviewer. Shared across scopes that opt in. |
| 1½. Model Adapter | `ModelAdapterResolver` | Per-model tuning preamble (e.g., adjusting verbosity for GPT-4o vs GPT-4.1). Loaded from `model-adapters.json` catalog file. |
| 2. Custom Instructions | `custom-instructions.json` | Domain-specific review guidance (per-provider). Injected for scopes that opt in. |
| 3. Scope Preamble | `review-rules.json` → `scopes.{scope}.preamble` | Context and JSON schema for the specific prompt scope. |
| 4. Numbered Rules | `review-rules.json` → `rules[]` | Filtered by scope, sorted by priority, enabled-only. Numbered automatically. |

#### Prompt Scopes

| Scope | Used For |
|-------|----------|
| `pass-1` | PR-level summary generation (cross-file context) |
| `single-file` | Per-file AI review (Pass 2) |
| `batch` | Legacy batch review (backward compatible) |
| `thread-verification` | Verifying whether prior comments are still valid |

#### Rule Catalog (`review-rules.json`)

The rule catalog is a versioned JSON file with hot-reload support (FileSystemWatcher-based debounce):

```json
{
  "version": "1.0",
  "identity": "You are a senior code reviewer...",
  "scopes": {
    "single-file": {
      "preamble": "Review this single file...",
      "rulesHeader": "## Review Rules",
      "includeIdentity": true,
      "includeCustomInstructions": true
    }
  },
  "rules": [
    {
      "id": "SEC-001",
      "scope": "single-file",
      "category": "Security",
      "priority": 1,
      "text": "Flag hardcoded secrets, API keys, or connection strings.",
      "enabled": true
    }
  ]
}
```

Rules can be toggled, reordered, or scoped without restarting the service. The pipeline caches assembled prompts and invalidates on file change.

#### Model Adapters

The `ModelAdapterResolver` loads model-specific preambles, capabilities, and metadata from a single `model-adapters.json` catalog file:

```json
{
  "adapters": [
    {
      "name": "gpt-4o-mini-tuning",
      "modelPattern": "gpt-4o-mini",
      "promptStyle": "imperative",
      "preamble": "Be concise. Skip obvious LGTM observations.",
      "isReasoningModel": false,
      "contextWindowSize": 128000,
      "maxOutputTokensModel": 16384,
      "inputCostPer1MTokens": 0.15,
      "outputCostPer1MTokens": 0.60,
      "requestsPerMinute": 4990,
      "tokensPerMinute": 499000,
      "quirks": ["May miss subtle cross-file issues"]
    },
    {
      "name": "o4-mini-tuning",
      "modelPattern": "o4-mini",
      "promptStyle": "imperative",
      "preamble": "Use your reasoning capabilities for deep analysis.",
      "isReasoningModel": true,
      "contextWindowSize": 200000,
      "maxOutputTokensModel": 100000,
      "inputCostPer1MTokens": 1.10,
      "outputCostPer1MTokens": 4.40,
      "requestsPerMinute": 150,
      "tokensPerMinute": 150000,
      "quirks": ["Does not support Temperature or JSON response format"]
    }
  ]
}
```

Adapters are evaluated in order; the first adapter whose `modelPattern` regex matches (case-insensitive) wins. If no adapter matches, a built-in default is used. The file is resolved from the application base directory (`model-adapters.json` next to the executable).

**Adapter Fields:**

| Field | Type | Description |
|-------|------|-------------|
| `name` | string | Human-readable adapter name (for logging). |
| `modelPattern` | string | Regex matched against the deployment/model name. First match wins. |
| `promptStyle` | string | `"imperative"` or `"conversational"`. Currently informational. |
| `preamble` | string | Model-specific prompt instructions injected between Identity and Custom Instructions. |
| `isReasoningModel` | bool | True for o-series models. Disables Temperature and JSON response format. |
| `temperature` | float? | Override sampling temperature. Ignored for reasoning models. |
| `maxOutputTokensBatch` | int? | Override max output tokens for batch reviews. |
| `maxOutputTokensSingleFile` | int? | Override max output tokens for single-file reviews. |
| `maxOutputTokensVerification` | int? | Override max output tokens for thread verification. |
| `maxOutputTokensPrSummary` | int? | Override max output tokens for Pass 1 summary. |
| `maxInputLinesPerFile` | int? | Override per-file input line truncation limit. |
| `contextWindowSize` | int? | Model's total context window (input + output tokens). |
| `maxOutputTokensModel` | int? | Model-level hard limit on output tokens. |
| `inputCostPer1MTokens` | decimal? | USD cost per 1M input tokens (for cost estimation). |
| `outputCostPer1MTokens` | decimal? | USD cost per 1M output tokens (for cost estimation). |
| `requestsPerMinute` | int? | RPM rate limit (drives [RPM-aware throttling](#rpm-aware-throttling--cost-estimation)). |
| `tokensPerMinute` | int? | TPM rate limit (informational). |
| `quirks` | string[] | Documented model quirks (logged, not injected into prompts). |

#### Assistants API / Vector Store Settings

When using the **Vector** or **Auto** review strategy, the service interacts with the Azure OpenAI Assistants API. Configure these settings under the `Assistants` section:

```json
{
  "Assistants": {
    "AutoThreshold": 5,
    "PollIntervalMs": 1000,
    "MaxPollAttempts": 120,
    "ApiVersion": "2024-05-01-preview",
    "MaxParallelUploads": 10
  }
}
```

| Setting | Type | Default | Description |
|---------|------|---------|-------------|
| `AutoThreshold` | int | `5` | When `reviewStrategy` is `Auto`, PRs with more than this many changed files use Vector Store; otherwise FileByFile. |
| `PollIntervalMs` | int | `1000` | Milliseconds between polling attempts when waiting for Vector Store indexing or Assistant run completion. |
| `MaxPollAttempts` | int | `120` | Maximum polling attempts before timing out. With default interval, this gives a 2-minute timeout. |
| `ApiVersion` | string | `"2024-05-01-preview"` | Azure OpenAI Assistants API version. |
| `MaxParallelUploads` | int | `10` | Maximum concurrent file uploads to the Vector Store. |

#### Legacy Custom Instructions

For simpler setups, you can still use `custom-instructions.json` directly:

```json
{
  "customInstructions": "In addition to standard review criteria, also evaluate the following:\n\n- **Cyclomatic complexity**: Flag methods with high cyclomatic complexity (roughly >10) and suggest refactoring.\n- **Method length**: Flag methods longer than ~50 lines and suggest decomposition.\n- **Magic numbers/strings**: Flag hardcoded values that should be constants or configuration.\n- **Error handling**: Ensure exceptions are caught at appropriate levels and not silently swallowed.\n- **Async best practices**: Check for proper async/await usage — no sync-over-async, no fire-and-forget without justification."
}
```

If no `review-rules.json` catalog exists, the pipeline falls back to hardcoded prompts. If the file doesn't exist or the path is empty, no custom instructions are injected — the AI still performs a thorough review using its base instructions.

---

## Running the Service

### Local Development

```bash
# Navigate to the project
cd src/HVO.AiCodeReview

# Restore and build
dotnet build

# Run with development settings (reads appsettings.Development.json)
dotnet run --launch-profile http
```

The service starts on **http://localhost:5094**. Swagger UI is available at http://localhost:5094/swagger.

### Docker / Container

A multi-stage `Dockerfile` is included at `src/HVO.AiCodeReview/Dockerfile`. It builds with the .NET 8 SDK, publishes to a slim ASP.NET runtime image, and runs as a non-root user on port 8080.

```bash
# Build (from repo root)
docker build -f src/HVO.AiCodeReview/Dockerfile -t hvo-ai-code-review .

# Run with environment variables for secrets
docker run -d -p 8080:8080 \
  -e AzureDevOps__Organization="MyOrg" \
  -e AzureDevOps__PersonalAccessToken="your-pat" \
  -e AzureOpenAI__Endpoint="https://my-resource.openai.azure.com/" \
  -e AzureOpenAI__ApiKey="your-key" \
  -e AzureOpenAI__DeploymentName="gpt-4o" \
  hvo-ai-code-review
```

### Production Deployment

For production, deploy as an Azure App Service, Azure Container Instance, or Kubernetes pod. Pass all secrets via environment variables or Azure Key Vault references — **never commit secrets to source control**.

```bash
# Override specific settings via command-line
dotnet run -- \
  --AzureDevOps:MinReviewIntervalMinutes=10 \
  --AzureOpenAI:DeploymentName=gpt-4o
```

---

## API Endpoints

### POST /api/review

Execute an AI code review for a pull request.

**Request:**

```http
POST /api/review
Content-Type: application/json

{
  "projectName": "OneVision",
  "repositoryName": "MyRepo",
  "pullRequestId": 12345,
  "reviewDepth": "Standard",
  "reviewStrategy": "FileByFile"
}
```

| Field | Type | Required | Default | Description |
|-------|------|----------|---------|-------------|
| `projectName` | string | Yes | — | Azure DevOps project name. |
| `repositoryName` | string | Yes | — | Repository name or GUID. |
| `pullRequestId` | int | Yes | — | Pull request ID (must be > 0). |
| `reviewDepth` | string | No | `"Standard"` | Review depth mode: `"Quick"`, `"Standard"`, or `"Deep"`. See [Review Depth Modes](#review-depth-modes). |
| `reviewStrategy` | string | No | `"FileByFile"` | Pass 2 strategy: `"FileByFile"`, `"Vector"`, or `"Auto"`. See [Review Strategies](#review-strategies). |

**Response — Reviewed (Full Review or Re-Review):**

```json
{
  "status": "Reviewed",
  "recommendation": "ApprovedWithSuggestions",
  "summary": "## Code Review #1 -- PR #12345\n\n### Summary\n5 files changed (3 edits, 2 adds, 0 deletes)...",
  "issueCount": 4,
  "errorCount": 0,
  "warningCount": 1,
  "infoCount": 3,
  "errorMessage": null,
  "reviewDepth": "Standard",
  "vote": 5,
  "promptTokens": 12500,
  "completionTokens": 3200,
  "totalTokens": 15700,
  "aiDurationMs": 5400,
  "estimatedCost": 0.0038
}
```

**Response — Skipped (no changes since last review):**

```json
{
  "status": "Skipped",
  "recommendation": null,
  "summary": "This PR has already been reviewed. No new changes detected since the last review.",
  "issueCount": 0,
  "errorCount": 0,
  "warningCount": 0,
  "infoCount": 0,
  "errorMessage": null,
  "vote": null
}
```

**Response — Rate Limited:**

```json
{
  "status": "RateLimited",
  "recommendation": null,
  "summary": "This PR was reviewed too recently. Please wait 115 seconds before requesting another review. (Cooldown: 5 min, last reviewed: 2026-02-14 00:50:18 UTC, next allowed: 2026-02-14 00:55:18 UTC)",
  "issueCount": 0,
  "errorCount": 0,
  "warningCount": 0,
  "infoCount": 0,
  "errorMessage": null,
  "vote": null
}
```

**Response — Error:**

```json
{
  "status": "Error",
  "recommendation": null,
  "summary": null,
  "issueCount": 0,
  "errorCount": 0,
  "warningCount": 0,
  "infoCount": 0,
  "errorMessage": "Detailed error message here.",
  "vote": null
}
```

**Status Codes:**

| Code | When |
|------|------|
| `200 OK` | Review completed, skipped, or rate-limited. |
| `400 Bad Request` | Invalid request body. |
| `500 Internal Server Error` | Unhandled exception (returned as `status: "Error"`). |

**Response Field Reference:**

| Field | Type | Description |
|-------|------|-------------|
| `status` | string | `"Reviewed"`, `"Skipped"`, `"RateLimited"`, or `"Error"`. |
| `recommendation` | string? | `"Approved"`, `"ApprovedWithSuggestions"`, `"NeedsWork"`, `"Rejected"`, or `null`. |
| `summary` | string? | The full Markdown summary posted to the PR thread. |
| `issueCount` | int | Total inline comments posted. |
| `errorCount` | int | Bug / Security severity comments. |
| `warningCount` | int | Concern / Performance severity comments. |
| `infoCount` | int | Suggestion / LGTM / Info severity comments. |
| `errorMessage` | string? | Error details if `status` is `"Error"`. |
| `reviewDepth` | string? | The review depth used: `"Quick"`, `"Standard"`, or `"Deep"`. `null` for non-reviewed responses. |
| `vote` | int? | Vote cast: `10` (Approved), `5` (Approved w/ Suggestions), `-5` (Waiting for Author), `-10` (Rejected), or `null`. |
| `promptTokens` | int? | Total prompt (input) tokens consumed across all AI calls. |
| `completionTokens` | int? | Total completion (output) tokens consumed. |
| `totalTokens` | int? | Sum of prompt + completion tokens. |
| `aiDurationMs` | long? | AI inference time in milliseconds (sum of all AI calls). |
| `estimatedCost` | decimal? | Estimated cost in USD based on model pricing and actual token usage. Requires pricing data in the model adapter. |

---

### GET /api/review/metrics

Get full review history and aggregated AI metrics for a pull request. All data comes from PR properties — not affected by tag deletion.

**Request:**

```http
GET /api/review/metrics?project=OneVision&repository=MyRepo&pullRequestId=12345
```

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `project` | string | Yes | Azure DevOps project name. |
| `repository` | string | Yes | Repository name. |
| `pullRequestId` | int | Yes | Pull request ID. |

**Response:**

```json
{
  "pullRequestId": 12345,
  "reviewCount": 3,
  "lastReviewedAtUtc": "2026-02-14T00:50:18.123Z",
  "lastSourceCommit": "abc1234def5678",
  "lastIteration": 2,
  "wasDraft": false,
  "voteSubmitted": true,
  "history": [
    {
      "reviewNumber": 1,
      "reviewedAtUtc": "2026-02-13T22:15:00Z",
      "action": "Full Review",
      "verdict": "APPROVED WITH SUGGESTIONS",
      "sourceCommit": "abc1234",
      "iteration": 1,
      "isDraft": true,
      "inlineComments": 4,
      "filesChanged": 37,
      "vote": null,
      "modelName": "gpt-4o-2024-08-06",
      "promptTokens": 12500,
      "completionTokens": 3200,
      "totalTokens": 15700,
      "aiDurationMs": 5400,
      "totalDurationMs": 8200,
      "estimatedCost": 0.0511
    },
    {
      "reviewNumber": 2,
      "reviewedAtUtc": "2026-02-14T00:50:18Z",
      "action": "Skipped",
      "verdict": "No Changes",
      "sourceCommit": "abc1234",
      "iteration": 1,
      "isDraft": false,
      "inlineComments": 0,
      "filesChanged": 0,
      "vote": null,
      "modelName": null,
      "promptTokens": null,
      "completionTokens": null,
      "totalTokens": null,
      "aiDurationMs": null,
      "totalDurationMs": null
    }
  ],
  "totalPromptTokens": 12500,
  "totalCompletionTokens": 3200,
  "totalTokensUsed": 15700,
  "totalAiDurationMs": 5400,
  "totalDurationMs": 8200
}
```

---

### GET /api/review/health

Health check endpoint. Verifies the service is running and can reach Azure DevOps.

**Request:**

```http
GET /api/review/health
```

**Response (healthy):**

```json
{
  "status": "healthy",
  "timestamp": "2026-02-14T00:55:00.000Z",
  "azureDevOps": "connected"
}
```

**Response (degraded):**

```json
{
  "status": "degraded",
  "timestamp": "2026-02-14T00:55:00.000Z",
  "azureDevOps": "unreachable: Connection refused"
}
```

| Code | Status | When |
|------|--------|------|
| `200` | healthy | Service running, Azure DevOps reachable |
| `503` | degraded | Service running, Azure DevOps unreachable |

---

## Azure DevOps Pipeline Integration

To automatically trigger a review on every pull request, add a step to your PR validation pipeline that calls the API.

### Pipeline YAML

```yaml
trigger: none

pr:
  branches:
    include:
      - main
      - develop

variables:
  # URL where the AI Code Review service is hosted
  aiReviewServiceUrl: 'https://your-ai-review-service.azurewebsites.net'

jobs:
  - job: AICodeReview
    displayName: 'AI Code Review'
    pool:
      vmImage: 'ubuntu-latest'
    steps:
      - task: Bash@3
        displayName: 'Request AI Code Review'
        inputs:
          targetType: inline
          script: |
            RESPONSE=$(curl -s -w "\n%{http_code}" -X POST \
              "$(aiReviewServiceUrl)/api/review" \
              -H "Content-Type: application/json" \
              -d '{
                "projectName": "$(System.TeamProject)",
                "repositoryName": "$(Build.Repository.Name)",
                "pullRequestId": $(System.PullRequest.PullRequestId)
              }')

            HTTP_CODE=$(echo "$RESPONSE" | tail -1)
            BODY=$(echo "$RESPONSE" | sed '$d')

            echo "HTTP Status: $HTTP_CODE"
            echo "Response: $BODY"

            # Extract status from JSON response
            STATUS=$(echo "$BODY" | python3 -c "import sys,json; print(json.load(sys.stdin).get('status',''))")

            if [ "$STATUS" = "Error" ]; then
              echo "##vso[task.logissue type=error]AI Code Review failed"
              exit 1
            fi

            echo "AI Review Status: $STATUS"
```

### Pipeline Variables

The pipeline YAML above uses a mix of built-in and user-defined variables. The built-in variables are **automatically populated by Azure DevOps at runtime** — you do not need to set them. The only variable you need to configure is the service URL.

| Variable | Auto / Manual | Description |
|----------|:---:|-------------|
| `$(System.TeamProject)` | Auto | Azure DevOps project name. Populated automatically when the pipeline runs. |
| `$(Build.Repository.Name)` | Auto | Repository name. Populated automatically when the pipeline runs. |
| `$(System.PullRequest.PullRequestId)` | Auto | The PR ID that triggered this pipeline run. **Only available on PR-triggered pipelines** (`pr:` trigger). Azure DevOps injects this automatically — the pipeline step passes it in the API request body so the service knows which PR to review. |
| `$(aiReviewServiceUrl)` | Manual | The base URL where the AI Code Review service is hosted. Set this as a pipeline variable or in a variable group (e.g., `https://your-ai-review-service.azurewebsites.net`). |

### Optional: Fail Pipeline on "Needs Work"

```yaml
      - task: Bash@3
        displayName: 'Check AI Review Verdict'
        inputs:
          targetType: inline
          script: |
            RECOMMENDATION=$(echo "$BODY" | python3 -c "import sys,json; print(json.load(sys.stdin).get('recommendation',''))")

            if [ "$RECOMMENDATION" = "NeedsWork" ] || [ "$RECOMMENDATION" = "Rejected" ]; then
              echo "##vso[task.logissue type=warning]AI Review verdict: $RECOMMENDATION"
              # Uncomment the next line to fail the build on NeedsWork/Rejected
              # exit 1
            fi
```

### Optional: Gate with Review Status

If you want the pipeline to pass even when the AI review finds issues (advisory mode), check the `status` field only:

```yaml
            if [ "$STATUS" = "Reviewed" ] || [ "$STATUS" = "Skipped" ] || [ "$STATUS" = "RateLimited" ]; then
              echo "AI Code Review completed successfully."
            else
              echo "##vso[task.logissue type=error]Unexpected status: $STATUS"
              exit 1
            fi
```

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

---

## Model Benchmarks & Selection

The project includes a benchmark test suite (`ModelBenchmarkTests.cs`) that evaluates all configured models against **10 known-bad-code issues** to measure detection quality, latency, and cost at each review depth.

### Known-Bad-Code Test Issues

The benchmark uses a deliberately vulnerable C# file containing these 10 issues:

| # | Issue | Category |
|---|-------|----------|
| 1 | Hardcoded database credentials | Security |
| 2 | SQL injection (string concatenation) | Security |
| 3 | Path traversal (unsanitized user input) | Security |
| 4 | Null dereference (no null check) | Bug |
| 5 | `HttpClient` created in a loop (socket exhaustion) | Performance |
| 6 | Exception silently swallowed (empty catch) | Bug |
| 7 | Sensitive data logged to console | Security |
| 8 | `async void` method (fire-and-forget) | Bug |
| 9 | Double `Dispose` (manual + using) | Bug |
| 10 | API key in query string | Security |

### Model Comparison

Each model is tested at all three review depths. The "Quality Score" is the number of issues detected out of 10:

| Model | Context | Max Out | RPM | $/1M In | $/1M Out | Quality (Std) | Speed |
|-------|--------:|--------:|----:|--------:|---------:|:-------------:|:-----:|
| **gpt-4o-mini** | 128K | 16K | 4,990 | $0.15 | $0.60 | 7–8/10 | Fast |
| **gpt-4o** | 128K | 4K | 2,700 | $2.50 | $10.00 | 8–9/10 | Medium |
| **o3-mini** | 200K | 100K | 100 | $1.10 | $4.40 | 8–9/10 | Slow |
| **o4-mini** | 200K | 100K | 150 | $1.10 | $4.40 | 9–10/10 | Slow |
| **gpt-5-mini** | 400K | 128K | 501 | $0.25 | $2.00 | 8–9/10 | Medium |

### Selected Depth → Model Mapping

Based on benchmark results, the following defaults are configured:

```
Quick    → gpt-4o-mini   (fastest, cheapest — ideal for PR summary only)
Standard → gpt-4o-mini   (best cost/quality balance for per-file reviews)
Deep     → o4-mini       (reasoning model — best quality for deep analysis)
```

**Why these models?**

- **gpt-4o-mini** detects 7–8 out of 10 issues at ~$0.007 per review with 4,990 RPM throughput. For Quick mode (no file reviews) and Standard mode (per-file), this provides excellent value.
- **o4-mini** detects 9–10 out of 10 issues and excels at cross-file reasoning (Deep Pass 3). The higher cost (~$0.05 per review) is justified for critical PRs and release branches.
- **gpt-4o** provides higher quality (8–9/10) but at 16× the cost of gpt-4o-mini. Reserved for consensus mode or manual overrides.
- **o3-mini** matches o4-mini quality with similar pricing but lower RPM (100 vs 150). o4-mini is preferred.

### Running Benchmarks

```bash
# Run all benchmark tests (requires real Azure OpenAI — costs money)
dotnet test --filter TestCategory=Benchmark

# Results include per-model quality scores, token counts, latency, and estimated cost
# Output is printed as both console tables and Markdown tables for documentation
```

---

## Project Structure

```
HVO.AiCodeReview/
├── HVO.AiCodeReview.sln                # Solution file
├── .gitignore
├── README.md
├── .env.template                       # Environment variable template (safe to commit)
│
├── .devcontainer/                      # Dev container configuration
│   ├── devcontainer.json               # Container features, mounts, env vars
│   ├── Dockerfile                      # Dev container base image
│   ├── deps.compose.yml                # PostgreSQL + Redis for local dev
│   ├── post-create.sh                  # SDK validation, tooling setup
│   ├── start-deps.sh                   # Start dependency containers
│   └── python-requirements.txt         # Python packages for tooling
│
├── .github/
│   └── copilot-session-context.md      # Copilot session context & project history
│
├── scripts/
│   ├── ai-code-review.ps1              # PowerShell pipeline script
│   └── azure-pipelines-template.yml    # Azure Pipelines YAML template
│
├── docs/                               # Documentation
│
├── src/
│   └── HVO.AiCodeReview/               # Main web API project
│       ├── HVO.AiCodeReview.csproj
│       ├── Program.cs                  # DI setup, middleware pipeline
│       ├── Dockerfile                  # Multi-stage production Docker build
│       ├── appsettings.json            # Configuration template
│       ├── appsettings.Development.json        # Dev overrides (gitignored)
│       ├── appsettings.Development.template.json # Dev config template
│       ├── custom-instructions.json    # Optional AI review instructions
│       ├── review-rules.json           # Layered prompt rule catalog (hot-reloadable)
│       ├── model-adapters.json         # Per-model adapter catalog (pricing, RPM, quirks)
│       │
│       ├── Controllers/
│       │   └── CodeReviewController.cs # API endpoints (w/ CancellationToken)
│       │
│       ├── Models/
│       │   ├── AiProviderSettings.cs       # Multi-provider AI configuration + DepthModels
│       │   ├── AssistantsSettings.cs       # Assistants API / Vector Store settings
│       │   ├── AzureDevOpsSettings.cs      # Azure DevOps configuration
│       │   ├── AzureOpenAISettings.cs      # Legacy Azure OpenAI settings
│       │   ├── CodeReviewResult.cs         # AI review result (summary, verdicts, comments)
│       │   ├── DeepAnalysisResult.cs       # Pass 3 deep analysis result model
│       │   ├── FileChange.cs               # File diff with unified diff & changed ranges
│       │   ├── ModelAdapter.cs             # Model adapter config + CalculateCost() helper
│       │   ├── PrSummaryResult.cs          # Pass 1 PR summary result model
│       │   ├── PullRequestInfo.cs          # PR info from Azure DevOps
│       │   ├── ReviewDepth.cs              # ReviewDepth enum (Quick, Standard, Deep)
│       │   ├── ReviewMetadata.cs           # PR properties metadata + history entries
│       │   ├── ReviewMetricsResponse.cs    # Metrics API response DTO
│       │   ├── ReviewProfile.cs            # Configurable review profile (thresholds, density)
│       │   ├── ReviewRequest.cs            # POST request DTO (depth + strategy)
│       │   ├── ReviewResponse.cs           # POST response DTO (w/ tokens, cost)
│       │   ├── ReviewRuleCatalog.cs        # Prompt rule catalog models
│       │   ├── ReviewStatusUpdate.cs       # Progress tracking model
│       │   ├── ReviewStrategy.cs           # ReviewStrategy enum (FileByFile, Vector, Auto)
│       │   └── WorkItemInfo.cs             # Work item integration model
│       │
│       ├── Services/
│       │   ├── CodeReviewOrchestrator.cs   # Review lifecycle orchestration (modular extracted methods)
│       │   ├── ICodeReviewOrchestrator.cs  # Orchestrator interface
│       │   ├── AzureOpenAiReviewService.cs # Azure OpenAI Chat Completions provider
│       │   ├── VectorStoreReviewService.cs # Azure OpenAI Assistants API + Vector Store provider
│       │   ├── ConsensusReviewService.cs   # Multi-provider consensus aggregator
│       │   ├── CodeReviewServiceFactory.cs # Config-driven provider factory + DepthModels DI
│       │   ├── DepthModelResolver.cs       # Depth → ICodeReviewService routing
│       │   ├── ICodeReviewService.cs       # AI service interface (provider-agnostic)
│       │   ├── AzureDevOpsService.cs       # Azure DevOps REST API client
│       │   ├── IAzureDevOpsService.cs      # DevOps service interface
│       │   ├── PromptAssemblyPipeline.cs   # Layered prompt assembly with hot-reload
│       │   ├── ModelAdapterResolver.cs     # Per-model adapter preamble + metadata resolver
│       │   ├── ReviewRateLimiter.cs        # In-memory PR-level rate limiter
│       │   ├── RateLimitHelper.cs          # Retry-After header parsing + retry delay computation
│       │   └── GlobalRateLimitSignal.cs    # Singleton global cooldown signal for 429 responses
│       │
│       └── Properties/
│           └── launchSettings.json     # Dev launch profiles
│
└── tests/
    └── HVO.AiCodeReview.Tests/         # MSTest unit + integration tests (337 total)
        ├── HVO.AiCodeReview.Tests.csproj
        ├── appsettings.Test.json           # Test config (gitignored)
        ├── appsettings.Test.template.json  # Test config template
        ├── MSTestSettings.cs               # MSTest parallelism configuration
        ├── ReviewLifecycleTests.cs         # 5 parallel lifecycle tests
        ├── ServiceIntegrationTests.cs      # 7 service-level tests
        ├── UnifiedDiffTests.cs             # 9 diff algorithm unit tests
        ├── MultiProviderTests.cs           # 18 factory + consensus tests
        ├── LayeredPromptTests.cs           # 38 prompt pipeline tests
        ├── ModelAdapterTests.cs            # 35 model adapter tests
        ├── TwoPassReviewTests.cs           # 21 two-pass architecture tests
        ├── ReviewProfileTests.cs           # 13 review profile tests
        ├── TruncationConfigTests.cs        # 14 truncation config tests
        ├── FileClassificationTests.cs      # 24 file classification tests
        ├── ThreadManagementTests.cs        # 17 thread management tests
        ├── BuildSummaryMarkdownTests.cs    # 10 summary formatting tests
        ├── ReviewDepthTests.cs             # 22 depth mode + integration tests
        ├── RaceConditionTests.cs           # 3 concurrency / thread-safety tests
        ├── RateLimitTests.cs               # 32 rate-limit helper + global signal tests
        ├── VectorStoreReviewServiceTests.cs # 24 vector store unit tests
        ├── VectorStoreIntegrationTest.cs   # 1 live vector store integration test
        ├── ModelBenchmarkTests.cs          # 8 model benchmark tests (5 models × 3 depths)
        ├── AiQualityVerificationTests.cs   # 3 known-bad-code LiveAI tests
        ├── LiveAiDepthModeTests.cs         # 4 depth mode LiveAI tests
        ├── AiSmokeTest.cs                  # 2 manual AI smoke tests
        ├── SimulationPR63643.cs            # 1 simulation/manual test
        ├── ReviewFlowIntegrationTests.cs   # 3 legacy lifecycle (Ignored)
        └── Helpers/
            ├── FakeCodeReviewService.cs    # Deterministic AI replacement
            ├── TestServiceBuilder.cs       # Shared DI builder (FakeAi + RealAi)
            └── TestPullRequestHelper.cs    # Disposable repo + 6-layer safety
```

---

## Testing

The project includes comprehensive integration tests that run against a real Azure DevOps instance using **disposable test repositories**.

### Disposable Test Repositories

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

### 6-Layer Safety System

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

### PAT Requirements for Tests

The PAT used for integration tests requires the following **additional** scope beyond what the service itself needs:

| Scope | Permission | Used For |
|-------|-----------|----------|
| Code | **Manage** | Creating and deleting disposable test repositories |

### Run All Automated Tests

```bash
# From repo root
dotnet test --filter 'TestCategory!=Manual&FullyQualifiedName!~InspectPR&FullyQualifiedName!~CleanupTestPR'
```

### Test Categories

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
| `VectorStoreReviewServiceTests.cs` | 24 | Unit | Vector Store review service: file upload, vector store creation, assistant lifecycle, response parsing, cleanup, error handling. |
| `VectorStoreIntegrationTest.cs` | 1 | LiveAI | Live Vector Store integration test against real Azure OpenAI Assistants API. |
| `ModelBenchmarkTests.cs` | 8 | Benchmark | Model quality comparison: 5 individual model tests + 3 all-model comparison tests (one per depth). Runs against 10 known-bad-code issues. Produces comparison tables and cost estimates. |
| `AiQualityVerificationTests.cs` | 3 | LiveAI | Push code with **known, deliberate issues** (hardcoded secrets, SQL injection, null derefs, resource leaks) and verify the real AI flags them. Includes a fix-and-reverify cycle. Run with `--filter TestCategory=LiveAI`. |
| `LiveAiDepthModeTests.cs` | 4 | LiveAI | Real AI tests for all three review depth modes: Quick (no inline), Standard (inline + verdicts), Deep (+ cross-file analysis). Includes a depth comparison test that runs all 3 modes on the same multi-file known-bad code and compares output. Uses `PushMultipleFilesAsync` for cross-file scenarios. |
| `AiSmokeTest.cs` | 2 | Manual | Manual-only tests that call real Azure OpenAI (basic prompt + JSON mode). Run with `--filter TestCategory=Manual`. |
| `SimulationPR63643.cs` | 1 | Manual | Simulation test against a specific PR for debugging and validation. |
| `ReviewFlowIntegrationTests.cs` | 3 (Ignored) | — | Legacy monolithic lifecycle test. Replaced by `ReviewLifecycleTests.cs`. Kept for reference. |

### Test Infrastructure

| Component | Purpose |
|-----------|---------|
| `TestServiceBuilder.cs` | Shared DI container builder. `BuildWithFakeAi()` registers `FakeCodeReviewService` for deterministic tests. `BuildWithRealAi(modelOverride?)` registers the real `CodeReviewService` and optionally overrides the AI model deployment name. |
| `TestPullRequestHelper.cs` | Creates/manages disposable test repos with the 6-layer safety system (instance tracking, never-delete list, name prefix, marker file, creation recency, PAT ACL verification). Supports single-file (`PushNewCommitAsync`) and multi-file (`PushMultipleFilesAsync`) pushes for cross-file analysis testing. |
| `FakeCodeReviewService.cs` | Deterministic fake with `ResultFactory`, `VerificationResultFactory`, and `DeepAnalysisFactory` for custom per-test behavior. Produces 2 stable inline comments per file for dedup testing. |

### Running Tests

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

### Manual Test Utilities

Two tests are tagged `[Ignore]` (run explicitly by filter) for ad-hoc use:

| Test | Purpose |
|------|---------|
| `InspectPR_NoCleanup` | Creates a disposable repo + PR and leaves it alive (sets `SkipCleanupOnDispose = true`) so you can manually inspect the PR in the Azure DevOps UI. |
| `CleanupTestPR` | Manually deletes a specific repo by name/ID. Performs its own safety checks (prefix, never-delete list, marker file) before deletion. |

### Test Configuration

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

---

## Dev Container

The repo includes a full dev container configuration in `.devcontainer/` for a consistent development environment.

**What's included:**

| Component | Details |
|-----------|---------|
| **.NET** | SDK 10.0 (+ 8.0, 9.0) |
| **Node.js** | LTS |
| **Python** | 3.12 with venv |
| **Java** | 21 |
| **Go** | Latest |
| **Terraform** | Latest |
| **Azure CLI** | Latest with Bicep |
| **GitHub CLI** | Latest |
| **Docker-in-Docker** | Docker + Compose v2 |
| **kubectl + Helm** | Latest |
| **PowerShell** | Latest |

**VS Code Extensions:** C# Dev Kit, GitHub Copilot, GitHub Copilot Chat, GitHub Actions.

**Local Services:** PostgreSQL 16 and Redis 7 are started automatically via `docker compose` on container start (`start-deps.sh`).

**Secrets:** .NET User Secrets are bind-mounted from the host machine (`~/.microsoft/usersecrets`), so each developer gets their own secrets without anything being committed to source control.

**Getting started:** Open the repo in VS Code and select "Reopen in Container" when prompted. The `post-create.sh` script validates all SDKs, creates a Python venv, installs tooling, and sets up HTTPS dev certificates.

---

## Scripts

The `scripts/` folder contains pipeline integration helpers:

| File | Description |
|------|-------------|
| `ai-code-review.ps1` | PowerShell script for Azure Pipelines. Calls the review API, sets pipeline output variables (`AI_REVIEW_STATUS`, `AI_REVIEW_RECOMMENDATION`, `AI_REVIEW_ISSUE_COUNT`), and fails the pipeline on "Rejected" or warns on "NeedsWork". |
| `azure-pipelines-template.yml` | Azure Pipelines YAML template with two options: inline PowerShell (Option A) or external script file (Option B). Conditionally runs on PR builds only. |

---

## Test Roadmap / Future Enhancements

Planned improvements to the testing infrastructure:

| Enhancement | Description | Status |
|-------------|-------------|--------|
| **Multi-model comparison** | Run the same tests against different models and compare comment quality, latency, and cost. | ✅ Done — `ModelBenchmarkTests.cs` covers 5 models × 3 depths with quality scoring and cost estimation. |
| **Cost & latency tracking** | Instrument tests to log token usage and wall-clock time per model, enabling data-driven model selection. | ✅ Done — Benchmark tests capture token counts, latency, and estimated cost per model. `ReviewResponse` includes `EstimatedCost`. |
| **Refactor ServiceIntegrationTests** | Migrate `ServiceIntegrationTests.cs` from its private `BuildServices()` to use `TestServiceBuilder.BuildWithFakeAi()` for consistency. | Medium |
| **Multi-file PR edge cases** | Test PRs with 10–50 changed files to verify parallel review, density threshold, and rate limiting under load. | Medium |
| **Large file density threshold** | Push a single file with 1000+ lines and only 2 changes to verify the density-based threshold correctly skips low-density files. | Medium |
| **Language-specific known-bad-code** | Expand `KnownBadCode` samples beyond C# to include TypeScript, Python, Java, and SQL to test the AI's cross-language review capability. | Low |
| **Flaky test detection** | Run `LiveAI` tests N times and track which assertions are non-deterministic due to AI variability. Adjust thresholds accordingly. | Low |
| **Pipeline integration tests** | Stand up a real Azure DevOps pipeline webhook scenario end-to-end in a disposable project. | Future |

---

## License

Internal use only. Not for external distribution.
