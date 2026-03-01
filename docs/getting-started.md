# Getting Started

## Table of Contents

- [Prerequisites](#prerequisites)
- [Quick Start](#quick-start)
  - [Local Development](#local-development)
  - [Docker](#docker)
  - [Trigger a Review](#trigger-a-review)
- [Project Structure](#project-structure)
- [Dev Container](#dev-container)
- [Next Steps](#next-steps)

---

## Prerequisites

| Requirement | Details |
|-------------|---------|
| **.NET 10 SDK** | [Download](https://dotnet.microsoft.com/download/dotnet/10.0). Required to build and run. |
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

For full configuration details, see [Configuration](configuration.md).

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

For production deployment options (App Service, ACI, Kubernetes), see [Configuration → Environment Variables](configuration.md#environment-variables).

### Trigger a Review

```bash
curl -X POST http://localhost:5094/api/review \
  -H "Content-Type: application/json" \
  -d '{ "projectName": "MyProject", "repositoryName": "MyRepo", "pullRequestId": 12345 }'
```

For full API documentation, see [API Reference](api-reference.md). For pipeline integration, see [Pipeline Integration](pipeline-integration.md).

---

## Project Structure

```
HVO.AiCodeReview/
├── HVO.AiCodeReview.sln                # Solution file
├── .gitignore
├── README.md
├── CONTRIBUTING.md                     # PR workflow, coding standards
├── CHANGELOG.md                        # Release history
├── LICENSE                             # License
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
│   ├── copilot-instructions.md         # Copilot custom instructions
│   ├── PULL_REQUEST_TEMPLATE.md        # PR checklist template
│   ├── ISSUE_TEMPLATE/
│   │   ├── bug_report.md               # Bug report template
│   │   └── feature_request.md          # Feature request template
│   └── workflows/
│       └── ci.yml                      # GitHub Actions CI — build, test, coverage badges
│
├── scripts/
│   ├── ai-code-review.sh               # Bash pipeline script (curl/jq)
│   └── azure-pipelines-template.yml    # Azure Pipelines YAML template
│
├── docs/                               # Documentation
│   ├── getting-started.md              # Prerequisites & quick start
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
│       │   ├── IDevOpsService.cs           # DevOps service interface (provider-agnostic)
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
    └── HVO.AiCodeReview.Tests/         # MSTest unit + integration tests (509 total)
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
            ├── FakeDevOpsService.cs        # In-memory DevOps fake (no real backend)
            ├── TestServiceBuilder.cs       # Shared DI builder (FakeAi / FullyFake / RealAi)
            └── TestPullRequestHelper.cs    # Disposable repo + 6-layer safety
```

---

## Dev Container

The repo includes a full dev container configuration in `.devcontainer/` with .NET 10 (plus 8 and 9 for compatibility), Node.js, Python, Java, Go, Docker-in-Docker, Azure CLI, GitHub CLI, and more. PostgreSQL 16 and Redis 7 start automatically.

Open the repo in VS Code and select "Reopen in Container" when prompted.

---

## Next Steps

- **[Configuration](configuration.md)** — Full configuration reference for all settings.
- **[API Reference](api-reference.md)** — Endpoint documentation.
- **[Architecture](architecture.md)** — System design deep-dive.
- **[Testing](testing.md)** — Test guide and categories.
- **[Pipeline Integration](pipeline-integration.md)** — Azure DevOps pipeline setup.
- **[Contributing](../CONTRIBUTING.md)** — PR workflow and coding standards.
