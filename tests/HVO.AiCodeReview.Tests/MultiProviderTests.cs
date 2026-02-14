using AiCodeReview.Models;
using AiCodeReview.Services;
using AiCodeReview.Tests.Helpers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AiCodeReview.Tests;

/// <summary>
/// Tests for the multi-provider architecture:
///   - <see cref="CodeReviewServiceFactory"/> (DI registration, fallback, unknown type)
///   - <see cref="ConsensusReviewService"/> (merge logic, threshold, vote, error isolation)
///
/// All tests use in-memory <see cref="FakeCodeReviewService"/> instances —
/// no real AI calls are made.
/// </summary>
[TestClass]
public class MultiProviderTests
{
    // ═══════════════════════════════════════════════════════════════════════
    //  Factory: Legacy Fallback
    // ═══════════════════════════════════════════════════════════════════════

    [TestMethod]
    public void Factory_NoProviders_FallsBackToLegacyAzureOpenAI()
    {
        // Config with AzureOpenAI but NO AiProvider:Providers
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AzureOpenAI:Endpoint"] = "https://test.openai.azure.com/",
                ["AzureOpenAI:ApiKey"] = "test-key-12345",
                ["AzureOpenAI:DeploymentName"] = "gpt-4o",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging(b => b.AddConsole());
        services.AddCodeReviewService(config);

        var sp = services.BuildServiceProvider();
        var svc = sp.GetRequiredService<ICodeReviewService>();

        Assert.IsInstanceOfType(svc, typeof(AzureOpenAiReviewService),
            "Should fall back to AzureOpenAiReviewService when no providers configured.");
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Factory: Single Provider from Config
    // ═══════════════════════════════════════════════════════════════════════

    [TestMethod]
    public void Factory_SingleProvider_CreatesCorrectService()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AiProvider:Mode"] = "single",
                ["AiProvider:ActiveProvider"] = "azure-openai",
                ["AiProvider:Providers:azure-openai:Type"] = "azure-openai",
                ["AiProvider:Providers:azure-openai:DisplayName"] = "Test GPT-4o",
                ["AiProvider:Providers:azure-openai:Endpoint"] = "https://test.openai.azure.com/",
                ["AiProvider:Providers:azure-openai:ApiKey"] = "test-key-12345",
                ["AiProvider:Providers:azure-openai:Model"] = "gpt-4o",
                ["AiProvider:Providers:azure-openai:Enabled"] = "true",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging(b => b.AddConsole());
        services.AddCodeReviewService(config);

        var sp = services.BuildServiceProvider();
        var svc = sp.GetRequiredService<ICodeReviewService>();

        Assert.IsInstanceOfType(svc, typeof(AzureOpenAiReviewService),
            "Single-provider mode with azure-openai type should create AzureOpenAiReviewService.");
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Factory: Consensus Mode Creates ConsensusReviewService
    // ═══════════════════════════════════════════════════════════════════════

    [TestMethod]
    public void Factory_ConsensusMode_CreatesConsensusService()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AiProvider:Mode"] = "consensus",
                ["AiProvider:ConsensusThreshold"] = "2",
                ["AiProvider:Providers:model-a:Type"] = "azure-openai",
                ["AiProvider:Providers:model-a:DisplayName"] = "Model A",
                ["AiProvider:Providers:model-a:Endpoint"] = "https://test.openai.azure.com/",
                ["AiProvider:Providers:model-a:ApiKey"] = "key-a",
                ["AiProvider:Providers:model-a:Model"] = "gpt-4o",
                ["AiProvider:Providers:model-a:Enabled"] = "true",
                ["AiProvider:Providers:model-b:Type"] = "azure-openai",
                ["AiProvider:Providers:model-b:DisplayName"] = "Model B",
                ["AiProvider:Providers:model-b:Endpoint"] = "https://test2.openai.azure.com/",
                ["AiProvider:Providers:model-b:ApiKey"] = "key-b",
                ["AiProvider:Providers:model-b:Model"] = "gpt-4.1",
                ["AiProvider:Providers:model-b:Enabled"] = "true",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging(b => b.AddConsole());
        services.AddCodeReviewService(config);

        var sp = services.BuildServiceProvider();
        var svc = sp.GetRequiredService<ICodeReviewService>();

        Assert.IsInstanceOfType(svc, typeof(ConsensusReviewService),
            "Consensus mode should create ConsensusReviewService.");
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Factory: Unknown Provider Type Throws
    // ═══════════════════════════════════════════════════════════════════════

    [TestMethod]
    public void Factory_UnknownProviderType_Throws()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AiProvider:Mode"] = "single",
                ["AiProvider:ActiveProvider"] = "mystery-ai",
                ["AiProvider:Providers:mystery-ai:Type"] = "nonexistent-provider",
                ["AiProvider:Providers:mystery-ai:DisplayName"] = "Mystery",
                ["AiProvider:Providers:mystery-ai:Endpoint"] = "http://localhost:9999",
                ["AiProvider:Providers:mystery-ai:ApiKey"] = "key",
                ["AiProvider:Providers:mystery-ai:Model"] = "model",
                ["AiProvider:Providers:mystery-ai:Enabled"] = "true",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging(b => b.AddConsole());
        services.AddCodeReviewService(config);

        var sp = services.BuildServiceProvider();

        var ex = Assert.ThrowsException<InvalidOperationException>(
            () => sp.GetRequiredService<ICodeReviewService>());

        Assert.IsTrue(ex.Message.Contains("nonexistent-provider"),
            $"Exception should mention the unknown type. Got: {ex.Message}");
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Factory: Disabled Provider Is Skipped
    // ═══════════════════════════════════════════════════════════════════════

    [TestMethod]
    public void Factory_DisabledProvider_IsSkipped()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AiProvider:Mode"] = "single",
                ["AiProvider:ActiveProvider"] = "active-one",
                ["AiProvider:Providers:active-one:Type"] = "azure-openai",
                ["AiProvider:Providers:active-one:DisplayName"] = "Active",
                ["AiProvider:Providers:active-one:Endpoint"] = "https://test.openai.azure.com/",
                ["AiProvider:Providers:active-one:ApiKey"] = "key",
                ["AiProvider:Providers:active-one:Model"] = "gpt-4o",
                ["AiProvider:Providers:active-one:Enabled"] = "true",
                // Second provider disabled
                ["AiProvider:Providers:disabled-one:Type"] = "azure-openai",
                ["AiProvider:Providers:disabled-one:DisplayName"] = "Disabled",
                ["AiProvider:Providers:disabled-one:Endpoint"] = "https://test2.openai.azure.com/",
                ["AiProvider:Providers:disabled-one:ApiKey"] = "key2",
                ["AiProvider:Providers:disabled-one:Model"] = "gpt-4o",
                ["AiProvider:Providers:disabled-one:Enabled"] = "false",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging(b => b.AddConsole());
        services.AddCodeReviewService(config);

        var sp = services.BuildServiceProvider();
        var svc = sp.GetRequiredService<ICodeReviewService>();

        // Should be single AzureOpenAiReviewService (not consensus — only 1 enabled)
        Assert.IsInstanceOfType(svc, typeof(AzureOpenAiReviewService),
            "With only one enabled provider in single mode, should create AzureOpenAiReviewService.");
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Consensus: Overlapping Comments from Two Providers Are Kept
    // ═══════════════════════════════════════════════════════════════════════

    [TestMethod]
    public async Task Consensus_OverlappingComments_AreKept()
    {
        // Provider A flags file.cs lines 5-10
        var providerA = new FakeCodeReviewService
        {
            ResultFactory = (_, _) => new CodeReviewResult
            {
                Summary = new ReviewSummary { Verdict = "APPROVED WITH SUGGESTIONS", Description = "A says suggestions" },
                FileReviews = new List<FileReview> { new() { FilePath = "/file.cs", Verdict = "APPROVED" } },
                InlineComments = new List<InlineComment>
                {
                    new() { FilePath = "/file.cs", StartLine = 5, EndLine = 10, Comment = "Null check missing" },
                },
                RecommendedVote = 5,
            }
        };

        // Provider B flags file.cs lines 6-11 (overlaps with A within tolerance)
        var providerB = new FakeCodeReviewService
        {
            ResultFactory = (_, _) => new CodeReviewResult
            {
                Summary = new ReviewSummary { Verdict = "APPROVED", Description = "B says approved" },
                FileReviews = new List<FileReview> { new() { FilePath = "/file.cs", Verdict = "APPROVED" } },
                InlineComments = new List<InlineComment>
                {
                    new() { FilePath = "/file.cs", StartLine = 6, EndLine = 11, Comment = "Missing null guard" },
                },
                RecommendedVote = 10,
            }
        };

        var providers = new List<(string, ICodeReviewService)>
        {
            ("ModelA", providerA),
            ("ModelB", providerB),
        };

        var logger = LoggerFactory.Create(b => b.AddConsole()).CreateLogger<ConsensusReviewService>();
        var consensus = new ConsensusReviewService(providers, threshold: 2, logger);

        var prInfo = new PullRequestInfo { PullRequestId = 1, Title = "Test PR" };
        var files = new List<FileChange> { new() { FilePath = "/file.cs", ModifiedContent = "// test" } };

        var result = await consensus.ReviewAsync(prInfo, files);

        // Both flagged overlapping lines → should keep the comment
        Assert.AreEqual(1, result.InlineComments.Count,
            "Overlapping comments from two providers should be merged into one.");
        Assert.IsTrue(result.InlineComments[0].Comment.Contains("ModelA"),
            "Consensus comment should be attributed to ModelA.");
        Assert.IsTrue(result.InlineComments[0].Comment.Contains("ModelB"),
            "Consensus comment should be attributed to ModelB.");

        Console.WriteLine($"  ✓ Consensus merged: {result.InlineComments[0].Comment}");
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Consensus: Non-Overlapping Comments Are Filtered Out
    // ═══════════════════════════════════════════════════════════════════════

    [TestMethod]
    public async Task Consensus_NonOverlappingComments_AreFilteredOut()
    {
        // Provider A flags file.cs lines 5-10
        var providerA = new FakeCodeReviewService
        {
            ResultFactory = (_, _) => new CodeReviewResult
            {
                Summary = new ReviewSummary { Verdict = "APPROVED WITH SUGGESTIONS", Description = "A" },
                InlineComments = new List<InlineComment>
                {
                    new() { FilePath = "/file.cs", StartLine = 5, EndLine = 10, Comment = "Issue at top" },
                },
                RecommendedVote = 5,
            }
        };

        // Provider B flags file.cs lines 50-55 (no overlap)
        var providerB = new FakeCodeReviewService
        {
            ResultFactory = (_, _) => new CodeReviewResult
            {
                Summary = new ReviewSummary { Verdict = "APPROVED", Description = "B" },
                InlineComments = new List<InlineComment>
                {
                    new() { FilePath = "/file.cs", StartLine = 50, EndLine = 55, Comment = "Issue at bottom" },
                },
                RecommendedVote = 10,
            }
        };

        var providers = new List<(string, ICodeReviewService)>
        {
            ("ModelA", providerA),
            ("ModelB", providerB),
        };

        var logger = LoggerFactory.Create(b => b.AddConsole()).CreateLogger<ConsensusReviewService>();
        var consensus = new ConsensusReviewService(providers, threshold: 2, logger);

        var prInfo = new PullRequestInfo { PullRequestId = 1, Title = "Test PR" };
        var files = new List<FileChange> { new() { FilePath = "/file.cs", ModifiedContent = "// test" } };

        var result = await consensus.ReviewAsync(prInfo, files);

        // No overlap → both filtered out
        Assert.AreEqual(0, result.InlineComments.Count,
            "Non-overlapping comments should be filtered out when threshold=2.");

        Console.WriteLine($"  ✓ Non-overlapping comments filtered (threshold=2).");
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Consensus: Threshold=1 Keeps All Comments
    // ═══════════════════════════════════════════════════════════════════════

    [TestMethod]
    public async Task Consensus_Threshold1_KeepsAllComments()
    {
        var providerA = new FakeCodeReviewService
        {
            ResultFactory = (_, _) => new CodeReviewResult
            {
                Summary = new ReviewSummary { Verdict = "APPROVED", Description = "A" },
                InlineComments = new List<InlineComment>
                {
                    new() { FilePath = "/file.cs", StartLine = 5, EndLine = 10, Comment = "Only A sees this" },
                },
                RecommendedVote = 10,
            }
        };

        var providerB = new FakeCodeReviewService
        {
            ResultFactory = (_, _) => new CodeReviewResult
            {
                Summary = new ReviewSummary { Verdict = "APPROVED", Description = "B" },
                InlineComments = new List<InlineComment>
                {
                    new() { FilePath = "/other.cs", StartLine = 1, EndLine = 5, Comment = "Only B sees this" },
                },
                RecommendedVote = 10,
            }
        };

        var providers = new List<(string, ICodeReviewService)>
        {
            ("ModelA", providerA),
            ("ModelB", providerB),
        };

        var logger = LoggerFactory.Create(b => b.AddConsole()).CreateLogger<ConsensusReviewService>();
        var consensus = new ConsensusReviewService(providers, threshold: 1, logger);

        var prInfo = new PullRequestInfo { PullRequestId = 1, Title = "Test PR" };
        var files = new List<FileChange> { new() { FilePath = "/file.cs", ModifiedContent = "// test" } };

        var result = await consensus.ReviewAsync(prInfo, files);

        // Threshold 1 → every comment passes
        Assert.AreEqual(2, result.InlineComments.Count,
            "Threshold=1 should keep all comments from all providers.");

        Console.WriteLine($"  ✓ Threshold=1 keeps all {result.InlineComments.Count} comments.");
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Consensus: Uses Lowest (Most Critical) Vote
    // ═══════════════════════════════════════════════════════════════════════

    [TestMethod]
    public async Task Consensus_UsesLowestVote()
    {
        var providerA = new FakeCodeReviewService
        {
            ResultFactory = (_, _) => new CodeReviewResult
            {
                Summary = new ReviewSummary { Verdict = "APPROVED", Description = "A" },
                RecommendedVote = 10, // Approved
            }
        };

        var providerB = new FakeCodeReviewService
        {
            ResultFactory = (_, _) => new CodeReviewResult
            {
                Summary = new ReviewSummary { Verdict = "NEEDS WORK", Description = "B" },
                RecommendedVote = -5, // Rejected
            }
        };

        var providers = new List<(string, ICodeReviewService)>
        {
            ("Optimist", providerA),
            ("Pessimist", providerB),
        };

        var logger = LoggerFactory.Create(b => b.AddConsole()).CreateLogger<ConsensusReviewService>();
        var consensus = new ConsensusReviewService(providers, threshold: 1, logger);

        var prInfo = new PullRequestInfo { PullRequestId = 1, Title = "Test PR" };
        var files = new List<FileChange> { new() { FilePath = "/file.cs", ModifiedContent = "// test" } };

        var result = await consensus.ReviewAsync(prInfo, files);

        Assert.AreEqual(-5, result.RecommendedVote,
            "Consensus should use the lowest (most critical) vote.");

        Console.WriteLine($"  ✓ Lowest vote used: {result.RecommendedVote}.");
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Consensus: Harshest Verdict Wins
    // ═══════════════════════════════════════════════════════════════════════

    [TestMethod]
    public async Task Consensus_HarshestVerdictWins()
    {
        var providerA = new FakeCodeReviewService
        {
            ResultFactory = (_, _) => new CodeReviewResult
            {
                Summary = new ReviewSummary { Verdict = "APPROVED", Description = "All good." },
                RecommendedVote = 10,
            }
        };

        var providerB = new FakeCodeReviewService
        {
            ResultFactory = (_, _) => new CodeReviewResult
            {
                Summary = new ReviewSummary { Verdict = "REJECTED", Description = "Major issues." },
                RecommendedVote = -10,
            }
        };

        var providers = new List<(string, ICodeReviewService)>
        {
            ("Gentle", providerA),
            ("Strict", providerB),
        };

        var logger = LoggerFactory.Create(b => b.AddConsole()).CreateLogger<ConsensusReviewService>();
        var consensus = new ConsensusReviewService(providers, threshold: 1, logger);

        var prInfo = new PullRequestInfo { PullRequestId = 1, Title = "Test PR" };
        var files = new List<FileChange> { new() { FilePath = "/file.cs", ModifiedContent = "// test" } };

        var result = await consensus.ReviewAsync(prInfo, files);

        Assert.AreEqual("REJECTED", result.Summary.Verdict,
            "Consensus should use the harshest verdict.");
        Assert.IsTrue(result.Summary.Description.Contains("Consensus from 2 providers"),
            "Summary should note consensus source.");

        Console.WriteLine($"  ✓ Harshest verdict: {result.Summary.Verdict}.");
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Consensus: One Provider Fails, Other Still Completes
    // ═══════════════════════════════════════════════════════════════════════

    [TestMethod]
    public async Task Consensus_OneProviderFails_OtherStillCompletes()
    {
        // Provider A always throws
        var providerA = new FakeCodeReviewService
        {
            ResultFactory = (_, _) => throw new HttpRequestException("Simulated API failure"),
        };

        // Provider B returns normally
        var providerB = new FakeCodeReviewService
        {
            ResultFactory = (_, _) => new CodeReviewResult
            {
                Summary = new ReviewSummary { Verdict = "APPROVED", Description = "Good code." },
                InlineComments = new List<InlineComment>
                {
                    new() { FilePath = "/file.cs", StartLine = 1, EndLine = 5, Comment = "Minor style issue" },
                },
                RecommendedVote = 5,
            }
        };

        var providers = new List<(string, ICodeReviewService)>
        {
            ("FailingModel", providerA),
            ("WorkingModel", providerB),
        };

        var logger = LoggerFactory.Create(b => b.AddConsole()).CreateLogger<ConsensusReviewService>();
        // threshold=1 so remaining provider's comments pass
        var consensus = new ConsensusReviewService(providers, threshold: 1, logger);

        var prInfo = new PullRequestInfo { PullRequestId = 1, Title = "Test PR" };
        var files = new List<FileChange> { new() { FilePath = "/file.cs", ModifiedContent = "// test" } };

        var result = await consensus.ReviewAsync(prInfo, files);

        // Should still return a result from the working provider
        Assert.AreEqual("APPROVED", result.Summary.Verdict);
        Assert.IsTrue(result.InlineComments.Count > 0,
            "Working provider's comments should be included.");

        Console.WriteLine($"  ✓ Error isolation: one provider failed, result still returned.");
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Consensus: All Providers Fail → AggregateException
    // ═══════════════════════════════════════════════════════════════════════

    [TestMethod]
    public async Task Consensus_AllProvidersFail_ThrowsAggregateException()
    {
        var providerA = new FakeCodeReviewService
        {
            ResultFactory = (_, _) => throw new HttpRequestException("A failed"),
        };

        var providerB = new FakeCodeReviewService
        {
            ResultFactory = (_, _) => throw new TimeoutException("B timed out"),
        };

        var providers = new List<(string, ICodeReviewService)>
        {
            ("A", providerA),
            ("B", providerB),
        };

        var logger = LoggerFactory.Create(b => b.AddConsole()).CreateLogger<ConsensusReviewService>();
        var consensus = new ConsensusReviewService(providers, threshold: 1, logger);

        var prInfo = new PullRequestInfo { PullRequestId = 1, Title = "Test PR" };
        var files = new List<FileChange> { new() { FilePath = "/file.cs", ModifiedContent = "// test" } };

        await Assert.ThrowsExceptionAsync<AggregateException>(
            () => consensus.ReviewAsync(prInfo, files),
            "Should throw AggregateException when all providers fail.");

        Console.WriteLine($"  ✓ All-fail scenario throws AggregateException.");
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Consensus: Thread Verification Uses Majority Vote
    // ═══════════════════════════════════════════════════════════════════════

    [TestMethod]
    public async Task Consensus_ThreadVerification_UsesMajorityVote()
    {
        // Provider A says thread 1 is fixed, thread 2 is NOT fixed
        var providerA = new FakeCodeReviewService
        {
            VerificationResultFactory = candidates => candidates.Select(c => new ThreadVerificationResult
            {
                ThreadId = c.ThreadId,
                IsFixed = c.ThreadId == 1, // thread 1 fixed, thread 2 not
                Reasoning = $"A: thread {c.ThreadId}",
            }).ToList(),
        };

        // Provider B says thread 1 is fixed, thread 2 IS fixed
        var providerB = new FakeCodeReviewService
        {
            VerificationResultFactory = candidates => candidates.Select(c => new ThreadVerificationResult
            {
                ThreadId = c.ThreadId,
                IsFixed = true, // both fixed according to B
                Reasoning = $"B: thread {c.ThreadId}",
            }).ToList(),
        };

        // Provider C says thread 1 is NOT fixed, thread 2 is NOT fixed
        var providerC = new FakeCodeReviewService
        {
            VerificationResultFactory = candidates => candidates.Select(c => new ThreadVerificationResult
            {
                ThreadId = c.ThreadId,
                IsFixed = false, // neither fixed according to C
                Reasoning = $"C: thread {c.ThreadId}",
            }).ToList(),
        };

        var providers = new List<(string, ICodeReviewService)>
        {
            ("A", providerA),
            ("B", providerB),
            ("C", providerC),
        };

        var logger = LoggerFactory.Create(b => b.AddConsole()).CreateLogger<ConsensusReviewService>();
        var consensus = new ConsensusReviewService(providers, threshold: 2, logger);

        var candidates = new List<ThreadVerificationCandidate>
        {
            new() { ThreadId = 1, OriginalComment = "Fix this", CurrentCode = "fixed code" },
            new() { ThreadId = 2, OriginalComment = "Fix that", CurrentCode = "still broken" },
        };

        var results = await consensus.VerifyThreadResolutionsAsync(candidates);

        Assert.AreEqual(2, results.Count);

        // Thread 1: A=fixed, B=fixed, C=not → 2/3 fixed → majority says fixed
        var t1 = results.First(r => r.ThreadId == 1);
        Assert.IsTrue(t1.IsFixed, "Thread 1: 2/3 say fixed → should be fixed.");

        // Thread 2: A=not, B=fixed, C=not → 1/3 fixed → majority says NOT fixed
        var t2 = results.First(r => r.ThreadId == 2);
        Assert.IsFalse(t2.IsFixed, "Thread 2: 1/3 say fixed → should NOT be fixed.");

        // Verify attribution in reasoning
        Assert.IsTrue(t1.Reasoning.Contains("Consensus:"), "Reasoning should mention Consensus.");

        Console.WriteLine($"  ✓ Majority vote: Thread1={t1.IsFixed}, Thread2={t2.IsFixed}.");
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Consensus: ReviewFileAsync Fan-out Works
    // ═══════════════════════════════════════════════════════════════════════

    [TestMethod]
    public async Task Consensus_ReviewFileAsync_FansOutCorrectly()
    {
        

        var providerA = new FakeCodeReviewService();
        var providerB = new FakeCodeReviewService();

        // Track calls via the ResultFactory — but for ReviewFileAsync we need
        // to verify it actually calls ReviewFileAsync (the FakeCodeReviewService
        // handles this natively via its ReviewFileAsync method).
        var providers = new List<(string, ICodeReviewService)>
        {
            ("A", providerA),
            ("B", providerB),
        };

        var logger = LoggerFactory.Create(b => b.AddConsole()).CreateLogger<ConsensusReviewService>();
        var consensus = new ConsensusReviewService(providers, threshold: 1, logger);

        var prInfo = new PullRequestInfo { PullRequestId = 1, Title = "Test PR" };
        var file = new FileChange { FilePath = "/single.cs", ModifiedContent = "// test", ChangeType = "add" };

        var result = await consensus.ReviewFileAsync(prInfo, file, totalFilesInPr: 5);

        Assert.IsNotNull(result, "ReviewFileAsync should return a result.");
        Assert.IsNotNull(result.Summary, "Result should have a summary.");
        // Both providers return 2 inline comments each for 1 file → with threshold=1, all 4 kept
        // But dedup within consensus: same file+line from both providers overlap → merged
        Assert.IsTrue(result.InlineComments.Count > 0,
            "Should have inline comments from the fan-out.");

        Console.WriteLine($"  ✓ ReviewFileAsync fan-out: {result.InlineComments.Count} consensus comments.");
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Consensus: Metrics Are Summed Across Providers
    // ═══════════════════════════════════════════════════════════════════════

    [TestMethod]
    public async Task Consensus_MetricsAreSummed()
    {
        var providerA = new FakeCodeReviewService
        {
            ResultFactory = (_, _) => new CodeReviewResult
            {
                Summary = new ReviewSummary { Verdict = "APPROVED", Description = "A" },
                RecommendedVote = 10,
                PromptTokens = 100,
                CompletionTokens = 50,
                TotalTokens = 150,
                AiDurationMs = 2000,
                ModelName = "gpt-4o",
            }
        };

        var providerB = new FakeCodeReviewService
        {
            ResultFactory = (_, _) => new CodeReviewResult
            {
                Summary = new ReviewSummary { Verdict = "APPROVED", Description = "B" },
                RecommendedVote = 10,
                PromptTokens = 200,
                CompletionTokens = 80,
                TotalTokens = 280,
                AiDurationMs = 3000,
                ModelName = "gpt-4.1",
            }
        };

        var providers = new List<(string, ICodeReviewService)>
        {
            ("A", providerA),
            ("B", providerB),
        };

        var logger = LoggerFactory.Create(b => b.AddConsole()).CreateLogger<ConsensusReviewService>();
        var consensus = new ConsensusReviewService(providers, threshold: 1, logger);

        var prInfo = new PullRequestInfo { PullRequestId = 1, Title = "Test PR" };
        var files = new List<FileChange> { new() { FilePath = "/file.cs", ModifiedContent = "// test" } };

        var result = await consensus.ReviewAsync(prInfo, files);

        Assert.AreEqual(300, result.PromptTokens, "Prompt tokens should be summed.");
        Assert.AreEqual(130, result.CompletionTokens, "Completion tokens should be summed.");
        Assert.AreEqual(430, result.TotalTokens, "Total tokens should be summed.");
        Assert.AreEqual(3000, result.AiDurationMs, "Duration should use max.");
        Assert.IsTrue(result.ModelName!.Contains("gpt-4o"), "Model name should include both models.");
        Assert.IsTrue(result.ModelName!.Contains("gpt-4.1"), "Model name should include both models.");

        Console.WriteLine($"  ✓ Metrics summed: {result.TotalTokens} tokens, {result.AiDurationMs}ms, model={result.ModelName}.");
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  AiProviderSettings: Defaults Are Sensible
    // ═══════════════════════════════════════════════════════════════════════

    [TestMethod]
    public void AiProviderSettings_DefaultsAreSensible()
    {
        var settings = new AiProviderSettings();

        Assert.AreEqual("single", settings.Mode);
        Assert.AreEqual("azure-openai", settings.ActiveProvider);
        Assert.AreEqual(5, settings.MaxParallelReviews);
        Assert.AreEqual(2, settings.ConsensusThreshold);
        Assert.AreEqual(0, settings.Providers.Count);
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  AiProviderSettings: Config Binding Round-Trip
    // ═══════════════════════════════════════════════════════════════════════

    [TestMethod]
    public void AiProviderSettings_ConfigBindingRoundTrip()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AiProvider:Mode"] = "consensus",
                ["AiProvider:ActiveProvider"] = "model-x",
                ["AiProvider:MaxParallelReviews"] = "3",
                ["AiProvider:ConsensusThreshold"] = "2",
                ["AiProvider:Providers:model-x:Type"] = "azure-openai",
                ["AiProvider:Providers:model-x:DisplayName"] = "Model X",
                ["AiProvider:Providers:model-x:Endpoint"] = "https://x.openai.azure.com/",
                ["AiProvider:Providers:model-x:ApiKey"] = "key-x",
                ["AiProvider:Providers:model-x:Model"] = "gpt-5",
                ["AiProvider:Providers:model-x:Enabled"] = "true",
            })
            .Build();

        var settings = config.GetSection("AiProvider").Get<AiProviderSettings>()!;

        Assert.AreEqual("consensus", settings.Mode);
        Assert.AreEqual("model-x", settings.ActiveProvider);
        Assert.AreEqual(3, settings.MaxParallelReviews);
        Assert.AreEqual(2, settings.ConsensusThreshold);
        Assert.AreEqual(1, settings.Providers.Count);
        Assert.IsTrue(settings.Providers.ContainsKey("model-x"));

        var provider = settings.Providers["model-x"];
        Assert.AreEqual("azure-openai", provider.Type);
        Assert.AreEqual("Model X", provider.DisplayName);
        Assert.AreEqual("https://x.openai.azure.com/", provider.Endpoint);
        Assert.AreEqual("key-x", provider.ApiKey);
        Assert.AreEqual("gpt-5", provider.Model);
        Assert.IsTrue(provider.Enabled);

        Console.WriteLine($"  ✓ Config binding round-trip: {settings.Mode}, {settings.Providers.Count} provider(s).");
    }
}
