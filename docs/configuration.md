# Configuration

All configuration is read from the standard ASP.NET Core configuration system, supporting `appsettings.json`, `appsettings.{Environment}.json`, environment variables, and command-line arguments.

## Table of Contents

- [Application Settings](#application-settings)
  - [AzureDevOps Section](#azuredevops-section)
  - [AzureOpenAI Section (Legacy)](#azureopenai-section-legacy--backward-compatible)
- [Multi-Provider AI Configuration](#multi-provider-ai-configuration)
  - [Provider Config](#provider-config)
  - [Example: Consensus Mode](#example-consensus-mode-with-two-models)
- [Depth-Specific Model Routing](#depth-specific-model-routing)
- [Per-Pass Model Routing (PassRouting)](#per-pass-model-routing-passrouting)
- [Environment Variables](#environment-variables)
- [Custom Review Instructions](#custom-review-instructions)
  - [Prompt Layers](#prompt-layers-in-order)
  - [Rule Catalog](#rule-catalog-review-rulesjson)
  - [Model Adapters](#model-adapters)
  - [Assistants API / Vector Store Settings](#assistants-api--vector-store-settings)
- [ReviewQueue Section](#reviewqueue-section)

---

## Application Settings

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

### AzureDevOps Section

| Setting | Type | Default | Required | Description |
|---------|------|---------|----------|-------------|
| `Organization` | `string` | `""` | Yes | Azure DevOps organization name. |
| `PersonalAccessToken` | `string` | `""` | Yes | PAT with the scopes listed in [Getting Started](getting-started.md#azure-devops-pat-scopes). |
| `ServiceAccountIdentityId` | `string` | `""` | No | GUID of the PAT owner. If empty, auto-discovered at first use via the connection data API. |
| `ReviewTagName` | `string` | `"ai-code-review"` | No | Label applied to PRs after review. Purely decorative. |
| `AddReviewerVote` | `bool` | `true` | No | Whether to add the service as a reviewer with a vote on non-draft PRs. |
| `MinReviewIntervalMinutes` | `int` | `5` | No | Cooldown between reviews on the same PR. Set to `0` to disable rate limiting. |

### AzureOpenAI Section (Legacy — Backward Compatible)

> **Note:** The `AzureOpenAI` section still works as a fallback. If no `AiProvider:Providers` are configured,
> the service reads from this section. For new setups, prefer the `AiProvider` section below.

| Setting | Type | Default | Required | Description |
|---------|------|---------|----------|-------------|
| `Endpoint` | `string` | `""` | Yes | Azure OpenAI endpoint URL (e.g., `https://your-resource.openai.azure.com/`). |
| `ApiKey` | `string` | `""` | Yes | Azure OpenAI API key. |
| `DeploymentName` | `string` | `""` | Yes | The model deployment name (e.g., `gpt-4o`, `gpt-4`, `gpt-5`). Any chat-completion model that supports structured JSON output will work. |
| `CustomInstructionsPath` | `string` | `"custom-instructions.json"` | No | Path to optional custom review instructions file. Relative to app directory. |

---

## Multi-Provider AI Configuration

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

| Setting | Type | Default | Required | Description |
|---------|------|---------|----------|-------------|
| `MaxParallelReviews` | `int` | `5` | No | Maximum concurrent per-file AI review calls. |
| `MaxInputLinesPerFile` | `int` | `5000` | No | Maximum number of source lines sent to AI per file. Files exceeding this are truncated. |
| `Mode` | `string` | `"single"` | No | `"single"` = use `ActiveProvider` only. `"consensus"` = fan out to ALL enabled providers and merge. |
| `ActiveProvider` | `string` | `"azure-openai"` | No | Which provider key to use when Mode = single. |
| `ConsensusThreshold` | `int` | `2` | No | In consensus mode, minimum providers that must flag a comment for it to be kept. |
| `DepthModels` | `object` | `{}` | No | Maps review depth → provider key. See [Depth-Specific Model Routing](#depth-specific-model-routing). |
| `PassRouting` | `object` | `{}` | No | Maps review pass → provider key. See [Per-Pass Model Routing](#per-pass-model-routing-passrouting). |
| `SecurityPassEnabled` | `bool` | `false` | No | Global default for the dedicated security review pass. When `true`, every review includes a security pass unless overridden per-request. |

### Provider Config

Each entry under `Providers` has:

| Setting | Type | Default | Required | Description |
|---------|------|---------|----------|-------------|
| `Type` | `string` | — | Yes | Provider type: `azure-openai`. More planned: `github-copilot`, `openai`, `local`. |
| `DisplayName` | `string` | `""` | No | Human-readable label for logs and comment attribution. |
| `Endpoint` | `string` | `""` | Yes | API endpoint URL. |
| `ApiKey` | `string` | `""` | Yes | API key or token. |
| `Model` | `string` | `""` | Yes | Model / deployment name. |
| `CustomInstructionsPath` | `string` | `""` | No | Optional path to custom review instructions JSON. |
| `Enabled` | `bool` | `true` | No | Set `false` to temporarily disable without removing config. |

### Example: Consensus Mode with Two Models

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

---

## Depth-Specific Model Routing

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

See [Model Benchmarks & Selection](model-benchmarks.md) for the data behind these recommendations.

---

## Per-Pass Model Routing (PassRouting)

While `DepthModels` maps the review depth to a provider, `PassRouting` maps each **review pass** to a provider, giving fine-grained control over which model handles each stage of the review pipeline.

```json
{
  "AiProvider": {
    "ActiveProvider": "azure-openai",
    "PassRouting": {
      "PrSummary": "azure-openai-mini",
      "PerFileReview": "azure-openai",
      "DeepReview": "azure-openai-o4-mini",
      "SecurityPass": "azure-openai-mini",
      "ThreadVerification": "azure-openai-mini"
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

### Available Passes

| Pass | Description |
|------|-------------|
| `PrSummary` | Pass 1 — PR-level summary generation (cross-file context). |
| `PerFileReview` | Pass 2 — Per-file parallel review. |
| `DeepReview` | Pass 3 — Holistic deep analysis (Deep mode only). |
| `SecurityPass` | Dedicated security analysis — OWASP Top 10, hardcoded secrets, injection risks, auth/authz. Runs after standard/deep passes. Default model: gpt-4o-mini. |
| `ThreadVerification` | AI verification of prior comment threads during re-review. |

### Resolution Order

When the orchestrator needs an AI service for a specific pass, the `PassModelResolver` resolves in this order:

1. **PassRouting** — If `AiProvider:PassRouting` has an entry for the current pass, use that provider.
2. **DepthModels** — If no pass-specific routing, fall back to the depth-based model (e.g., `Deep` → `o4-mini`).
3. **ActiveProvider** — If neither pass nor depth routing is configured, use the default `ActiveProvider`.

This means `PassRouting` takes priority over `DepthModels`. You can mix both — configure depth-based defaults via `DepthModels` and override specific passes via `PassRouting`.

### Example: Cost-Optimized Pass Routing

Use a cheap model for summaries and thread verification, a balanced model for per-file reviews, and a reasoning model for deep analysis:

| Pass | Model | Rationale |
|------|-------|-----------|
| `PrSummary` | gpt-4o-mini | Summary generation is lightweight — cheap model suffices. |
| `PerFileReview` | gpt-4o | Best quality for detailed per-file review comments. |
| `DeepReview` | o4-mini | Reasoning model for cross-file analysis and verdict consistency. |
| `SecurityPass` | gpt-4o-mini | All models scored 100/100 on security benchmarks — mini is 17× cheaper. See [Security Benchmarks](model-benchmarks.md#security-pass-benchmark-results-2026-03-01). |
| `ThreadVerification` | gpt-4o-mini | Thread verification is simple yes/no — cheap model suffices. |

### PassModels in Review History

When per-pass model routing is active, the review history tracks which model was used for each pass:

```json
{
  "PassModels": {
    "PrSummary": "gpt-4o-mini-2024-07-18",
    "PerFileReview": "gpt-4o-2024-08-06",
    "DeepReview": "o4-mini-2025-04-16",
    "ThreadVerification": "gpt-4o-mini-2024-07-18"
  }
}
```

### Adding a New Provider Type

1. Create a class implementing `ICodeReviewService` (see `AzureOpenAiReviewService` for reference)
2. Add a case to the `switch` in `CodeReviewServiceFactory.CreateProvider()`
3. Add a config block under `AiProvider:Providers` with the new `Type` value

---

## Environment Variables

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

---

## Custom Review Instructions

The AI system prompt is assembled by the **Prompt Assembly Pipeline** from a layered rule catalog:

### Prompt Layers (in order)

| Layer | Source | Description |
|-------|--------|-------------|
| 1. Identity | `review-rules.json` → `identity` | Establishes the AI as a senior code reviewer. Shared across scopes that opt in. |
| 1½. Model Adapter | `ModelAdapterResolver` | Per-model tuning preamble (e.g., adjusting verbosity for GPT-4o vs GPT-4.1). Loaded from `model-adapters.json` catalog file. |
| 2. Custom Instructions | `custom-instructions.json` | Domain-specific review guidance (per-provider). Injected for scopes that opt in. |
| 3. Scope Preamble | `review-rules.json` → `scopes.{scope}.preamble` | Context and JSON schema for the specific prompt scope. |
| 4. Numbered Rules | `review-rules.json` → `rules[]` | Filtered by scope, sorted by priority, enabled-only. Numbered automatically. |

### Prompt Scopes

| Scope | Used For |
|-------|----------|
| `pass-1` | PR-level summary generation (cross-file context) |
| `single-file` | Per-file AI review (Pass 2) |
| `batch` | Legacy batch review (backward compatible) |
| `thread-verification` | Verifying whether prior comments are still valid |

### Rule Catalog (`review-rules.json`)

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

### Model Adapters

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

| Setting | Type | Default | Required | Description |
|---------|------|---------|----------|-------------|
| `name` | `string` | — | Yes | Human-readable adapter name (for logging). |
| `modelPattern` | `string` | — | Yes | Regex matched against the deployment/model name. First match wins. |
| `promptStyle` | `string` | `"imperative"` | No | `"imperative"` or `"conversational"`. Currently informational. |
| `preamble` | `string` | `""` | No | Model-specific prompt instructions injected between Identity and Custom Instructions. |
| `isReasoningModel` | `bool` | `false` | No | True for o-series models. Disables Temperature and JSON response format. |
| `temperature` | `float?` | `null` | No | Override sampling temperature. Ignored for reasoning models. |
| `maxOutputTokensBatch` | `int?` | `null` | No | Override max output tokens for batch reviews. |
| `maxOutputTokensSingleFile` | `int?` | `null` | No | Override max output tokens for single-file reviews. |
| `maxOutputTokensVerification` | `int?` | `null` | No | Override max output tokens for thread verification. |
| `maxOutputTokensPrSummary` | `int?` | `null` | No | Override max output tokens for Pass 1 summary. |
| `maxInputLinesPerFile` | `int?` | `null` | No | Override per-file input line truncation limit. |
| `contextWindowSize` | `int?` | `null` | No | Model's total context window (input + output tokens). |
| `maxOutputTokensModel` | `int?` | `null` | No | Model-level hard limit on output tokens. |
| `inputCostPer1MTokens` | `decimal?` | `null` | No | USD cost per 1M input tokens (for cost estimation). |
| `outputCostPer1MTokens` | `decimal?` | `null` | No | USD cost per 1M output tokens (for cost estimation). |
| `requestsPerMinute` | `int?` | `null` | No | RPM rate limit (drives [RPM-aware throttling](architecture.md#rpm-aware-throttling)). |
| `tokensPerMinute` | `int?` | `null` | No | TPM rate limit (informational). |
| `quirks` | `string[]` | `[]` | No | Documented model quirks (logged, not injected into prompts). |

### Assistants API / Vector Store Settings

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
| Setting | Type | Default | Required | Description |
|---------|------|---------|----------|-------------|
| `AutoThreshold` | `int` | `5` | No | When `reviewStrategy` is `Auto`, PRs with more than this many changed files use Vector Store; otherwise FileByFile. |
| `PollIntervalMs` | `int` | `1000` | No | Milliseconds between polling attempts when waiting for Vector Store indexing or Assistant run completion. |
| `MaxPollAttempts` | `int` | `120` | No | Maximum polling attempts before timing out. With default interval, this gives a 2-minute timeout. |
| `ApiVersion` | `string` | `"2024-05-01-preview"` | No | Azure OpenAI Assistants API version. |
| `MaxParallelUploads` | `int` | `10` | No | Maximum concurrent file uploads to the Vector Store. |

### Legacy Custom Instructions

For simpler setups, you can still use `custom-instructions.json` directly:

```json
{
  "customInstructions": "In addition to standard review criteria, also evaluate the following:\n\n- **Cyclomatic complexity**: Flag methods with high cyclomatic complexity (roughly >10) and suggest refactoring.\n- **Method length**: Flag methods longer than ~50 lines and suggest decomposition.\n- **Magic numbers/strings**: Flag hardcoded values that should be constants or configuration.\n- **Error handling**: Ensure exceptions are caught at appropriate levels and not silently swallowed.\n- **Async best practices**: Check for proper async/await usage — no sync-over-async, no fire-and-forget without justification."
}
```

If no `review-rules.json` catalog exists, the pipeline falls back to hardcoded prompts. If the file doesn't exist or the path is empty, no custom instructions are injected — the AI still performs a thorough review using its base instructions.

---

### ReviewQueue Section

Controls the review queue and worker pool for parallel PR processing. When disabled (default), reviews execute synchronously on the request thread — identical to prior versions.

```json
{
  "ReviewQueue": {
    "Enabled": false,
    "MaxConcurrentReviews": 3,
    "MaxQueueDepth": 50,
    "MaxConcurrentAiCalls": 8,
    "SessionTimeoutMinutes": 30
  }
}
```

| Setting | Type | Default | Required | Description |
|---------|------|---------|----------|-------------|
| `Enabled` | `bool` | `false` | No | Enables the review queue. When `true`, `POST /api/review` returns `202 Accepted` and processes reviews in the background. |
| `MaxConcurrentReviews` | `int` | `3` | No | Number of reviews that can execute concurrently (worker pool size). |
| `MaxQueueDepth` | `int` | `50` | No | Maximum number of reviews that can be queued. Returns `503` when full. |
| `MaxConcurrentAiCalls` | `int` | `8` | No | System-wide limit on concurrent Azure OpenAI inference calls across all active reviews. Prevents 429 rate-limit cascades. |
| `SessionTimeoutMinutes` | `int` | `30` | No | Maximum time a single review session can run before being automatically cancelled. |

See [Review Queue & Worker Pool](architecture.md#review-queue--worker-pool) for architecture details and [API Reference](api-reference.md#get-apireviewqueue) for queue management endpoints.
