# AI Code Review Service

[![CI](https://github.com/RoySalisbury/HVO.AiCodeReview/actions/workflows/ci.yml/badge.svg)](https://github.com/RoySalisbury/HVO.AiCodeReview/actions/workflows/ci.yml)
![Tests](https://img.shields.io/endpoint?url=https://gist.githubusercontent.com/RoySalisbury/ee77620767face944a0df89232c56af1/raw/tests.json)
![Line Coverage](https://img.shields.io/endpoint?url=https://gist.githubusercontent.com/RoySalisbury/ee77620767face944a0df89232c56af1/raw/coverage-line.json)
![Branch Coverage](https://img.shields.io/endpoint?url=https://gist.githubusercontent.com/RoySalisbury/ee77620767face944a0df89232c56af1/raw/coverage-branch.json)
![.NET](https://img.shields.io/badge/.NET-10.0-blue)
![License](https://img.shields.io/badge/license-proprietary-lightgrey)

A centralized, AI-powered code review service for Azure DevOps pull requests. The service analyzes PR diffs using Azure OpenAI with configurable model deployments (GPT-4o, GPT-4o-mini, o4-mini, o3-mini, GPT-5-mini, etc.), posts inline comments and a summary thread, adds a reviewer vote, and tracks full review history with cost estimation — all driven by a single HTTP API call.

---

## Features

| Feature | Description |
|---------|-------------|
| **AI-Powered Review** | Azure OpenAI with configurable model deployments for structured reviews with per-file verdicts, inline comments, and observations. |
| **Two-Pass Architecture** | Pass 1 generates a cross-file PR summary. Pass 2 reviews each file individually with that context injected. |
| **Review Depth Modes** | Three depths — **Quick**, **Standard**, and **Deep** (+ holistic re-evaluation). |
| **Per-Pass Model Routing** | Each review pass can target a different AI model for cost optimization. |
| **Review Strategies** | **FileByFile**, **Vector** (Assistants API + Vector Store), and **Auto**. |
| **Layered Prompt Architecture** | Versioned rule catalog with scoped rules, priorities, and hot-reload. |
| **Cost Estimation** | Every review includes `EstimatedCost` (USD) from model-specific pricing. |
| **Inline PR Comments** | Targeted inline comments with severity levels (Bug, Security, Concern, Performance, Suggestion). |
| **Smart Re-Review** | Detects new commits, re-reviews changed code, deduplicates inline comments. |
| **Draft PR Awareness** | Reviews drafts without voting; auto-submits vote on draft→active transition. |
| **Multi-Provider Consensus** | Fan-out to multiple AI models; only comments meeting agreement threshold are posted. |
| **Rate Limiting** | PR-level cooldown + API-level 429 retry with global cooldown signal. |
| **Model Benchmarks** | Built-in benchmark suite with 10 known-bad-code scenarios. |

For the full architecture diagram and review flow, see [Architecture](docs/architecture.md).

---

## Quick Start

```bash
cd src/HVO.AiCodeReview
dotnet build
dotnet run --launch-profile http
# → http://localhost:5094/swagger
```

```bash
curl -X POST http://localhost:5094/api/review \
  -H "Content-Type: application/json" \
  -d '{ "projectName": "MyProject", "repositoryName": "MyRepo", "pullRequestId": 12345 }'
```

For prerequisites, Docker setup, and detailed instructions, see [Getting Started](docs/getting-started.md).

---

## Documentation

| Guide | Description |
|-------|-------------|
| [Getting Started](docs/getting-started.md) | Prerequisites, PAT setup, local/Docker quick start, project structure. |
| [Configuration](docs/configuration.md) | Application settings, multi-provider AI config, depth-specific model routing, environment variables, custom review instructions, Assistants API settings. |
| [API Reference](docs/api-reference.md) | `POST /api/review`, `GET /api/review/metrics`, `GET /api/review/health` — request/response formats, status codes, field reference. |
| [Architecture](docs/architecture.md) | Review depth modes, review strategies, two-pass architecture, review decision logic, review history & tracking, rate limiting, RPM-aware throttling & cost estimation. |
| [Pipeline Integration](docs/pipeline-integration.md) | Azure DevOps pipeline YAML, pipeline variables, optional fail-on-NeedsWork, scripts. |
| [Testing](docs/testing.md) | Disposable test repositories, 6-layer safety system, test categories (509 tests), test infrastructure, running tests, test configuration. |
| [Model Benchmarks](docs/model-benchmarks.md) | Known-bad-code test issues, live benchmark results, model comparison, depth → model mapping. |

---

## Testing

```bash
# All automated tests (fake AI + fake DevOps — fast, no API cost)
dotnet test --filter 'TestCategory!=Manual&TestCategory!=LiveAI&TestCategory!=Benchmark&TestCategory!=LiveDevOps'
```

See [Testing](docs/testing.md) for full details.

---

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md) for PR workflow, coding standards, and how to run tests locally.

---

## License

See [LICENSE](LICENSE). Internal use only. Not for external distribution.
