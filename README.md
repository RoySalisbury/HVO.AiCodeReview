# AI Code Review Service

A centralized, AI-powered code review service for Azure DevOps pull requests. The service analyzes PR diffs using Azure OpenAI with a configurable model deployment (GPT-4o, GPT-4, GPT-5, etc.), posts inline comments and a summary thread, adds a reviewer vote, and tracks full review history — all driven by a single HTTP API call.

---

## Table of Contents

- [Features](#features)
- [Architecture](#architecture)
- [Prerequisites](#prerequisites)
- [Configuration](#configuration)
  - [Application Settings](#application-settings)
  - [Multi-Provider AI Configuration](#multi-provider-ai-configuration)
  - [Environment Variables](#environment-variables)
  - [Custom Review Instructions](#custom-review-instructions)
- [Running the Service](#running-the-service)
  - [Local Development](#local-development)
  - [Docker / Container](#docker--container)
  - [Production Deployment](#production-deployment)
- [API Endpoints](#api-endpoints)
  - [POST /api/review](#post-apireview)
  - [GET /api/review/metrics](#get-apireviewmetrics)
  - [GET /api/review/health](#get-apireviewhealth)
- [Azure DevOps Pipeline Integration](#azure-devops-pipeline-integration)
- [Review Decision Logic](#review-decision-logic)
- [Review History & Tracking](#review-history--tracking)
- [Rate Limiting](#rate-limiting)
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
| **AI-Powered Review** | Uses Azure OpenAI with a configurable model deployment to analyze diffs and produce structured reviews with per-file verdicts, inline comments, and observations. |
| **Inline PR Comments** | Posts targeted inline comments on specific lines with severity levels (Bug, Security, Concern, Performance, Suggestion). |
| **Summary Thread** | Posts a comprehensive review summary as a PR comment thread with file inventory, per-file reviews, observations, and overall verdict. |
| **Reviewer Vote** | Automatically adds itself as a reviewer with a vote (Approved / Approved with Suggestions / Waiting for Author / Rejected) on non-draft PRs. |
| **Smart Re-Review** | Detects new commits and only re-reviews when code has actually changed. Deduplicates inline comments to avoid repeating feedback. |
| **Draft PR Awareness** | Reviews draft PRs without voting. Automatically submits a vote when a draft transitions to active (vote-only flow). |
| **Review History** | Full history stored in PR properties (source of truth) and appended as a visible table in the PR description. |
| **AI Metrics** | Captures token counts (prompt, completion, total), model name, AI latency, and total review duration per review. |
| **Metrics API** | Dedicated endpoint returns full review history and aggregated AI metrics for any PR. |
| **Rate Limiting** | In-memory cooldown prevents the same PR from being reviewed too frequently. Configurable interval; blocks early before any API calls. |
| **Configurable Prompt** | Domain-specific review instructions loaded from a JSON file, injected between the fixed identity preamble and response format rules. |
| **Tag / Label** | Applies a decorative `ai-code-review` label to reviewed PRs for easy filtering in the PR list. |
| **Swagger UI** | Interactive API documentation available at `/swagger` in development mode. |

---

## Architecture

```
┌─────────────────────┐       ┌──────────────────────────┐
│  Azure DevOps       │       │  AI Code Review Service   │
│  Pipeline / Webhook  │──────▶│  ASP.NET Core Web API     │
└─────────────────────┘       │  (.NET 8)                 │
                              │                           │
                              │  ┌─────────────────────┐  │
                              │  │ ReviewController     │  │
                              │  └────────┬────────────┘  │
                              │           │               │
                              │  ┌────────▼────────────┐  │
                              │  │ CodeReviewOrchest-   │  │
                              │  │ rator                │  │
                              │  └──┬─────────────┬────┘  │
                              │     │             │       │
                              │  ┌──▼──┐    ┌─────▼────┐  │
                              │  │Azure│    │CodeReview │  │
                              │  │DevOps│    │Service   │  │
                              │  │Svc   │    │(AI)      │  │
                              │  └──┬───┘    └────┬─────┘  │
                              └─────┼─────────────┼───────┘
                                    │             │
                              ┌─────▼─────┐ ┌─────▼──────┐
                              │ Azure     │ │ Azure      │
                              │ DevOps    │ │ OpenAI     │
                              │ REST API  │ │ (any model)│
                              └───────────┘ └────────────┘
```

**Flow:**

1. A pipeline task (or manual call) sends `POST /api/review` with the PR details.
2. The **Rate Limiter** checks if the PR was reviewed too recently — rejects immediately if so.
3. The **Orchestrator** fetches PR state and metadata from Azure DevOps, then decides the action:
   - **Full Review** — first review; calls the AI, posts comments, votes.
   - **Re-Review** — new commits detected; calls the AI again, deduplicates comments.
   - **Vote Only** — draft-to-active transition with no code changes; submits vote only.
   - **Skip** — no changes since last review; records a skip event in history.
4. The **CodeReviewService** sends the diff to Azure OpenAI and parses the structured JSON response.
5. Results are posted back to the PR as inline comments, a summary thread, reviewer vote, tag, metadata, and history.

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
    "Mode": "single",
    "ActiveProvider": "azure-openai",
    "ConsensusThreshold": 2,
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
| `Mode` | string | `"single"` | `"single"` = use `ActiveProvider` only. `"consensus"` = fan out to ALL enabled providers and merge. |
| `ActiveProvider` | string | `"azure-openai"` | Which provider key to use when Mode = single. |
| `ConsensusThreshold` | int | `2` | In consensus mode, minimum providers that must flag a comment for it to be kept. |

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

The AI system prompt is assembled from three parts:

1. **Identity Preamble** (hardcoded) — Establishes the AI as a senior code reviewer.
2. **Custom Instructions** (optional, from file) — Domain-specific review guidance.
3. **Response Format Rules** (hardcoded) — Mandates the structured JSON output schema.

To customize what the AI checks, edit `custom-instructions.json`:

```json
{
  "customInstructions": "In addition to standard review criteria, also evaluate the following:\n\n- **Cyclomatic complexity**: Flag methods with high cyclomatic complexity (roughly >10) and suggest refactoring.\n- **Method length**: Flag methods longer than ~50 lines and suggest decomposition.\n- **Magic numbers/strings**: Flag hardcoded values that should be constants or configuration.\n- **Error handling**: Ensure exceptions are caught at appropriate levels and not silently swallowed.\n- **Async best practices**: Check for proper async/await usage — no sync-over-async, no fire-and-forget without justification."
}
```

If the file doesn't exist or the path is empty, no custom instructions are injected — the AI still performs a thorough review using its base instructions.

---

## Running the Service

### Local Development

```bash
# Navigate to the project
cd Projects/AiCodeReview/AiCodeReview

# Restore and build
dotnet build

# Run with development settings (reads appsettings.Development.json)
dotnet run --launch-profile http
```

The service starts on **http://localhost:5094**. Swagger UI is available at http://localhost:5094/swagger.

### Docker / Container

Create a `Dockerfile` in the `AiCodeReview/` directory:

```dockerfile
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY AiCodeReview/AiCodeReview.csproj AiCodeReview/
RUN dotnet restore AiCodeReview/AiCodeReview.csproj
COPY AiCodeReview/ AiCodeReview/
RUN dotnet publish AiCodeReview/AiCodeReview.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app .
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
ENTRYPOINT ["dotnet", "AiCodeReview.dll"]
```

```bash
# Build
docker build -t ai-code-review .

# Run with environment variables for secrets
docker run -d -p 8080:8080 \
  -e AzureDevOps__Organization="MyOrg" \
  -e AzureDevOps__PersonalAccessToken="your-pat" \
  -e AzureOpenAI__Endpoint="https://my-resource.openai.azure.com/" \
  -e AzureOpenAI__ApiKey="your-key" \
  -e AzureOpenAI__DeploymentName="gpt-4o" \
  ai-code-review
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
  "pullRequestId": 12345
}
```

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `projectName` | string | Yes | Azure DevOps project name. |
| `repositoryName` | string | Yes | Repository name or GUID. |
| `pullRequestId` | int | Yes | Pull request ID (must be > 0). |

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
  "vote": 5
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
| `vote` | int? | Vote cast: `10` (Approved), `5` (Approved w/ Suggestions), `-5` (Waiting for Author), `-10` (Rejected), or `null`. |

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
      "totalDurationMs": 8200
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

Simple health check endpoint.

**Request:**

```http
GET /api/review/health
```

**Response:**

```json
{
  "status": "healthy",
  "timestamp": "2026-02-14T00:55:00.000Z"
}
```

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
- **Token Counts** — Prompt tokens, completion tokens, total tokens.
- **Timing** — AI response time (`aiDurationMs`) and total end-to-end duration (`totalDurationMs`).

---

## Rate Limiting

The service includes an in-memory rate limiter that prevents the same PR from being reviewed too frequently. This protects against misuse and unnecessary AI API costs.

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

---

## Project Structure

```
AiCodeReview/
├── AiCodeReview.sln                    # Solution file
├── .gitignore
├── README.md
│
├── AiCodeReview/                       # Main web API project
│   ├── AiCodeReview.csproj
│   ├── Program.cs                      # DI setup, middleware pipeline
│   ├── appsettings.json                # Configuration template
│   ├── appsettings.Development.json    # Dev overrides (secrets — gitignored)
│   ├── custom-instructions.json        # Optional AI review instructions
│   │
│   ├── Controllers/
│   │   └── CodeReviewController.cs     # API endpoints
│   │
│   ├── Models/
│   │   ├── AiProviderSettings.cs       # Multi-provider AI configuration model
│   │   ├── AzureDevOpsSettings.cs      # Azure DevOps configuration model
│   │   ├── AzureOpenAISettings.cs      # Azure OpenAI configuration (legacy compat)
│   │   ├── ReviewRequest.cs            # POST request DTO
│   │   ├── ReviewResponse.cs           # POST response DTO
│   │   ├── ReviewMetricsResponse.cs    # GET metrics response DTO
│   │   ├── ReviewMetadata.cs           # PR properties metadata model
│   │   ├── ReviewStatusUpdate.cs       # Progress tracking model
│   │   ├── CodeReviewResult.cs         # AI review result model
│   │   └── PullRequestInfo.cs          # PR info fetched from Azure DevOps
│   │
│   ├── Services/
│   │   ├── CodeReviewOrchestrator.cs   # Main review flow orchestration
│   │   ├── ICodeReviewOrchestrator.cs  # Orchestrator interface
│   │   ├── AzureOpenAiReviewService.cs # Azure OpenAI provider implementation
│   │   ├── ConsensusReviewService.cs   # Multi-provider consensus aggregator
│   │   ├── CodeReviewServiceFactory.cs # Config-driven provider factory + DI
│   │   ├── ICodeReviewService.cs       # AI service interface (provider-agnostic)
│   │   ├── AzureDevOpsService.cs       # Azure DevOps REST API client
│   │   ├── IAzureDevOpsService.cs      # DevOps service interface
│   │   └── ReviewRateLimiter.cs        # In-memory rate limiter
│   │
│   └── Properties/
│       └── launchSettings.json         # Dev launch profiles
│
└── AiCodeReview.Tests/                 # MSTest integration tests
    ├── AiCodeReview.Tests.csproj
    ├── appsettings.Test.json           # Test configuration (legacy + multi-provider)
    ├── ReviewFlowIntegrationTests.cs   # Full lifecycle test (7 scenarios)
    ├── ReviewLifecycleTests.cs         # 5 parallel lifecycle tests
    ├── AiQualityVerificationTests.cs   # Known-bad-code LiveAI tests
    ├── ServiceIntegrationTests.cs      # Service-level tests (7 tests)
    ├── AiSmokeTest.cs                  # AI smoke tests (Manual category)
    └── Helpers/
        ├── FakeCodeReviewService.cs    # Deterministic AI replacement for tests
        ├── TestServiceBuilder.cs       # Shared DI builder (FakeAi + RealAi)
        └── TestPullRequestHelper.cs    # Disposable repo lifecycle + 6-layer safety
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
cd Projects/AiCodeReview

# Run all tests (excluding manual/ignored)
dotnet test --filter 'TestCategory!=Manual&FullyQualifiedName!~InspectPR&FullyQualifiedName!~CleanupTestPR'
```

### Test Categories

| Test File | Tests | Category | Description |
|-----------|-------|----------|-------------|
| `ReviewLifecycleTests.cs` | 5 | Integration | Independent, parallelizable lifecycle tests: first review, skip, re-review dedup, draft→active vote-only, reset + re-review. Each test creates its own disposable repo. Replaces the old monolithic 7-scenario test. |
| `ServiceIntegrationTests.cs` | 7 | Integration | History round-trips, ReviewCount, description table, tag resilience, metrics API, property read/write, description PATCH. Each test gets its own disposable repo. |
| `AiQualityVerificationTests.cs` | 3 | LiveAI | Push code with **known, deliberate issues** (hardcoded secrets, SQL injection, null derefs, resource leaks) and verify the real AI flags them. Includes a fix-and-reverify cycle. Run with `--filter TestCategory=LiveAI`. |
| `AiSmokeTest.cs` | 2 | Manual | Manual-only tests that call real Azure OpenAI (basic prompt + JSON mode). Run with `--filter TestCategory=Manual`. |
| `ReviewFlowIntegrationTests.cs` | 1 (Ignored) | — | Legacy monolithic 7-scenario lifecycle test. Replaced by `ReviewLifecycleTests.cs`. Kept for reference. |

### Test Infrastructure

| Component | Purpose |
|-----------|---------|
| `TestServiceBuilder.cs` | Shared DI container builder. `BuildWithFakeAi()` registers `FakeCodeReviewService` for deterministic tests. `BuildWithRealAi(modelOverride?)` registers the real `CodeReviewService` and optionally overrides the AI model deployment name. |
| `TestPullRequestHelper.cs` | Creates/manages disposable test repos with the 6-layer safety system (instance tracking, never-delete list, name prefix, marker file, creation recency, PAT ACL verification). |
| `FakeCodeReviewService.cs` | Deterministic fake with `ResultFactory` and `VerificationResultFactory` for custom per-test behavior. Produces 2 stable inline comments per file for dedup testing. |

### Running Tests

```bash
cd Projects/AiCodeReview

# All automated tests (fake AI — fast, no API cost)
dotnet test --filter 'TestCategory!=Manual&TestCategory!=LiveAI'

# LiveAI tests only (real Azure OpenAI — costs money, slower)
dotnet test --filter TestCategory=LiveAI

# Everything including LiveAI (but not manual)
dotnet test --filter 'TestCategory!=Manual'

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

Tests read from `appsettings.Test.json`:

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

## Test Roadmap / Future Enhancements

Planned improvements to the testing infrastructure:

| Enhancement | Description | Priority |
|-------------|-------------|----------|
| **Multi-model comparison** | Run the same `LiveAI` tests against different models (gpt-4o, gpt-4.1, gpt-5) and compare comment quality, latency, and cost. `TestServiceBuilder.BuildWithRealAi(modelOverride)` already supports this. | High |
| **Refactor ServiceIntegrationTests** | Migrate `ServiceIntegrationTests.cs` from its private `BuildServices()` to use `TestServiceBuilder.BuildWithFakeAi()` for consistency. | Medium |
| **Multi-file PR edge cases** | Test PRs with 10–50 changed files to verify parallel review, density threshold, and rate limiting under load. | Medium |
| **Large file density threshold** | Push a single file with 1000+ lines and only 2 changes to verify the density-based threshold correctly skips low-density files. | Medium |
| **Language-specific known-bad-code** | Expand `KnownBadCode` samples beyond C# to include TypeScript, Python, Java, and SQL to test the AI's cross-language review capability. | Low |
| **Flaky test detection** | Run `LiveAI` tests N times and track which assertions are non-deterministic due to AI variability. Adjust thresholds accordingly. | Low |
| **Cost & latency tracking** | Instrument `LiveAI` tests to log token usage and wall-clock time per model, enabling data-driven model selection. | Low |
| **Pipeline integration tests** | Stand up a real Azure DevOps pipeline webhook scenario end-to-end in a disposable project. | Future |

---

## License

Internal use only. Not for external distribution.
