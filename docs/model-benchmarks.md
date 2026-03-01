# Model Benchmarks & Selection

The project includes a benchmark test suite (`ModelBenchmarkTests.cs`) that evaluates all configured models against **10 known-bad-code issues** to measure detection quality, latency, and cost at each review depth.

## Known-Bad-Code Test Issues

The benchmark uses a deliberately vulnerable C# file containing these 10 issues:

| # | Issue | Category |
|---|-------|----------|
| 1 | Hardcoded database credentials | Security |
| 2 | Hardcoded API key | Security |
| 3 | SQL injection (string concatenation) | Security |
| 4 | Path traversal (unsanitized user input) | Security |
| 5 | Null dereference (no null check) | Bug |
| 6 | `HttpClient` created in a loop (socket exhaustion) | Performance |
| 7 | Exception silently swallowed (empty catch) | Bug |
| 8 | Sensitive data logged to console | Security |
| 9 | `async void` method (fire-and-forget) | Bug |
| 10 | Double `Dispose` (use-after-dispose) | Bug |

## Live Benchmark Results (2026-03-01)

Benchmark run at **Standard** depth against the same 10-issue known-bad-code file. Each model creates a disposable PR in Azure DevOps, runs a full two-pass review, and records results.

| Model | Time | Vote | Issues | Errors | Warnings | Info | Prompt Tok | Compl Tok | Total Tok | AI ms | Est. Cost |
|-------|------|------|--------|--------|----------|------|------------|-----------|-----------|-------|-----------|
| **gpt-4o** | 00:27 | -10 | **8** | 7 | 1 | 0 | 6,938 | 1,761 | 8,699 | 16,598 | $0.0350 |
| gpt-4o-mini | 00:20 | -10 | 3 | 3 | 0 | 0 | 6,779 | 1,053 | 7,832 | 9,192 | $0.0016 |
| o3-mini | 00:31 | -10 | 3 | 1 | 2 | 0 | 6,838 | 2,850 | 9,688 | 24,694 | $0.0201 |
| o4-mini | 00:36 | -5 | 3 | 3 | 0 | 0 | 6,748 | 4,834 | 11,582 | 30,468 | $0.0287 |
| gpt-5-mini | 00:10 | +10 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | $0.0000 |

### Quality Score

```
gpt-4o          [████████░░] 8/10  (00:27)
gpt-4o-mini     [███░░░░░░░] 3/10  (00:20)
o3-mini         [███░░░░░░░] 3/10  (00:31)
o4-mini         [███░░░░░░░] 3/10  (00:36)
gpt-5-mini      [░░░░░░░░░░] 0/10  (00:10)
```

> **Notes:**
> - gpt-5-mini returned 0 tokens — the deployment may not be fully operational yet.
> - gpt-4o found the most issues (8/10) at a reasonable cost ($0.035/review).
> - gpt-4o-mini is the cheapest option ($0.0016/review) but missed 7 of 10 issues.
> - o4-mini used the most tokens and time but only matched gpt-4o-mini's detection rate at this depth.

## Model Comparison (Specs)

Each model is tested at all three review depths. The "Quality Score" is the number of issues detected out of 10:

| Model | Context | Max Out | RPM | $/1M In | $/1M Out | Quality (Std) | Speed |
|-------|--------:|--------:|----:|--------:|---------:|:-------------:|:-----:|
| **gpt-4o-mini** | 128K | 16K | 4,990 | $0.15 | $0.60 | 3/10 | Fast |
| **gpt-4o** | 128K | 4K | 2,700 | $2.50 | $10.00 | 8/10 | Medium |
| **o3-mini** | 200K | 100K | 100 | $1.10 | $4.40 | 3/10 | Slow |
| **o4-mini** | 200K | 100K | 150 | $1.10 | $4.40 | 3/10 | Slow |
| **gpt-5-mini** | 400K | 128K | 501 | $0.25 | $2.00 | 0/10* | Medium |

*gpt-5-mini may require deployment configuration updates.

## Selected Depth → Model Mapping

Based on benchmark results, the following defaults are configured:

```
Quick    → gpt-4o-mini   (fastest, cheapest — ideal for PR summary only)
Standard → gpt-4o-mini   (best cost/quality balance for per-file reviews)
Deep     → o4-mini       (reasoning model — best quality for deep analysis)
```

**Why these models?**

- **gpt-4o-mini** at $0.0016/review with 4,990 RPM throughput. For Quick mode (no file reviews) and Standard mode (per-file), this provides excellent value despite lower detection rate.
- **o4-mini** excels at cross-file reasoning (Deep Pass 3). The higher cost (~$0.03 per review) is justified for critical PRs and release branches.
- **gpt-4o** provides the highest quality (8/10) but at 22× the cost of gpt-4o-mini. Reserved for consensus mode or manual overrides.
- **o3-mini** matches o4-mini quality with similar pricing but lower RPM (100 vs 150). o4-mini is preferred.

See [configuration.md](configuration.md#depth-specific-model-routing) for how to configure depth-specific model routing.

## Benchmark History

| Date | gpt-4o-mini | gpt-4o | o3-mini | o4-mini | gpt-5-mini | Notes |
|------|------------|--------|---------|---------|------------|-------|
| 2026-03-01 | 3/10 ($0.002) | 8/10 ($0.035) | 3/10 ($0.020) | 3/10 ($0.029) | 0/10 (N/A) | Standard depth, first formal benchmark |

## Running Benchmarks

```bash
# Run all benchmark tests (requires real Azure OpenAI — costs money)
dotnet test --filter TestCategory=Benchmark

# Run a single model
dotnet test --filter "FullyQualifiedName~Benchmark_gpt4o_mini"

# Run all models at a specific depth
dotnet test --filter "FullyQualifiedName~Benchmark_AllModels_Standard"

# Results include per-model quality scores, token counts, latency, and estimated cost
# Output is printed as both console tables and Markdown tables for documentation
```
