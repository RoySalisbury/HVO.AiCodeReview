# AI Code Review Service

A centralized, AI-powered code review service for Azure DevOps pull requests. The service analyzes PR diffs using Azure OpenAI with configurable model deployments (GPT-4o, GPT-4o-mini, o4-mini, o3-mini, GPT-5-mini, etc.), posts inline comments and a summary thread, adds a reviewer vote, and tracks full review history with cost estimation — all driven by a single HTTP API call.

---

## Table of Contents

- [Features](#features)
- [Architecture Overview](#architecture-overview)
- [Prerequisites](#prerequisites)
- [Quick Start](#quick-start)
- [Project Structure](#project-structure)
- [License](#license)

### Documentation

| Guide | Description |
|-------|-------------|
| [Configuration](docs/configuration.md) | Application settings, multi-provider AI config, depth-specific model routing, environment variables, custom review instructions (layered prompts, rule catalog, model adapters), Assistants API settings. |
| [API Reference](docs/api-reference.md) | `POST /api/review`, `GET /api/review/metrics`, `GET /api/review/health` — request/response formats, status codes, field reference. |
| [Architecture](docs/architecture.md) | Review depth modes (Quick/Standard/Deep), review strategies (FileByFile/Vector/Auto), two-pass architecture, review decision logic, review history & tracking, rate limiting, RPM-aware throttling & cost estimation. |
| [Pipeline Integration](docs/pipeline-integration.md) | Azure DevOps pipeline YAML, pipeline variables, optional fail-on-NeedsWork, optional gate-with-status, scripts. |
| [Testing](docs/testing.md) | Disposable test repositories, 6-layer safety system, test categories (417 tests), test infrastructure, running tests, manual utilities, test configuration, test roadmap. |
| [Model Benchmarks](docs/model-benchmarks.md) | Known-bad-code test issues, model comparison table, depth → model mapping, running benchmarks. |

---

## Features

| Feature | Description |
|---------|-------------|
| **AI-Powered Review** | Uses Azure OpenAI with configurable model deployments to analyze diffs and produce structured reviews with per-file verdicts, inline comments, and observations. |
| **Two-Pass Architecture** | Pass 1 generates a cross-file PR summary for context. Pass 2 reviews each file individually with that context injected, improving accuracy for multi-file changes. |
| **Review Depth Modes** | Three review depths — **Quick** (Pass 1 only), **Standard** (Pass 1 + Pass 2), and **Deep** (+ Pass 3 holistic re-evaluation). See [Architecture → Depth Modes](docs/architecture.md#review-depth-modes). |
| **Depth-Specific Model Routing** | Each review depth can target a different AI model — e.g., Quick → gpt-4o-mini, Deep → o4-mini. See [Configuration → Depth Models](docs/configuration.md#depth-specific-model-routing). |
| **Per-Pass Model Routing** | Each review pass (PR Summary, Per-File, Deep, Security, Thread Verification) can target a different AI model. See [Configuration → Pass Routing](docs/configuration.md#per-pass-model-routing-passrouting). |
| **Review Strategies** | Three strategies — **FileByFile**, **Vector** (Assistants API + Vector Store), and **Auto**. See [Architecture → Strategies](docs/architecture.md#review-strategies). |
| **Reasoning Model Support** | Full compatibility with o-series models (o1, o3-mini, o4-mini) — automatic parameter adaptation. |
| **Layered Prompt Architecture** | Versioned rule catalog with scoped rules, priorities, and hot-reload. See [Configuration → Custom Instructions](docs/configuration.md#custom-review-instructions). |
| **Per-Model Adapter** | Model-specific tuning for verbosity, severity calibration, pricing, rate limits, and context windows. |
| **RPM-Aware Throttling** | Automatic per-call delay based on model RPM limits with lock-free ticket-based concurrency. |
| **Cost Estimation** | Every review includes `EstimatedCost` (USD) from model-specific pricing and actual token usage. |
| **Inline PR Comments** | Targeted inline comments on specific lines with severity levels (Bug, Security, Concern, Performance, Suggestion). |
| **Summary Thread** | Comprehensive review summary with file inventory, per-file reviews, observations, and verdict. |
| **Reviewer Vote** | Automatic reviewer vote (Approved / Approved with Suggestions / Waiting for Author / Rejected). |
| **Smart Re-Review** | Detects new commits, re-reviews changed code, deduplicates inline comments. |
| **Draft PR Awareness** | Reviews drafts without voting; auto-submits vote on draft→active transition. |
| **Review History** | Full history in PR properties + visible table in PR description. |
| **Multi-Provider Consensus** | Fan-out to multiple AI models; only comments meeting agreement threshold are posted. |
| **Rate Limiting** | Two-tier: PR-level cooldown + API-level 429 retry with global cooldown signal. See [Architecture → Rate Limiting](docs/architecture.md#rate-limiting). |
| **Health Check** | `/api/review/health` verifies Azure DevOps connectivity. |
| **Model Benchmarks** | Built-in benchmark suite with 10 known-bad-code scenarios. See [Model Benchmarks](docs/model-benchmarks.md). |

---

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

For a detailed walkthrough of the review flow, depth modes, strategies, and decision logic, see [Architecture](docs/architecture.md).

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

For full configuration details, see [Configuration](docs/configuration.md).


---

## Quick Start

### Local Development

```bash
cd src/HVO.AiCodeReview
dotnet build
dotnet run --launch-profile http
```

The service starts on **http://localhost:5094**. Swagger UI is available at http://localhost:5094/swagger.

### Docker

```bash
docker build -f src/HVO.AiCodeReview/Dockerfile -t hvo-ai-code-review .

docker run -d -p 8080:8080 \
  -e AzureDevOps__Organization="MyOrg" \
  -e AzureDevOps__PersonalAccessToken="your-pat" \
  -e AzureOpenAI__Endpoint="https://my-resource.openai.azure.com/" \
  -e AzureOpenAI__ApiKey="your-key" \
  -e AzureOpenAI__DeploymentName="gpt-4o" \
  hvo-ai-code-review
```

For production deployment options (App Service, ACI, Kubernetes), see [Configuration](docs/configuration.md#environment-variables).

### Trigger a Review

```bash
curl -X POST http://localhost:5094/api/review \
  -H "Content-Type: application/json" \
  -d '{ "projectName": "MyProject", "repositoryName": "MyRepo", "pullRequestId": 12345 }'
```

For full API documentation, see [API Reference](docs/api-reference.md). For pipeline integration, see [Pipeline Integration](docs/pipeline-integration.md).

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
│   ├── architecture.md                 # Architecture deep-dive
│   ├── api-reference.md                # API endpoint reference
│   ├── configuration.md                # Configuration guide
│   ├── model-benchmarks.md             # Model benchmarks & selection
│   ├── pipeline-integration.md         # Azure DevOps pipeline setup
│   └── testing.md                      # Testing guide & categories
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
│       │   ├── AiProviderSettings.cs       # Multi-provider AI configuration + DepthModels + PassRouting
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
│       │   ├── ReviewPass.cs               # ReviewPass enum (PrSummary, PerFileReview, DeepReview, SecurityPass, ThreadVerification)
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
│       │   ├── PassModelResolver.cs        # Pass → ICodeReviewService routing
│       │   ├── ICodeReviewServiceResolver.cs # Pass-aware service resolution interface
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
    └── HVO.AiCodeReview.Tests/         # MSTest unit + integration tests (417 total)
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
        ├── ResilienceTests.cs              # 7 Azure DevOps HTTP resilience tests
        ├── PassModelResolverTests.cs       # 10 per-pass model routing tests
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

417 tests across unit, integration, LiveAI, and benchmark categories. Tests run against disposable Azure DevOps repositories with a 6-layer safety system to prevent accidental deletion.

```bash
# All automated tests (fake AI — fast, no API cost)
dotnet test --filter 'TestCategory!=Manual&TestCategory!=LiveAI&TestCategory!=Benchmark'
```

For full details — test categories, infrastructure, configuration, manual utilities, and roadmap — see [Testing](docs/testing.md).

---

## Dev Container

The repo includes a full dev container configuration in `.devcontainer/` with .NET 8/9/10, Node.js, Python, Java, Go, Docker-in-Docker, Azure CLI, GitHub CLI, and more. PostgreSQL 16 and Redis 7 start automatically.

Open the repo in VS Code and select "Reopen in Container" when prompted.

---

## License

Internal use only. Not for external distribution.
