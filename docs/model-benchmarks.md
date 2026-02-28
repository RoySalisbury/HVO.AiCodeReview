# Model Benchmarks & Selection

The project includes a benchmark test suite (`ModelBenchmarkTests.cs`) that evaluates all configured models against **10 known-bad-code issues** to measure detection quality, latency, and cost at each review depth.

## Known-Bad-Code Test Issues

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

## Model Comparison

Each model is tested at all three review depths. The "Quality Score" is the number of issues detected out of 10:

| Model | Context | Max Out | RPM | $/1M In | $/1M Out | Quality (Std) | Speed |
|-------|--------:|--------:|----:|--------:|---------:|:-------------:|:-----:|
| **gpt-4o-mini** | 128K | 16K | 4,990 | $0.15 | $0.60 | 7–8/10 | Fast |
| **gpt-4o** | 128K | 4K | 2,700 | $2.50 | $10.00 | 8–9/10 | Medium |
| **o3-mini** | 200K | 100K | 100 | $1.10 | $4.40 | 8–9/10 | Slow |
| **o4-mini** | 200K | 100K | 150 | $1.10 | $4.40 | 9–10/10 | Slow |
| **gpt-5-mini** | 400K | 128K | 501 | $0.25 | $2.00 | 8–9/10 | Medium |

## Selected Depth → Model Mapping

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

See [configuration.md](configuration.md#depth-specific-model-routing) for how to configure depth-specific model routing.

## Running Benchmarks

```bash
# Run all benchmark tests (requires real Azure OpenAI — costs money)
dotnet test --filter TestCategory=Benchmark

# Results include per-model quality scores, token counts, latency, and estimated cost
# Output is printed as both console tables and Markdown tables for documentation
```
